<#
.SYNOPSIS
    Runs the real faster-whisper backend once, end to end, against a synthetic recording.

.DESCRIPTION
    This is the one thing the injected-fixture tests cannot prove: that the pinned model loads
    from the verified directory, that CTranslate2 runs on this machine, and that a real
    recogniser's output survives rebasing, gap handling, and de-duplication into a valid
    transcript.

    It downloads nothing. Every artifact must already be installed and verified, and the model
    directory is assembled from those verified files. If anything is missing the script says
    exactly what and stops - it never falls back to fetching it.

.PARAMETER ModelRoot
    Defaults to %LOCALAPPDATA%\EchoForge\models.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\smoke-production-backend.ps1
#>
[CmdletBinding()]
param(
    [string] $ModelRoot,
    [string] $RuntimeRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ModelRoot) { $ModelRoot = Join-Path $env:LOCALAPPDATA 'EchoForge\models' }
if (-not $RuntimeRoot) { $RuntimeRoot = Join-Path $env:LOCALAPPDATA 'EchoForge\runtime' }

$python = Join-Path $RuntimeRoot 'python-production\Scripts\python.exe'
if (-not (Test-Path $python)) {
    Write-Host "The production environment is not installed at $python." -ForegroundColor Red
    Write-Host 'Run scripts\install-worker-runtime.ps1 first.' -ForegroundColor Red
    exit 2
}

# Assemble the model directory from verified artifacts, exactly as the app does.
$manifest = Get-Content (Join-Path $repoRoot 'artifacts\manifest.json') -Raw | ConvertFrom-Json
$modelFiles = @($manifest.artifacts | Where-Object { $_.kind -eq 'speech-model' })

$revision = $modelFiles[0].revision
$staged = Join-Path $ModelRoot "staged\$revision"
New-Item -ItemType Directory -Force -Path $staged | Out-Null

foreach ($artifact in $modelFiles) {
    $source = Join-Path $ModelRoot (Join-Path $artifact.artifact_id (Join-Path $artifact.revision $artifact.filename))

    if (-not (Test-Path $source)) {
        Write-Host "  MISSING  $($artifact.artifact_id) ($($artifact.filename))" -ForegroundColor Red
        Write-Host '  The model is not installed. Nothing was downloaded; nothing was faked.' -ForegroundColor Red
        exit 1
    }

    $length = (Get-Item $source).Length
    if ($length -ne [int64]$artifact.size_bytes) {
        Write-Host "  INCOMPLETE  $($artifact.filename): $length of $($artifact.size_bytes) bytes" -ForegroundColor Red
        exit 1
    }

    Write-Host "  verifying $($artifact.filename)..." -ForegroundColor DarkGray
    $hash = (Get-FileHash $source -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne [string]$artifact.sha256) {
        Write-Host "  DIGEST MISMATCH  $($artifact.filename)" -ForegroundColor Red
        exit 1
    }

    $destination = Join-Path $staged $artifact.filename
    if (-not (Test-Path $destination) -or (Get-Item $destination).Length -ne $length) {
        Copy-Item $source -Destination $destination -Force
    }
}

Write-Host "  model staged at $staged" -ForegroundColor Green
Write-Host ''

$env:PYTHONPATH = Join-Path $repoRoot 'worker'
$env:ECHOFORGE_SMOKE_MODEL = $staged

& $python (Join-Path $PSScriptRoot 'smoke_production_backend.py')
exit $LASTEXITCODE
