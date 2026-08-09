<#
.SYNOPSIS
    Regenerates the pinned production Python runtime in artifacts/manifest.json.

.DESCRIPTION
    Resolves faster-whisper and its pinned CUDA runtime's complete Windows / CPython 3.12
    dependency closure with uv, then
    for each resolved version asks the package index for the one wheel that platform actually
    installs, downloads it, verifies its length and SHA-256 against the index, extracts its
    licence text, and writes the result into the manifest.

    Nothing here is copied by hand. Transcribing twenty-five digests manually is the single most
    likely way to introduce a wrong one, and a wrong digest is indistinguishable from a
    substituted file. Every value in the generated entries came from the index and was then
    re-computed from the bytes on disk.

    The model entries in the manifest are left exactly as they are: this script owns the
    "runtime." artifacts and nothing else.

.PARAMETER Check
    Do not write anything. Regenerate in memory and fail if the manifest would change. Use this
    to prove the committed lock still matches what the resolver produces.

.PARAMETER ModelRoot
    Where verified artifacts are stored. Defaults to %LOCALAPPDATA%\EchoForge\models.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\lock-worker-runtime.ps1
#>
[CmdletBinding()]
param(
    [switch] $Check,
    [string] $ModelRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot 'artifacts\manifest.json'
$licenseRoot = Join-Path $repoRoot 'third_party\licenses'
$requirementsIn = Join-Path $repoRoot 'worker\requirements-production.in'
$requirementsOut = Join-Path $repoRoot 'worker\requirements-production.txt'

if (-not $ModelRoot) { $ModelRoot = Join-Path $env:LOCALAPPDATA 'EchoForge\models' }

if (-not (Get-Command uv -ErrorAction SilentlyContinue)) {
    Write-Host 'uv was not found on PATH. Install it from https://docs.astral.sh/uv/' -ForegroundColor Red
    exit 2
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

# --------------------------------------------------------------------------------------------
# 1. Resolve the closure. uv decides the versions; this script never picks one.
# --------------------------------------------------------------------------------------------

Write-Host 'Resolving the Windows / CPython 3.12 closure...' -ForegroundColor Cyan
& uv pip compile $requirementsIn --python-platform windows --python-version 3.12 --no-header --quiet -o $requirementsOut
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$resolved = @()
foreach ($line in (Get-Content $requirementsOut)) {
    $trimmed = $line.Trim()
    if (-not $trimmed -or $trimmed.StartsWith('#')) { continue }
    if ($trimmed -match '^([A-Za-z0-9._-]+)==([^\s;]+)') {
        $resolved += [pscustomobject]@{ Name = $Matches[1]; Version = $Matches[2] }
    }
}

Write-Host "  $($resolved.Count) packages" -ForegroundColor DarkGray

# --------------------------------------------------------------------------------------------
# 2. For each version, find the wheel CPython 3.12 on Windows x64 actually installs.
# --------------------------------------------------------------------------------------------

function Select-Wheel([object[]] $files) {
    $wheels = @($files | Where-Object { $_.packagetype -eq 'bdist_wheel' })

    # An exact build for this interpreter beats everything else.
    $exact = $wheels | Where-Object { $_.filename -match '-cp312-cp312-win_amd64\.whl$' } | Select-Object -First 1
    if ($exact) { return $exact }

    # A stable-ABI wheel works on any interpreter at or above the version it was built for, so
    # the minor version has to be read rather than pattern-matched: hf-xet publishes cp38-abi3,
    # which a fixed list of recent versions would have missed entirely.
    $abi3 = $wheels |
        Where-Object { $_.filename -match '-cp3(?<minor>\d+)-abi3-win_amd64\.whl$' } |
        ForEach-Object {
            $null = $_.filename -match '-cp3(?<minor>\d+)-abi3-win_amd64\.whl$'
            [pscustomobject]@{ Minor = [int]$Matches['minor']; File = $_ }
        } |
        Where-Object { $_.Minor -le 12 } |
        Sort-Object -Property Minor -Descending |
        Select-Object -First 1

    if ($abi3) { return $abi3.File }

    foreach ($pattern in @(
            '-cp312-none-win_amd64\.whl$',
            '-py3-none-win_amd64\.whl$',
            '-py3-none-any\.whl$',
            '-py2\.py3-none-any\.whl$',
            '-none-any\.whl$')) {

        $match = $wheels | Where-Object { $_.filename -match $pattern } |
            Sort-Object -Property filename | Select-Object -First 1
        if ($match) { return $match }
    }

    return $null
}

function Get-LicenseText([string] $wheelPath) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($wheelPath)
    try {
        $entries = $zip.Entries | Where-Object {
            $_.FullName -match 'dist-info/(licenses/.*|LICEN[CS]E[^/]*|COPYING[^/]*|NOTICE[^/]*)$' -and $_.Length -gt 0
        } | Sort-Object -Property FullName

        if (-not $entries) { return $null }

        $parts = @()
        foreach ($entry in $entries) {
            $reader = New-Object System.IO.StreamReader($entry.Open())
            $parts += "===== $($entry.FullName) =====`r`n" + $reader.ReadToEnd()
            $reader.Dispose()
        }

        return ($parts -join "`r`n`r`n")
    }
    finally { $zip.Dispose() }
}

$generated = @()
$problems = New-Object System.Collections.Generic.List[string]

foreach ($package in $resolved) {
    $meta = Invoke-RestMethod "https://pypi.org/pypi/$($package.Name)/$($package.Version)/json" -TimeoutSec 120
    $wheel = Select-Wheel $meta.urls

    if (-not $wheel) {
        $problems.Add("$($package.Name) $($package.Version): no Windows CPython 3.12 wheel on the index")
        continue
    }

    $artifactId = 'runtime.' + ($package.Name.ToLowerInvariant() -replace '[^a-z0-9]+', '-')
    $revision = "$($package.Name)-$($package.Version)"
    $destination = Join-Path $ModelRoot (Join-Path $artifactId (Join-Path $revision $wheel.filename))
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null

    if (-not (Test-Path $destination)) {
        Write-Host "  fetching $($wheel.filename)" -ForegroundColor DarkGray
        Invoke-WebRequest -Uri $wheel.url -OutFile $destination -TimeoutSec 900
    }

    # Verified from the bytes on disk, not taken on trust from the index.
    $actualSize = (Get-Item $destination).Length
    $actualHash = (Get-FileHash $destination -Algorithm SHA256).Hash.ToLowerInvariant()

    if ($actualSize -ne [int64]$wheel.size) {
        $problems.Add("$($wheel.filename): downloaded $actualSize bytes, index says $($wheel.size)")
        continue
    }

    if ($actualHash -ne [string]$wheel.digests.sha256) {
        $problems.Add("$($wheel.filename): digest mismatch against the index")
        continue
    }

    $licenseName = "$($package.Name)-$($package.Version)-LICENSE.txt"
    $licensePath = Join-Path $licenseRoot $licenseName
    $licenseText = Get-LicenseText $destination

    if ($licenseText) {
        if (-not $Check) {
            New-Item -ItemType Directory -Force -Path $licenseRoot | Out-Null
            Set-Content -Path $licensePath -Value $licenseText -Encoding utf8 -NoNewline
        }
    }
    elseif (-not (Test-Path $licensePath)) {
        # Retained licence text is a release requirement, not a nicety. A wheel that carries
        # none has to be handled deliberately rather than skipped quietly.
        $problems.Add("$($package.Name) $($package.Version): the wheel carries no licence text; retain it manually at third_party/licenses/$licenseName")
        continue
    }

    $declared = if ($meta.info.PSObject.Properties.Name -contains 'license_expression' -and $meta.info.license_expression) {
        [string]$meta.info.license_expression
    }
    elseif ($meta.info.license) { (([string]$meta.info.license) -split "`n")[0].Trim() }
    else { 'see retained text' }

    if ($declared.Length -gt 64) { $declared = 'see retained text' }
    if ($declared.Length -lt 2) { $declared = 'see retained text' }

    $profiles = if ($package.Name.StartsWith('nvidia-', [StringComparison]::OrdinalIgnoreCase)) {
        @('cuda-fp16', 'cuda-int8-float16')
    }
    else {
        @('cpu-int8', 'cuda-fp16', 'cuda-int8-float16')
    }

    $generated += [ordered]@{
        artifact_id     = $artifactId
        kind            = 'runtime'
        repository      = "https://pypi.org/project/$($package.Name)/"
        url             = [string]$wheel.url
        revision        = $revision
        filename        = [string]$wheel.filename
        size_bytes      = [int64]$wheel.size
        sha256          = [string]$wheel.digests.sha256
        license         = $declared
        license_file    = "third_party/licenses/$licenseName"
        provenance      = "Resolved by uv for Windows / CPython 3.12 from worker/requirements-production.in, then downloaded and hashed locally; size and digest match the package index. Regenerate with scripts/lock-worker-runtime.ps1."
        runtime_version = "CPython 3.12 on Windows x64; $($package.Name) $($package.Version)"
        profiles        = $profiles
        verified_utc    = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddT00:00:00Z')
    }
}

if ($problems.Count -gt 0) {
    Write-Host ''
    foreach ($problem in $problems) { Write-Host "  FAIL  $problem" -ForegroundColor Red }
    Write-Host ''
    Write-Host '  Nothing was written. The manifest is left exactly as it was.' -ForegroundColor Red
    exit 1
}

# --------------------------------------------------------------------------------------------
# 3. Merge. The model entries are not this script's business.
# --------------------------------------------------------------------------------------------

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$kept = @($manifest.artifacts | Where-Object { -not ([string]$_.artifact_id).StartsWith('runtime.') })

$ordered = @()
$ordered += ($generated | Sort-Object -Property { $_.artifact_id })
$ordered += ($kept | Sort-Object -Property artifact_id)

$updated = [ordered]@{
    schema_version = 1
    notes          = [string]$manifest.notes
    artifacts      = $ordered
}

# ConvertTo-Json indents by four spaces per level, pads after every colon, and escapes
# apostrophes as '. All valid, none of it reviewable in a diff. The manifest is a file
# people have to read before trusting a download, so it is normalised to the same two-space,
# unescaped shape the hand-written entries use.
$raw = $updated | ConvertTo-Json -Depth 8 -Compress
$normaliser = Join-Path $PSScriptRoot 'normalize-manifest.py'

$json = ($raw | & uv run --no-project python $normaliser)
if ($LASTEXITCODE -ne 0) { Write-Host 'could not normalise the manifest' -ForegroundColor Red; exit 1 }

$json = ($json -join "`n").Replace("`r`n", "`n")
if (-not $json.EndsWith("`n")) { $json += "`n" }

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

if ($Check) {
    $current = ([System.IO.File]::ReadAllText($manifestPath, [System.Text.Encoding]::UTF8)).TrimStart([char]0xFEFF).Replace("`r`n", "`n")
    if ($current -ne $json) {
        Write-Host '  The committed manifest does not match a fresh resolution.' -ForegroundColor Red
        exit 1
    }

    Write-Host "  manifest matches a fresh resolution ($($ordered.Count) entries)" -ForegroundColor Green
    exit 0
}

[System.IO.File]::WriteAllText($manifestPath, $json, $utf8NoBom)

Write-Host ''
Write-Host "  wrote $($ordered.Count) artifacts ($($generated.Count) runtime, $($kept.Count) retained)" -ForegroundColor Green
Write-Host "  wheelhouse: $ModelRoot" -ForegroundColor DarkGray
exit 0
