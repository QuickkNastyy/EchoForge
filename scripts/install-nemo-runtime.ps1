<#
.SYNOPSIS
    Creates EchoForge's isolated NeMo environment inside an explicitly named WSL distribution.

.DESCRIPTION
    Uses an existing Linux CPython 3.11 interpreter to create a dedicated environment, then
    installs the complete hash-locked NeMo/PyTorch closure. This is a setup-time operation;
    transcription itself remains offline and never invokes pip.

    Existing environments are reused and synchronised. The script never clears or deletes a
    target directory. A non-venv target is refused.

.PARAMETER Distribution
    Exact WSL distribution name. Defaults to Ubuntu.

.PARAMETER PythonExecutable
    Absolute Linux path to a CPython 3.11 base interpreter.

.PARAMETER EnvironmentPath
    Absolute Linux path for the dedicated virtual environment.

.PARAMETER Wheelhouse
    Optional absolute Linux directory containing all locked wheels. When supplied, installation
    uses --no-index and cannot access a package server.
#>
[CmdletBinding()]
param(
    [string] $Distribution = 'Ubuntu',
    [Parameter(Mandatory = $true)]
    [string] $PythonExecutable,
    [Parameter(Mandatory = $true)]
    [string] $EnvironmentPath,
    [string] $Wheelhouse
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-LinuxAbsolute([string] $value, [string] $name) {
    if ([string]::IsNullOrWhiteSpace($value) -or -not $value.StartsWith('/') -or $value -match '[\x00-\x1f\x7f]') {
        throw "$name must be an absolute Linux path without control characters."
    }
}

if ([string]::IsNullOrWhiteSpace($Distribution) -or $Distribution -match '[\x00-\x1f\x7f]') {
    throw 'Distribution must be an explicit WSL distribution name.'
}
Assert-LinuxAbsolute $PythonExecutable 'PythonExecutable'
Assert-LinuxAbsolute $EnvironmentPath 'EnvironmentPath'
if ($Wheelhouse) { Assert-LinuxAbsolute $Wheelhouse 'Wheelhouse' }

$repoRoot = Split-Path -Parent $PSScriptRoot
$lockPath = Join-Path $repoRoot 'worker-nemo\requirements-production.txt'
$wsl = Join-Path $env:WINDIR 'System32\wsl.exe'
if (-not (Test-Path -LiteralPath $wsl)) { throw 'WSL2 is not installed.' }
if (-not (Test-Path -LiteralPath $lockPath)) { throw 'The NeMo hash lock is missing.' }

$version = (& $wsl --distribution $Distribution --exec $PythonExecutable --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $version -notmatch '^Python 3\.11(?:\.|$)') {
    throw "The configured base interpreter must be CPython 3.11; it reported '$version'."
}
Write-Host "Using $version in WSL distribution '$Distribution'." -ForegroundColor Cyan

$venvPython = $EnvironmentPath.TrimEnd('/') + '/bin/python'
$probe = & $wsl --distribution $Distribution --exec /usr/bin/env test -f ($EnvironmentPath.TrimEnd('/') + '/pyvenv.cfg')
$venvExists = $LASTEXITCODE -eq 0
if (-not $venvExists) {
    $targetExists = & $wsl --distribution $Distribution --exec /usr/bin/env test -e $EnvironmentPath
    if ($LASTEXITCODE -eq 0) {
        throw 'EnvironmentPath already exists but is not a Python virtual environment; nothing was changed.'
    }

    Write-Host "Creating $EnvironmentPath ..." -ForegroundColor Cyan
    & $wsl --distribution $Distribution --exec $PythonExecutable -m venv $EnvironmentPath
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$linuxLock = (& $wsl --distribution $Distribution --exec /usr/bin/wslpath -a $lockPath | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or -not $linuxLock.StartsWith('/')) {
    throw 'The Windows lock path could not be translated into WSL.'
}

$install = @('-m', 'pip', 'install', '--disable-pip-version-check', '--require-hashes')
if ($Wheelhouse) {
    $install += @('--no-index', '--find-links', $Wheelhouse)
}
$install += @('-r', $linuxLock)

Write-Host 'Installing the hash-locked NeMo runtime. This may download several gigabytes...' -ForegroundColor Cyan
& $wsl --distribution $Distribution --exec $venvPython @install
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$verify = 'import importlib.metadata as m; import torch; print("nemo=" + m.version("nemo_toolkit")); print("torch=" + m.version("torch")); print("cuda=" + str(torch.cuda.is_available()))'
& $wsl --distribution $Distribution --exec $venvPython -c $verify
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ''
Write-Host 'NeMo runtime installed and version-checked.' -ForegroundColor Green
Write-Host "Configure EchoForge with ECHOFORGE_NEMO_WSL_DISTRIBUTION=$Distribution"
Write-Host "and ECHOFORGE_NEMO_WSL_PYTHON=$venvPython"
