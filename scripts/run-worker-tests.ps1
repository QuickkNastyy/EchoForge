<#
.SYNOPSIS
    Runs the Python worker test suite.

.DESCRIPTION
    Syncs worker/.venv from the committed lock file and runs tests/worker_tests against it.
    The suite includes tests that launch a real worker child process, because a worker that
    only ever runs inside the test's own interpreter proves nothing about stdio framing,
    encodings, exit codes, or what the stream looks like when the process dies.

    The worker itself has no runtime dependencies in Phase 2A; pytest and jsonschema are
    development dependencies only.

.PARAMETER Frozen
    Fail rather than update the lock file. Use this in packaging and release checks.

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\run-worker-tests.ps1
#>
[CmdletBinding()]
param(
    [switch] $Frozen,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]] $PytestArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$workerRoot = Join-Path $repoRoot 'worker'

$uv = Get-Command uv -ErrorAction SilentlyContinue
if (-not $uv) {
    Write-Host 'uv was not found on PATH.' -ForegroundColor Red
    Write-Host 'The worker test suite needs uv and a CPython 3.12 interpreter.' -ForegroundColor Red
    Write-Host 'Install it from https://docs.astral.sh/uv/ and run this script again.' -ForegroundColor Red
    exit 2
}

Push-Location $workerRoot
try {
    $syncArgs = @('sync', '--group', 'dev')
    if ($Frozen) { $syncArgs += '--frozen' }

    & uv @syncArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "uv sync failed with exit code $LASTEXITCODE." -ForegroundColor Red
        exit $LASTEXITCODE
    }

    $runArgs = @('run', 'pytest')
    if ($PytestArgs) { $runArgs += $PytestArgs }

    & uv @runArgs
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}
