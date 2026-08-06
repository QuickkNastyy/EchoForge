<#
.SYNOPSIS
    Validates the pinned artifact manifest, and optionally the downloaded files.

.DESCRIPTION
    The artifact gate. Nothing may be downloaded that is not listed in artifacts/manifest.json,
    and every entry must name an immutable revision. A branch name, a mutable model alias, a
    glob, or a missing hash fails the build rather than producing a warning, because the whole
    point of pinning is that it cannot be quietly bypassed.

    Run with -Downloaded to also verify files already on disk: size first, then SHA-256. A
    mismatch is reported with the expected and actual digests and leaves the file untouched.

.PARAMETER ManifestPath
    Defaults to artifacts/manifest.json beside this script's repository root.

.PARAMETER ModelRoot
    Where downloaded artifacts live. Defaults to %LOCALAPPDATA%\EchoForge\models.

.PARAMETER Downloaded
    Also verify the bytes of artifacts that are present on disk.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-models.ps1
#>
[CmdletBinding()]
param(
    [string] $ManifestPath,
    [string] $ModelRoot,
    [switch] $Downloaded
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ManifestPath) { $ManifestPath = Join-Path $repoRoot 'artifacts\manifest.json' }
if (-not $ModelRoot) { $ModelRoot = Join-Path $env:LOCALAPPDATA 'EchoForge\models' }

$problems = New-Object System.Collections.Generic.List[string]
function Add-Problem([string] $message) { $problems.Add($message) }

if (-not (Test-Path $ManifestPath)) {
    Write-Host "artifact manifest not found: $ManifestPath" -ForegroundColor Red
    exit 2
}

try {
    $manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
}
catch {
    Write-Host "artifact manifest is not valid JSON: $($_.Exception.Message)" -ForegroundColor Red
    exit 2
}

if ($manifest.schema_version -ne 1) {
    Add-Problem "schema_version is $($manifest.schema_version); this script understands version 1"
}

# Revisions that are not revisions. Anything matching moves, so pinning to it pins nothing.
$mutableRefs = @('main', 'master', 'latest', 'head', 'dev', 'develop', 'trunk', 'stable', 'newest')
$seenIds = New-Object System.Collections.Generic.HashSet[string]
$verifiedCount = 0

$artifacts = @()
if ($manifest.PSObject.Properties.Name -contains 'artifacts' -and $null -ne $manifest.artifacts) {
    $artifacts = @($manifest.artifacts)
}

foreach ($artifact in $artifacts) {
    $id = if ($artifact.PSObject.Properties.Name -contains 'artifact_id') { $artifact.artifact_id } else { '<no artifact_id>' }

    foreach ($field in @('artifact_id', 'kind', 'repository', 'url', 'revision', 'filename',
                         'size_bytes', 'sha256', 'license', 'license_file',
                         'runtime_version', 'profiles', 'verified_utc')) {
        if (($artifact.PSObject.Properties.Name -notcontains $field) -or
            ($null -eq $artifact.$field) -or
            ("$($artifact.$field)".Trim().Length -eq 0)) {
            Add-Problem "$id is missing required field '$field'"
        }
    }

    if ($artifact.PSObject.Properties.Name -contains 'artifact_id') {
        if (-not $seenIds.Add([string]$artifact.artifact_id)) {
            Add-Problem "$id appears more than once"
        }
    }

    if ($artifact.PSObject.Properties.Name -contains 'revision' -and $artifact.revision) {
        $revision = [string]$artifact.revision
        if ($mutableRefs -contains $revision.ToLowerInvariant()) {
            Add-Problem "$id pins revision '$revision', which is a moving reference, not an immutable one"
        }
        elseif ($revision.Length -lt 7) {
            Add-Problem "$id has revision '$revision', which is too short to be an immutable commit or tag"
        }
    }

    if ($artifact.PSObject.Properties.Name -contains 'filename' -and $artifact.filename) {
        $filename = [string]$artifact.filename
        if ($filename -match '[*?]') {
            Add-Problem "$id has filename '$filename', which is a glob rather than an exact file"
        }
    }

    # The download URL is part of the gate. A plain-HTTP or templated URL would let the bytes
    # come from somewhere the manifest cannot vouch for.
    if ($artifact.PSObject.Properties.Name -contains 'url' -and $artifact.url) {
        $url = [string]$artifact.url
        if ($url -notmatch '^https://') {
            Add-Problem "$id has a url that is not https"
        }
        elseif ($url -match '[{}*?]|\s') {
            Add-Problem "$id has a url containing a placeholder or whitespace rather than an exact location"
        }
        elseif ($artifact.PSObject.Properties.Name -contains 'revision' -and $artifact.revision -and
                $url -notlike "*$($artifact.revision)*" -and $url -notlike '*files.pythonhosted.org*') {
            # A content-addressed package-index URL is immutable by construction; anything else
            # has to name the revision it was pinned to.
            Add-Problem "$id has a url that does not name its pinned revision"
        }
    }

    # Retained licence text is collected at pin time, not reconstructed at release.
    if ($artifact.PSObject.Properties.Name -contains 'license_file' -and $artifact.license_file) {
        $licensePath = Join-Path $repoRoot ([string]$artifact.license_file)
        if (-not (Test-Path $licensePath)) {
            Add-Problem "$id names license_file '$($artifact.license_file)', which is not in the repository"
        }
    }

    if ($artifact.PSObject.Properties.Name -contains 'sha256' -and $artifact.sha256) {
        if ([string]$artifact.sha256 -notmatch '^[0-9a-f]{64}$') {
            Add-Problem "$id has a sha256 that is not 64 lower-case hex characters"
        }
    }

    if ($artifact.PSObject.Properties.Name -contains 'size_bytes') {
        if ([int64]$artifact.size_bytes -le 0) {
            Add-Problem "$id has a non-positive size_bytes"
        }
    }

    if (-not $Downloaded) { continue }

    $path = Join-Path $ModelRoot (Join-Path $artifact.artifact_id (Join-Path $artifact.revision $artifact.filename))
    if (-not (Test-Path $path)) {
        Write-Host "  $($artifact.artifact_id): not downloaded" -ForegroundColor DarkGray
        continue
    }

    $actualSize = (Get-Item $path).Length
    if ($actualSize -ne [int64]$artifact.size_bytes) {
        Add-Problem "$id is $actualSize bytes on disk but the manifest expects $($artifact.size_bytes)"
        continue
    }

    $actualHash = (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -ne [string]$artifact.sha256) {
        Add-Problem "$id hash mismatch: expected $($artifact.sha256), found $actualHash"
        continue
    }

    $verifiedCount++
    Write-Host "  $($artifact.artifact_id): verified" -ForegroundColor Green
}

Write-Host ''
Write-Host "artifact manifest: $ManifestPath"
Write-Host "  entries    $($artifacts.Count)"
if ($Downloaded) { Write-Host "  verified   $verifiedCount" }

if ($problems.Count -gt 0) {
    Write-Host ''
    foreach ($problem in $problems) { Write-Host "  FAIL  $problem" -ForegroundColor Red }
    Write-Host ''
    Write-Host "  result  FAIL" -ForegroundColor Red
    exit 1
}

if ($artifacts.Count -eq 0) {
    Write-Host ''
    Write-Host "  No artifacts are pinned yet, so no production model may be downloaded." -ForegroundColor Yellow
}

Write-Host ''
Write-Host "  result  PASS" -ForegroundColor Green
exit 0
