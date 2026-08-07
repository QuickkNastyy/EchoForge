<#
.SYNOPSIS
    Confirms a machine is a clean test environment, then runs the published smoke test on it.

.DESCRIPTION
    The harness for the clean Windows 11 test. Running it is Phase 6 Pass 2; it exists now so the
    thing being run is written and reviewed rather than improvised on the day.

    Its first job is to prove the machine is actually clean. A "clean VM test" performed on a
    machine that happens to have a Python, an SDK or a CUDA toolkit on it proves nothing at all,
    and the failure mode is silent: everything works, for the wrong reason. So this refuses to
    report a pass if it finds any of them, and says which.

.PARAMETER Package
    The staged package, copied onto the machine. Defaults to a sibling EchoForge directory.

.PARAMETER AllowDirty
    Report what is present and carry on anyway. For a developer machine, never for the VM run.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File vm-precheck.ps1 -Package .\EchoForge
#>
[CmdletBinding()]
param(
    [string] $Package,
    [switch] $AllowDirty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $Package) { $Package = Join-Path $PSScriptRoot 'EchoForge' }

$dirty = New-Object System.Collections.Generic.List[string]
$notes = New-Object System.Collections.Generic.List[string]

function Present([string] $command) {
    return $null -ne (Get-Command $command -ErrorAction SilentlyContinue)
}

Write-Host 'EchoForge clean-environment precheck'
Write-Host ''

# -- what the machine must NOT have -------------------------------------------------------------

Write-Host '== The machine should not have these'

if (Present 'dotnet') {
    $sdks = & dotnet --list-sdks 2>$null
    if ($sdks) { $dirty.Add('a .NET SDK is installed') }
    else { $notes.Add('the dotnet host is present with no SDK, which a self-contained app does not use') }
}
else { Write-Host '  ok    no dotnet on PATH' -ForegroundColor Green }

foreach ($python in @('python', 'python3', 'py')) {
    if (Present $python) { $dirty.Add("a system Python is on PATH ($python)") }
}
if (-not ($dirty | Where-Object { $_ -like '*system Python*' })) {
    Write-Host '  ok    no system Python on PATH' -ForegroundColor Green
}

if (Present 'ollama') { $dirty.Add('Ollama is installed') }
else { Write-Host '  ok    no Ollama' -ForegroundColor Green }

if (Present 'nvcc') { $dirty.Add('a CUDA toolkit is installed') }
else { Write-Host '  ok    no CUDA toolkit (the driver alone is what EchoForge needs)' -ForegroundColor Green }

if (Present 'uv') { $dirty.Add('uv is installed') }

$echoforgeData = Join-Path $env:LOCALAPPDATA 'EchoForge'
if (Test-Path $echoforgeData) {
    $dirty.Add("an EchoForge data directory already exists at $echoforgeData")
}
else { Write-Host '  ok    no existing EchoForge data' -ForegroundColor Green }

# -- what it should have ------------------------------------------------------------------------

Write-Host ''
Write-Host '== The machine should have these'

$os = Get-CimInstance Win32_OperatingSystem
Write-Host "  note  $($os.Caption) build $($os.BuildNumber)"

$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)

if ($isAdmin) {
    $notes.Add('this shell is elevated; the installer must also be tested as a standard user')
}
else { Write-Host '  ok    running as a standard user' -ForegroundColor Green }

$free = (Get-PSDrive -Name ($env:SystemDrive.TrimEnd(':'))).Free
Write-Host ("  note  {0:N1} GB free on {1}" -f ($free / 1GB), $env:SystemDrive)
if ($free -lt 40GB) { $notes.Add('under 40 GB free; the full model set needs roughly 30 GB') }

$gpu = @(Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name)
foreach ($name in $gpu) { Write-Host "  note  adapter: $name" }

# -- verdict ------------------------------------------------------------------------------------

Write-Host ''

foreach ($note in $notes) { Write-Host "  note  $note" -ForegroundColor DarkGray }

if ($dirty.Count -gt 0) {
    Write-Host ''
    foreach ($item in $dirty) { Write-Host "  DIRTY  $item" -ForegroundColor Yellow }

    if (-not $AllowDirty) {
        Write-Host ''
        Write-Host '  This is not a clean environment, so a pass here would prove nothing.' -ForegroundColor Red
        Write-Host '  Use a fresh Windows 11 VM, or pass -AllowDirty to run anyway and read the' -ForegroundColor Red
        Write-Host '  result as informational only.' -ForegroundColor Red
        exit 2
    }

    Write-Host ''
    Write-Host '  -AllowDirty: continuing. This run is informational, not the release test.' -ForegroundColor Yellow
}

Write-Host ''
Write-Host '== Published smoke test'

$smoke = Join-Path $PSScriptRoot 'smoke-published.ps1'
if (-not (Test-Path $smoke)) {
    Write-Host "  smoke-published.ps1 was not copied alongside this script" -ForegroundColor Red
    exit 2
}

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $smoke -Package $Package
$smokeExit = $LASTEXITCODE

Write-Host ''
if ($smokeExit -eq 0 -and $dirty.Count -eq 0) {
    Write-Host '  result  PASS (clean environment)' -ForegroundColor Green
    exit 0
}

if ($smokeExit -eq 0) {
    Write-Host '  result  PASS (but the environment was not clean)' -ForegroundColor Yellow
    exit 0
}

Write-Host '  result  FAIL' -ForegroundColor Red
exit 1
