<#
.SYNOPSIS
    Proves the published application works without the repository, the .NET SDK, or system Python.

.DESCRIPTION
    The other smoke tests run from the source tree, which is exactly what makes them unable to
    catch the class of defect this one exists for: a path that resolves because a developer
    happens to be standing in a repository. This copies the staged package somewhere else, gives
    it its own data root, blanks the environment that a developer machine provides, and runs it.

    The interesting checks are the negative ones. The published copy must not need C:\EchoForge to
    exist, must not find a Python on PATH, must not have a .NET SDK to fall back on, and must
    resolve its manifest, its worker package and its native SQLite from beside its own executable.

.PARAMETER Package
    The staged package. Defaults to build\package\EchoForge, which scripts\package.ps1 produces.

.PARAMETER DataRoot
    A throwaway data root. Defaults to a new directory under %TEMP%, and is removed afterwards.

.PARAMETER KeepData
    Leave the throwaway data root in place for inspection.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\smoke-published.ps1
#>
[CmdletBinding()]
param(
    [string] $Package,
    [string] $DataRoot,
    [switch] $KeepData
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Package) { $Package = Join-Path $repoRoot 'build\package\EchoForge' }

$failures = New-Object System.Collections.Generic.List[string]
function Check([bool] $condition, [string] $what) {
    if ($condition) { Write-Host "  ok    $what" -ForegroundColor Green }
    else { Write-Host "  FAIL  $what" -ForegroundColor Red; $failures.Add($what) }
}
function Note([string] $what) { Write-Host "  note  $what" -ForegroundColor DarkGray }

Write-Host 'EchoForge published-layout smoke test'
Write-Host ''

if (-not (Test-Path (Join-Path $Package 'EchoForge.App.exe'))) {
    Write-Host "  no package at $Package" -ForegroundColor Red
    Write-Host '  Run scripts\package.ps1 first.' -ForegroundColor Red
    exit 2
}

# Somewhere with a space and a non-ASCII character in it, because an installed application ends up
# under names like "C:\Program Files\..." and profiles are named after people.
$sandbox = Join-Path $env:TEMP ("echoforge published smoke " + [Guid]::NewGuid().ToString('n').Substring(0, 8) + " ünïcode")
$installed = Join-Path $sandbox 'Program Files\EchoForge'
if (-not $DataRoot) { $DataRoot = Join-Path $sandbox 'AppData\EchoForge' }

New-Item -ItemType Directory -Force -Path $installed | Out-Null
New-Item -ItemType Directory -Force -Path $DataRoot | Out-Null

Write-Host "  package    $Package"
Write-Host "  installed  $installed"
Write-Host "  data       $DataRoot"
Write-Host ''

