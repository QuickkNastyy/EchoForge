<#
.SYNOPSIS
    Validates a staged EchoForge package before an installer is built from it.

.DESCRIPTION
    The installer consumes exactly what scripts\package.ps1 stages, and nothing else. This is the
    gate that proves the staged directory is a complete, self-contained, source-free application
    before iscc is ever run - so an incomplete stage fails here, loudly, with a list, rather than
    compiling into an installer that is missing a runtime or is quietly carrying a developer path.

    It checks:
      - package.json exists, parses, and describes a self-contained win-x64 EchoForge
      - the staged version matches the single source of truth (Directory.Build.props VersionPrefix)
      - every load-bearing file is present: the app, the self-contained .NET runtime, WPF, the
        native SQLite, the worker package, the pinned manifest, the third-party notice and licences
      - the manifest entry count agrees with what package.json recorded
      - no shipped text file names the repository it was built in
      - no signing material (.pfx/.p12/.key/.snk/.pem) leaked into the package

    Exit code 0 = valid. Non-zero = do not build an installer from this.

.PARAMETER Package
    The staged package directory. Defaults to build\package\EchoForge.
#>
[CmdletBinding()]
param(
    [string] $Package
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Package) { $Package = Join-Path $repoRoot 'build\package\EchoForge' }
$Package = [System.IO.Path]::GetFullPath($Package)

$failures = New-Object System.Collections.Generic.List[string]
function Check([bool] $condition, [string] $what) {
    if ($condition) { Write-Host "  ok    $what" -ForegroundColor Green }
    else { Write-Host "  FAIL  $what" -ForegroundColor Red; $failures.Add($what) }
}

Write-Host 'EchoForge package validation'
Write-Host "  package  $Package"
Write-Host ''

if (-not (Test-Path (Join-Path $Package 'EchoForge.App.exe'))) {
    Write-Host "  no package at $Package - run scripts\package.ps1 first" -ForegroundColor Red
    exit 2
}

# -- package.json describes what we think it does ----------------------------------------------
$layoutPath = Join-Path $Package 'package.json'
Check (Test-Path $layoutPath) 'package.json is present (staging finished)'

$stagedVersion = $null
if (Test-Path $layoutPath) {
    $layout = Get-Content $layoutPath -Raw | ConvertFrom-Json
    Check ($layout.product -eq 'EchoForge') 'package.json product is EchoForge'
    Check ($layout.runtime_identifier -eq 'win-x64') 'package.json runtime identifier is win-x64'
    Check ([bool]$layout.self_contained) 'package.json declares a self-contained build'
    Check (-not [string]::IsNullOrWhiteSpace($layout.version)) 'package.json records a version'
    $stagedVersion = $layout.version
}

# -- the version is single-sourced -------------------------------------------------------------
# Directory.Build.props VersionPrefix is the one place the version is defined. The staged
# ProductVersion may carry build metadata (e.g. "0.6.0+abc"); compare the leading dotted number.
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
[xml]$props = Get-Content $propsPath -Raw
# Directory.Build.props has several PropertyGroup elements; find the one that defines VersionPrefix.
$versionPrefix = $null
foreach ($group in $props.Project.PropertyGroup) {
    $node = $group.SelectSingleNode('VersionPrefix')
    if ($node) { $versionPrefix = $node.InnerText.Trim() }
}
if ($stagedVersion) {
    $stagedCore = ($stagedVersion -split '\+')[0]
    Check ($stagedCore -eq $versionPrefix) "staged version ($stagedCore) matches VersionPrefix ($versionPrefix)"
}

# -- every load-bearing file -------------------------------------------------------------------
$required = @(
    'EchoForge.App.exe',
    'EchoForge.App.dll',
    'package.json',
    'artifacts\manifest.json',
    'worker\echoforge_worker\__init__.py',
    'worker\requirements-production.txt',
    'third_party\NOTICE.md'
)
foreach ($relative in $required) {
    Check (Test-Path (Join-Path $Package $relative)) "present: $relative"
}

# Self-contained runtime and WPF must be in the package, not on the machine.
Check (Test-Path (Join-Path $Package 'System.Private.CoreLib.dll')) 'self-contained: the .NET runtime is in the package'
Check (Test-Path (Join-Path $Package 'PresentationFramework.dll')) 'self-contained: WPF is in the package'
Check (Test-Path (Join-Path $Package 'NAudio.Wasapi.dll')) 'NAudio is in the package'

