<#
.SYNOPSIS
    Compiles the EchoForge installer from a validated, staged package.

.DESCRIPTION
    The middle of the release pipeline, also usable on its own for an unsigned development installer.
    It does three things in order and stops at the first failure:

      1. Ensures the pinned Inno Setup compiler is staged (scripts\stage-inno.ps1).
      2. Validates the staged package (scripts\validate-package.ps1) - the installer is built from
         exactly this, so an incomplete or source-tainted package must not reach iscc.
      3. Runs iscc against packaging\inno\EchoForge.iss, passing the version read from package.json
         so the installer's version is the same single source of truth the application was built
         with, never a number typed twice.

    This produces an UNSIGNED installer. Signing is scripts\sign.ps1, orchestrated by
    scripts\release.ps1; a development build is unsigned on purpose and says so.

.PARAMETER Package
    The staged package. Defaults to build\package\EchoForge.

.PARAMETER SignInstaller
    Route the compile through a registered sign tool (release use). Requires signing configuration;
    without it the compile stays unsigned. scripts\release.ps1 sets this up.

.OUTPUTS
    The compiled installer under build\installer, and its path/size/sha256 printed at the end.
#>
[CmdletBinding()]
param(
    [string] $Package,
    [switch] $SignInstaller
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Package) { $Package = Join-Path $repoRoot 'build\package\EchoForge' }
$Package = [System.IO.Path]::GetFullPath($Package)

$iss = Join-Path $repoRoot 'packaging\inno\EchoForge.iss'
$outputDir = Join-Path $repoRoot 'build\installer'

function Step([string] $m) { Write-Host ''; Write-Host "== $m" -ForegroundColor Cyan }
function Fail([string] $m) { Write-Host ''; Write-Host "  $m" -ForegroundColor Red; exit 1 }

# -- 1. the compiler ---------------------------------------------------------------------------
Step 'Inno Setup compiler'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'stage-inno.ps1')
if ($LASTEXITCODE -ne 0) { Fail 'the Inno Setup compiler could not be staged' }

$identityPath = Join-Path $repoRoot 'build\tools\inno-tool-identity.json'
$iscc = (Get-Content $identityPath -Raw | ConvertFrom-Json).iscc
if (-not (Test-Path $iscc)) { Fail "ISCC.exe not found at $iscc" }

# -- 2. validate the package -------------------------------------------------------------------
Step 'Validate the package'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'validate-package.ps1') -Package $Package
if ($LASTEXITCODE -ne 0) { Fail 'the package did not validate; no installer was built' }

# -- 3. version, single-sourced from the package -----------------------------------------------
$layout = Get-Content (Join-Path $Package 'package.json') -Raw | ConvertFrom-Json
$version = ($layout.version -split '\+')[0]
if ([string]::IsNullOrWhiteSpace($version)) { Fail 'package.json did not record a version' }

Step "Compile the installer ($version)"
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

$isccArgs = @(
    "/DSourceDir=$Package",
    "/DAppVersion=$version",
    "/O$outputDir"
)
if ($SignInstaller) { $isccArgs += '/DSignInstaller' }
$isccArgs += $iss

& $iscc @isccArgs
if ($LASTEXITCODE -ne 0) { Fail 'the installer did not compile' }

$installer = Join-Path $outputDir "EchoForge-$version-win-x64.exe"
if (-not (Test-Path $installer)) { Fail "the expected installer was not produced at $installer" }

$size = (Get-Item $installer).Length
$hash = (Get-FileHash $installer -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host ''
Write-Host "  installer  $installer" -ForegroundColor Green
Write-Host ("  size       {0:N0} bytes ({1:N1} MB)" -f $size, ($size / 1MB))
Write-Host "  sha256     $hash"
if (-not $SignInstaller) {
    Write-Host '  signing    UNSIGNED (development build)' -ForegroundColor Yellow
}

exit 0
