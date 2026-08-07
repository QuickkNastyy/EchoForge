<#
.SYNOPSIS
    Installs EchoForge's own Python and its worker environment, offline, from verified artifacts.

.DESCRIPTION
    A thin wrapper around scripts/install-runtime.cs, which runs the production installers —
    PythonRuntimeInstaller and WorkerEnvironmentInstaller, the same classes the application's setup
    screen calls.

    It used to resolve and install packages its own way, with uv and a system Python. That meant a
    developer machine and an installed machine were built by two different implementations and only
    one of them was ever tested. The production implementation is now the only one, and this script
    exists so the familiar command still works.

    Nothing here can reach a package index. Every wheel is a manifest entry whose length and digest
    are checked before it is copied into the wheelhouse, and pip runs with --no-index.

.PARAMETER DataRoot
    Where the interpreter, the wheelhouse and the environment are created. Defaults to
    %LOCALAPPDATA%\EchoForge.

.PARAMETER Status
    Report what is installed and stop.

.PARAMETER Repair
    Verify everything already on disk, then rebuild the worker environment. Verifying first is the
    point: a model that is present and simply has no proof recorded against it is repaired by
    hashing it, not by fetching it again.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install-worker-runtime.ps1

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\install-worker-runtime.ps1 -Repair
#>
[CmdletBinding()]
param(
    [string] $DataRoot,
    [switch] $Status,
    [switch] $Repair
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$script = Join-Path $PSScriptRoot 'install-runtime.cs'

if (-not (Test-Path $script)) {
    Write-Host "install-runtime.cs was not found beside this script" -ForegroundColor Red
    exit 2
}

if ($DataRoot) { $env:ECHOFORGE_DATA_ROOT = $DataRoot }

$arguments = @()
if ($Status) { $arguments += '--status' }
if ($Repair) { $arguments += '--repair' }

Push-Location $repoRoot
try {
    if ($arguments.Count -gt 0) {
        & dotnet run $script -- @arguments
    }
    else {
        & dotnet run $script
    }

    exit $LASTEXITCODE
}
finally {
    Pop-Location
    if ($DataRoot) { Remove-Item Env:\ECHOFORGE_DATA_ROOT -ErrorAction SilentlyContinue }
}