try {
    Write-Host '== Copy'
    Copy-Item (Join-Path $Package '*') -Destination $installed -Recurse -Force
    Check (Test-Path (Join-Path $installed 'EchoForge.App.exe')) 'the published application copies to a path with spaces and non-ASCII characters'

    Write-Host ''
    Write-Host '== Layout'

    Check (Test-Path (Join-Path $installed 'artifacts\manifest.json')) 'the pinned manifest is beside the executable'
    Check (Test-Path (Join-Path $installed 'worker\echoforge_worker\__init__.py')) 'the worker package is beside the executable'
    Check (Test-Path (Join-Path $installed 'worker\requirements-production.txt')) 'the package list is beside the executable'
    Check (Test-Path (Join-Path $installed 'third_party\NOTICE.md')) 'the third-party notice ships'
    Check ((Get-ChildItem (Join-Path $installed 'third_party\licenses') -File).Count -gt 20) 'the retained licence texts ship'

    Check (Test-Path (Join-Path $installed 'System.Private.CoreLib.dll')) 'the .NET runtime is in the package rather than on the machine'
    Check (Test-Path (Join-Path $installed 'PresentationFramework.dll')) 'WPF is in the package'
    Check (Test-Path (Join-Path $installed 'NAudio.Wasapi.dll')) 'NAudio is in the package'

    $sqlite = @(Get-ChildItem $installed -Recurse -Filter 'e_sqlite3.dll' -File)
    Check ($sqlite.Count -gt 0) 'the native SQLite the library index needs is in the package'

    Write-Host ''
    Write-Host '== Running it with no developer machine around it'

    # The published application gets its own data root, and a PATH with nothing on it but Windows.
    # If anything still finds a Python, an SDK or the repository, it was not resolving from its own
    # directory and this is where that shows up.
    $env:ECHOFORGE_DATA_ROOT = $DataRoot
    $blankPath = "$env:SystemRoot\system32;$env:SystemRoot"

    $probe = Join-Path $installed 'EchoForge.App.exe'

    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $probe
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = $installed
    $psi.EnvironmentVariables['PATH'] = $blankPath
    $psi.EnvironmentVariables['ECHOFORGE_DATA_ROOT'] = $DataRoot
    # Anything a developer machine sets that the application must not depend on.
    foreach ($name in @('ECHOFORGE_PYTHON', 'DOTNET_ROOT', 'PYTHONPATH', 'PYTHONHOME', 'VIRTUAL_ENV')) {
        $psi.EnvironmentVariables.Remove($name) | Out-Null
    }

    $process = [System.Diagnostics.Process]::Start($psi)
    Start-Sleep -Seconds 14
    $process.Refresh()

    Check (-not $process.HasExited) 'the published executable launches with no .NET on PATH'

    if (-not $process.HasExited) {
        Check ($process.MainWindowTitle -eq 'EchoForge') 'the main window composes'

        # Give the background startup work — recovery, setup composition — time to run, so a
        # missing resource shows up as a crash here rather than passing silently.
        Start-Sleep -Seconds 6
        $process.Refresh()
        Check (-not $process.HasExited) 'it is still running after startup finishes composing'

        $null = $process.CloseMainWindow()
        $null = $process.WaitForExit(30000)
        $process.Refresh()

        Check ($process.HasExited -and $process.ExitCode -eq 0) 'it exits cleanly'
    }
    else {
        Note "it exited immediately with code $($process.ExitCode)"
    }

    Write-Host ''
    Write-Host '== Where it put things'

    # The published application must write to the data root it was given and nowhere near its
    # own installation directory.
    Check (Test-Path (Join-Path $DataRoot 'sessions')) 'it created its sessions directory under the data root'
    Check (Test-Path (Join-Path $DataRoot 'config')) 'it created its config directory under the data root'
    Check (-not (Test-Path (Join-Path $installed 'sessions'))) 'it wrote no sessions into the installation directory'

    Write-Host ''
    Write-Host '== Without the repository'

    # Nothing under the installation directory may name the repository it was built in.
    $offenders = @(
        Get-ChildItem $installed -Recurse -File -Include '*.json', '*.txt', '*.runtimeconfig.json' |
        Where-Object { $_.Length -lt 2MB } |
        Where-Object { (Get-Content $_.FullName -Raw -ErrorAction SilentlyContinue) -match [regex]::Escape($repoRoot) }
    )

    Check ($offenders.Count -eq 0) 'no shipped file names the repository it was built in'
    foreach ($offender in $offenders) { Note "  names the repository: $($offender.FullName)" }
}
finally {
    Remove-Item Env:\ECHOFORGE_DATA_ROOT -ErrorAction SilentlyContinue

    if (-not $KeepData) {
        try { Remove-Item $sandbox -Recurse -Force -ErrorAction Stop }
        catch { Note "the sandbox could not be removed: $sandbox" }
    }
    else {
        Note "sandbox kept at $sandbox"
    }
}

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host '  result  PASS' -ForegroundColor Green
    exit 0
}

Write-Host '  result  FAIL' -ForegroundColor Red
foreach ($failure in $failures) { Write-Host "    - $failure" -ForegroundColor Red }
exit 1
