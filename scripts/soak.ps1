<#
.SYNOPSIS
    Runs the published EchoForge for a fixed duration and watches it for resource leaks.

.DESCRIPTION
    A deterministic soak harness. It launches the published application under a disposable data root
    with nothing but Windows on PATH - the same isolation the published smoke uses - then samples the
    process (and any inference child processes) at a fixed interval for the whole duration, and at
    the end decides whether anything grew without bound or leaked.

    What it proves and what it does not:
      - It exercises the running application over time: startup, recovery, the composed window, the
        tray, the background setup composition, and whatever timers and caches run while it is up. A
        steadily climbing working set or handle count, or a llama-server or Python worker left behind,
        shows up here.
      - It does NOT drive a live recording workload, because that needs real audio and a real window
        a headless run cannot supply. The recording, chunk-rotation and finalization paths are
        covered deterministically by the crash-window and lifecycle unit tests instead.

    The three-hour gate is a real duration, not a label. A short smoke run of this harness is useful
    and is what runs by default; it is explicitly NOT the three-hour gate. Pass -Hours 3 to run the
    gate, on a machine that can give it three uninterrupted hours.

.PARAMETER Package
    The staged package. Defaults to build\package\EchoForge.

.PARAMETER Minutes
    Duration in minutes. Default 3 (a smoke). Ignored if -Hours is given.

.PARAMETER Hours
    Duration in hours. Set -Hours 3 for the release gate.

.PARAMETER SampleSeconds
    Sampling interval. Default 15.

.PARAMETER MaxGrowthFactor
    The most the late-window working set may exceed the early-window baseline before the run is a
    failure. Default 1.5 (a 50% climb).
#>
[CmdletBinding()]
param(
    [string] $Package,
    [double] $Minutes = 3,
    [double] $Hours = 0,
    [int] $SampleSeconds = 15,
    [double] $MaxGrowthFactor = 1.5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $Package) { $Package = Join-Path $repoRoot 'build\package\EchoForge' }
$Package = [System.IO.Path]::GetFullPath($Package)

$durationMinutes = if ($Hours -gt 0) { $Hours * 60 } else { $Minutes }
$isGate = ($Hours -ge 3)

$exe = Join-Path $Package 'EchoForge.App.exe'
if (-not (Test-Path $exe)) {
    Write-Host "no package at $Package - run scripts\package.ps1 first" -ForegroundColor Red
    exit 2
}

Write-Host 'EchoForge soak'
Write-Host ("  duration    {0:N0} minutes{1}" -f $durationMinutes, $(if ($isGate) { ' (THREE-HOUR GATE)' } else { ' (smoke, not the gate)' }))
Write-Host "  interval    $SampleSeconds s"
Write-Host ''

# Disposable data root and a Windows-only PATH, so nothing leans on the developer machine.
$sandbox = Join-Path $env:TEMP ("echoforge soak " + [Guid]::NewGuid().ToString('n').Substring(0, 8))
$dataRoot = Join-Path $sandbox 'data'
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $exe
$psi.UseShellExecute = $false
$psi.WorkingDirectory = $Package
$psi.EnvironmentVariables['PATH'] = "$env:SystemRoot\system32;$env:SystemRoot"
$psi.EnvironmentVariables['ECHOFORGE_DATA_ROOT'] = $dataRoot
foreach ($name in @('ECHOFORGE_PYTHON', 'DOTNET_ROOT', 'PYTHONPATH', 'PYTHONHOME', 'VIRTUAL_ENV')) {
    $psi.EnvironmentVariables.Remove($name) | Out-Null
}

$samples = New-Object System.Collections.Generic.List[object]
$leakedChildren = New-Object System.Collections.Generic.List[string]
$trackedChildPids = @{}
$process = $null
$exitedEarly = $false