$sqlite = @(Get-ChildItem $Package -Recurse -Filter 'e_sqlite3.dll' -File)
Check ($sqlite.Count -gt 0) 'the native SQLite the library index needs is in the package'

$licenseCount = 0
$licenseDir = Join-Path $Package 'third_party\licenses'
if (Test-Path $licenseDir) { $licenseCount = (Get-ChildItem $licenseDir -File).Count }
Check ($licenseCount -gt 20) "the retained licence texts ship ($licenseCount files)"

# -- x64 native only ---------------------------------------------------------------------------
# A self-contained win-x64 publish should carry win-x64 native assets and no other architecture's,
# because shipping arm64/x86 native blobs would be dead weight at best and a wrong-arch load at worst.
$foreignNative = @(Get-ChildItem $Package -Recurse -Directory |
    Where-Object { $_.Name -in @('win-arm64', 'win-x86', 'linux-x64', 'osx-x64', 'osx-arm64') } |
    Where-Object { (Get-ChildItem $_.FullName -File -ErrorAction SilentlyContinue).Count -gt 0 })
Check ($foreignNative.Count -eq 0) 'no foreign-architecture native runtimes are staged'
foreach ($dir in $foreignNative) { Write-Host "        foreign native: $($dir.FullName)" -ForegroundColor DarkYellow }

# -- the manifest agrees with package.json -----------------------------------------------------
if ($stagedVersion -and (Test-Path (Join-Path $Package 'artifacts\manifest.json')) -and ($layout.PSObject.Properties.Name -contains 'manifest_entries')) {
    $entries = (Get-Content (Join-Path $Package 'artifacts\manifest.json') -Raw | ConvertFrom-Json).artifacts.Count
    Check ($entries -eq $layout.manifest_entries) "manifest entry count ($entries) matches package.json ($($layout.manifest_entries))"
}

# -- no repository path leaked -----------------------------------------------------------------
# Same class of check the published smoke does: a shipped configuration file that names the build
# machine's source tree is a file that resolves by accident on the developer's disk and nowhere else.
$repoLeaks = @(
    Get-ChildItem $Package -Recurse -File -Include '*.json', '*.txt', '*.runtimeconfig.json', '*.pdb' -ErrorAction SilentlyContinue |
    Where-Object { $_.Length -lt 2MB } |
    Where-Object { (Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue) -match [regex]::Escape($repoRoot) }
)
Check ($repoLeaks.Count -eq 0) 'no shipped file names the repository it was built in'
foreach ($leak in $repoLeaks) { Write-Host "        names the repo: $($leak.FullName)" -ForegroundColor DarkYellow }

# -- no development artifacts ------------------------------------------------------------------
# The worker ships as its echoforge_worker package only. A developer virtual environment, a pytest
# cache or bytecode caches vacuumed into the package would bloat the installer and carry
# development-only dependencies (pytest, pygments) that are not part of the product.
$devArtifacts = @(Get-ChildItem $Package -Recurse -Directory -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in @('.venv', 'venv', '.pytest_cache', '__pycache__') })
Check ($devArtifacts.Count -eq 0) 'no development virtualenv or cache directories are staged'
foreach ($d in $devArtifacts) { Write-Host "        dev artifact: $($d.FullName)" -ForegroundColor Red }

# -- no signing material -----------------------------------------------------------------------
# A package must never carry a certificate or private key. This is a distribution artifact.
$secrets = @(Get-ChildItem $Package -Recurse -File -Include '*.pfx', '*.p12', '*.key', '*.snk', '*.pem' -ErrorAction SilentlyContinue)
Check ($secrets.Count -eq 0) 'no signing material (.pfx/.p12/.key/.snk/.pem) is staged in the package'
foreach ($secret in $secrets) { Write-Host "        secret-like file: $($secret.FullName)" -ForegroundColor Red }

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host '  result  VALID' -ForegroundColor Green
    exit 0
}

Write-Host '  result  INVALID' -ForegroundColor Red
foreach ($failure in $failures) { Write-Host "    - $failure" -ForegroundColor Red }
exit 1
