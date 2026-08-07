<#
.SYNOPSIS
    Produces a self-contained EchoForge, laid out for the installer.

.DESCRIPTION
    Restores, gates, builds, publishes and stages. The output is a directory that can be copied to
    a machine with no .NET, no Python and no developer tools on it, and run.

    Self-contained win-x64, not trimmed and not single-file. Both of those exclusions are measured
    rather than ideological: WPF loads XAML by reflection, the SQLite provider resolves its native
    library by name, and NAudio's WASAPI interop is COM - a trimmed build of this application fails
    at the first window rather than at build time. Single-file would bundle the managed assemblies
    and change nothing about the parts that matter, because the app-local Python, the worker package
    and llama.cpp are separate files and separate processes by design.

    The artifact gate runs before anything is published. A build that shipped a manifest which had
    not been validated would ship the one file that decides what the application is allowed to
    download.

.PARAMETER Output
    Where to stage. Defaults to build\package beside the repository.

.PARAMETER Configuration
    Defaults to Release.

.PARAMETER SkipTests
    Publish without running the test suite. For iterating on the layout only; a release build must
    never use it.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package.ps1
#>
[CmdletBinding()]
param(
    [string] $Output,
    [string] $Configuration = 'Release',
    [switch] $SkipTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot 'EchoForge.slnx'
$appProject = Join-Path $repoRoot 'src\EchoForge.App\EchoForge.App.csproj'

if (-not $Output) { $Output = Join-Path $repoRoot 'build\package' }

$stageRoot = Join-Path $Output 'EchoForge'
$runtimeId = 'win-x64'

function Step([string] $message) {
    Write-Host ''
    Write-Host "== $message" -ForegroundColor Cyan
}

function Fail([string] $message) {
    Write-Host ''
    Write-Host "  $message" -ForegroundColor Red
    exit 1
}

Step 'Artifact gate'

# Before anything else. The manifest is the only list of things the application may download, and
# publishing an unvalidated one would ship exactly the file that decides that.
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'verify-models.ps1')
if ($LASTEXITCODE -ne 0) { Fail 'the artifact manifest did not pass its gate; nothing was published' }

Step 'Restore'
& dotnet restore $solution
if ($LASTEXITCODE -ne 0) { Fail 'restore failed' }

Step "Build ($Configuration)"
& dotnet build $solution -c $Configuration --no-restore --warnaserror
if ($LASTEXITCODE -ne 0) { Fail 'build failed' }

if (-not $SkipTests) {
    Step 'Tests'
    & dotnet test $solution -c $Configuration --no-restore --no-build
    if ($LASTEXITCODE -ne 0) { Fail 'tests failed; nothing was published' }
}
else {
    Write-Host ''
    Write-Host '  -SkipTests: the suite was not run. This is not a release build.' -ForegroundColor Yellow
}

Step 'Publish'

if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null

& dotnet publish $appProject `
    -c $Configuration `
    -r $runtimeId `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:PublishTrimmed=false `
    -p:PublishReadyToRun=false `
    -o $stageRoot

if ($LASTEXITCODE -ne 0) { Fail 'publish failed' }

Step 'Stage'

# The publish output already carries the worker package, the pinned manifest and the licence
# texts, because the application project copies them as content. Everything below is a check that
# it did, rather than a second copy that could disagree with the first.
$required = @(
    'EchoForge.App.exe',
    'EchoForge.App.dll',
    'artifacts\manifest.json',
    'worker\echoforge_worker\__init__.py',
    'worker\requirements-production.txt',
    'third_party\NOTICE.md'
)

$missing = @()
foreach ($relative in $required) {
    if (-not (Test-Path (Join-Path $stageRoot $relative))) { $missing += $relative }
}

# The runtime has to be in the publish output, not on the machine.
if (-not (Test-Path (Join-Path $stageRoot 'System.Private.CoreLib.dll'))) {
    $missing += 'System.Private.CoreLib.dll (the self-contained .NET runtime)'
}

if (-not (Test-Path (Join-Path $stageRoot 'PresentationFramework.dll'))) {
    $missing += 'PresentationFramework.dll (WPF)'
}

if (-not (Test-Path (Join-Path $stageRoot 'runtimes\win-x64\native\e_sqlite3.dll'))) {
    if (-not (Test-Path (Join-Path $stageRoot 'e_sqlite3.dll'))) {
        $missing += 'e_sqlite3.dll (the native SQLite the library index needs)'
    }
}

if ($missing.Count -gt 0) {
    foreach ($item in $missing) { Write-Host "  MISSING  $item" -ForegroundColor Red }
    Fail 'the published layout is incomplete'
}

# A file the installer and the smoke test can both read, so neither has to guess the version.
$version = (Get-Item (Join-Path $stageRoot 'EchoForge.App.dll')).VersionInfo.ProductVersion
$manifestEntries = (Get-Content (Join-Path $repoRoot 'artifacts\manifest.json') -Raw | ConvertFrom-Json).artifacts.Count

$layout = [ordered]@{
    product           = 'EchoForge'
    version           = $version
    runtime_identifier = $runtimeId
    self_contained    = $true
    single_file       = $false
    trimmed           = $false
    manifest_entries  = $manifestEntries
    entry_point       = 'EchoForge.App.exe'
}

$layoutPath = Join-Path $stageRoot 'package.json'
$layout | ConvertTo-Json -Depth 4 | Set-Content -Path $layoutPath -Encoding utf8

$size = (Get-ChildItem $stageRoot -Recurse -File | Measure-Object -Property Length -Sum).Sum
$files = (Get-ChildItem $stageRoot -Recurse -File | Measure-Object).Count

Write-Host ''
Write-Host "  staged    $stageRoot" -ForegroundColor Green
Write-Host "  version   $version"
Write-Host "  files     $files"
Write-Host ("  size      {0:N0} MB" -f ($size / 1MB))
Write-Host ''
Write-Host '  The installer inputs are in packaging\inno. Building and signing the installer, and'
Write-Host '  qualifying it on a clean Windows 11 virtual machine, is Phase 6 Pass 2.' -ForegroundColor DarkGray

exit 0