try {
    $process = [System.Diagnostics.Process]::Start($psi)
    Write-Host "  pid         $($process.Id)"
    Start-Sleep -Seconds 8   # let it finish composing before the first sample

    $deadline = (Get-Date).AddMinutes($durationMinutes)
    $sample = 0
    while ((Get-Date) -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) {
            Write-Host "  the application exited during the soak (code $($process.ExitCode))" -ForegroundColor Red
            $exitedEarly = $true
            break
        }

        $ws = $process.WorkingSet64
        $handles = $process.HandleCount
        $threads = $process.Threads.Count

        # Inference children should be short-lived; any that persist are a leak. Track them by PID
        # and parentage - a global "any python running" check would pick up unrelated processes on a
        # shared developer machine, which is exactly the false positive to avoid.
        $children = @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $($process.Id)" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match 'python|llama-server' })
        foreach ($child in $children) { $trackedChildPids[[int]$child.ProcessId] = $child.Name }

        $sample++
        $samples.Add([pscustomobject]@{ n = $sample; ws = $ws; handles = $handles; threads = $threads; children = $children.Count })
        Write-Host ("  [{0,3}] ws {1,7:N0} MB   handles {2,6}   threads {3,4}   inference-children {4}" -f `
            $sample, ($ws / 1MB), $handles, $threads, $children.Count)

        Start-Sleep -Seconds $SampleSeconds
    }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(30000)) { $process.Kill($true) }
    }
    # Only the specific inference children this run spawned count as leaks - checked by their own
    # PID, so unrelated python/llama-server processes on the machine are never blamed on EchoForge.
    foreach ($childPid in $trackedChildPids.Keys) {
        $alive = Get-Process -Id $childPid -ErrorAction SilentlyContinue
        if ($alive) { $leakedChildren.Add("$($trackedChildPids[$childPid]) (pid $childPid)") }
    }
    try { Remove-Item $sandbox -Recurse -Force -ErrorAction Stop } catch { }
}

Write-Host ''
if ($exitedEarly -or $samples.Count -lt 4) {
    Write-Host '  result  FAIL - too few samples or the application exited early' -ForegroundColor Red
    exit 1
}

# Compare the first quartile of samples to the last quartile: a leak shows as sustained growth, not
# as the ordinary jitter between two adjacent samples.
$q = [Math]::Max(1, [int]($samples.Count / 4))
$early = ($samples | Select-Object -First $q | Measure-Object -Property ws -Average).Average
$late = ($samples | Select-Object -Last $q | Measure-Object -Property ws -Average).Average
$growth = if ($early -gt 0) { $late / $early } else { 0 }

$maxHandles = ($samples | Measure-Object -Property handles -Maximum).Maximum
$firstHandles = $samples[0].handles

Write-Host ("  working set   early {0:N0} MB  ->  late {1:N0} MB   (x{2:N2})" -f ($early / 1MB), ($late / 1MB), $growth)
Write-Host ("  handles       first {0}  ->  peak {1}" -f $firstHandles, $maxHandles)
Write-Host ("  samples       {0}" -f $samples.Count)

$failures = New-Object System.Collections.Generic.List[string]
if ($growth -gt $MaxGrowthFactor) { $failures.Add("working set grew x$([Math]::Round($growth,2)), over the x$MaxGrowthFactor limit") }
if ($maxHandles -gt ($firstHandles * 3 + 500)) { $failures.Add("handle count climbed from $firstHandles to $maxHandles") }
if ($leakedChildren.Count -gt 0) { $failures.Add("inference processes left running: " + ($leakedChildren -join ', ')) }

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host '  result  FAIL' -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "    - $f" -ForegroundColor Red }
    exit 1
}

if ($isGate) {
    Write-Host '  result  PASS (three-hour gate)' -ForegroundColor Green
}
else {
    Write-Host '  result  PASS (smoke)' -ForegroundColor Green
    Write-Host '  note    THREE-HOUR SOAK NOT RUN - this was a smoke. Re-run with -Hours 3 for the gate.' -ForegroundColor Yellow
}
exit 0
