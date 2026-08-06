<#
.SYNOPSIS
    Installs the production worker environment from verified artifacts, offline.

.DESCRIPTION
    Builds a wheelhouse from the artifacts the manifest pins, re-verifying every file's length
    and SHA-256 first, then creates the production virtual environment and installs from that
    wheelhouse with the package index switched off.

    The index is off on purpose. Once the wheels are verified there is nothing left to fetch,
    and an installer that could still reach the network could still install something the
    manifest never vouched for. This is also what makes the installation reproducible on a
    machine with no connection at all.

.PARAMETER ModelRoot
    Where verified artifacts live. Defaults to %LOCALAPPDATA%\EchoForge\models.

.PARAMETER RuntimeRoot
    Where the production environment is created. Defaults to %LOCALAPPDATA%\EchoForge\runtime.

.PARAMETER WhatIf
    Report what is missing and stop without creating anything.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install-worker-runtime.ps1
#>
[CmdletBinding()]
param(
    [string] $ModelRoot,
    [string] $RuntimeRoot,
    [switch] $WhatIf
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot 'artifacts\manifest.json'
$requirements = Join-Path $repoRoot 'worker\requirements-production.txt'

if (-not $ModelRoot) { $ModelRoot = Join-Path $env:LOCALAPPDATA 'EchoForge\models' }
if (-not $RuntimeRoot) { $RuntimeRoot = Join-Path $env:LOCALAPPDATA 'EchoForge\runtime' }

$wheelhouse = Join-Path $RuntimeRoot 'wheelhouse'
$environment = Join-Path $RuntimeRoot 'python-production'

if (-not (Get-Command uv -ErrorAction SilentlyContinue)) {
    Write-Host 'uv was not found on PATH. Install it from https://docs.astral.sh/uv/' -ForegroundColor Red
    exit 2
}

$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$runtimeArtifacts = @($manifest.artifacts | Where-Object { ([string]$_.artifact_id).StartsWith('runtime.') })

Write-Host "Verifying $($runtimeArtifacts.Count) pinned wheels..." -ForegroundColor Cyan

$missing = New-Object System.Collections.Generic.List[string]
$verified = @()

foreach ($artifact in $runtimeArtifacts) {
    $path = Join-Path $ModelRoot (Join-Path $artifact.artifact_id (Join-Path $artifact.revision $artifact.filename))

    if (-not (Test-Path $path)) {
        $missing.Add("$($artifact.artifact_id): not downloaded")
        continue
    }

    if ((Get-Item $path).Length -ne [int64]$artifact.size_bytes) {
        $missing.Add("$($artifact.artifact_id): wrong length")
        continue
    }

    $hash = (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne [string]$artifact.sha256) {
        $missing.Add("$($artifact.artifact_id): digest mismatch")
        continue
    }

    $verified += $path
}

if ($missing.Count -gt 0) {
    Write-Host ''
    foreach ($problem in $missing) { Write-Host "  MISSING  $problem" -ForegroundColor Red }
    Write-Host ''
    Write-Host '  Nothing was installed. Fetch the artifacts first (the app can, or re-run' -ForegroundColor Red
    Write-Host '  scripts\lock-worker-runtime.ps1), then run this again.' -ForegroundColor Red
    exit 1
}

Write-Host "  all $($verified.Count) verified" -ForegroundColor Green

if ($WhatIf) {
    Write-Host '  -WhatIf: stopping before creating anything.' -ForegroundColor Yellow
    exit 0
}

# A flat directory, because --find-links wants one place to look and the artifact store is
# laid out by artifact and revision rather than by filename.
if (Test-Path $wheelhouse) { Remove-Item $wheelhouse -Recurse -Force }
New-Item -ItemType Directory -Force -Path $wheelhouse | Out-Null
foreach ($path in $verified) { Copy-Item $path -Destination $wheelhouse }

Write-Host "Creating the production environment at $environment" -ForegroundColor Cyan
& uv venv --python 3.12 $environment
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# --no-index: everything needed is already verified and local. An installer that could still
# reach out could still install something the manifest never vouched for.
& uv pip install --python $environment --no-index --find-links $wheelhouse --requirement $requirements
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$python = Join-Path $environment 'Scripts\python.exe'
Write-Host ''
Write-Host 'Checking the installed stack imports...' -ForegroundColor Cyan
& $python -c "import faster_whisper, ctranslate2, av, onnxruntime; print('faster-whisper', faster_whisper.__version__); print('ctranslate2', ctranslate2.__version__); print('CUDA devices visible to CTranslate2:', ctranslate2.get_cuda_device_count())"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host "  production interpreter: $python" -ForegroundColor Green
Write-Host '  Set ECHOFORGE_PYTHON to that path to have EchoForge use it.' -ForegroundColor DarkGray
exit 0
