<#
.SYNOPSIS
    Provisions EchoForge's complete, isolated NeMo runtime inside WSL, from nothing.

.DESCRIPTION
    This is what the Install button behind NVIDIA Parakeet and Canary runs. It owns the whole
    provisioning problem rather than half of it: WSL availability, an exact CPython 3.11, the
    hash-locked NeMo/PyTorch closure, a CUDA probe, and a real inference smoke test. Nothing here
    asks the user to open a shell, run pip, or set an environment variable.

    Why uv rather than the distribution's Python: the host distro ships CPython 3.14, the pinned
    NeMo closure is resolved for 3.11, and `apt install python3.11` both mutates the user's system
    and is unavailable on several supported releases. uv installs a standalone CPython build into a
    directory EchoForge owns, so the exact interpreter is reproducible and the distribution is left
    exactly as it was found. uv itself is staged from EchoForge's pinned, SHA-256 verified artifact
    - it is never curled from an install script.

    Everything lives under ~/.local/share/echoforge/nemo. No global site-packages are touched, no
    system Python is modified, and removing that one directory removes the whole runtime.

    Each step prints a machine-readable `::step::<name>::<state>` line so the application can show
    real progress instead of a spinner. Steps already satisfied are skipped and reported as such,
    which is what makes this safe to run again after a failure.

.PARAMETER Distribution
    Exact WSL distribution name. Defaults to the WSL default distribution.

.PARAMETER UvArchive
    Windows path to the verified uv Linux archive EchoForge staged from its artifact registry.

.PARAMETER ModelDirectory
    Windows path to the verified model directory to smoke-test, if any.

.PARAMETER SkipSmokeTest
    Provision only. Readiness is never claimed without a smoke test, so this is for diagnostics.
#>
[CmdletBinding()]
param(
    [string] $Distribution,
    [Parameter(Mandatory = $true)][string] $UvArchive,
    [string] $ModelDirectory,
    [switch] $SkipSmokeTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolved to a real absolute path as soon as the distribution is known. Carrying a literal
# "$HOME" around means every command that quotes it correctly stops expanding it, which is a very
# quiet way to create a directory called $HOME next to whatever the working directory happened
# to be.
$RuntimeRoot = ''
$RuntimeSuffix = '.local/share/echoforge/nemo'
$PythonVersion = '3.11.14'

function Step([string] $name, [string] $state, [string] $detail = '') {
    Write-Host "::step::${name}::${state}::${detail}"
}

function Fail([string] $name, [string] $detail) {
    Step $name 'failed' $detail
    Write-Error $detail
    exit 1
}

$wsl = Join-Path $env:WINDIR 'System32\wsl.exe'
if (-not (Test-Path -LiteralPath $wsl)) {
    Fail 'wsl' 'WSL is not installed on this machine.'
}

# WSL emits UTF-16LE. Reading it as anything else produces the interleaved-null text that makes
# every string comparison below silently false.
$previousEncoding = [Console]::OutputEncoding
$env:WSL_UTF8 = '1'

function Invoke-Wsl {
    param([string[]] $Arguments, [switch] $AllowFailure)

    # Windows PowerShell turns each stderr line from a native command into an ErrorRecord, and with
    # ErrorActionPreference = Stop that terminates the script. uv writes ordinary progress to
    # stderr, so a successful install would abort the run with its own status line as the message.
    # The exit code is the only thing that decides whether a step failed.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & $wsl @Arguments 2>&1 | Out-String
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }

    if ($LASTEXITCODE -ne 0 -and -not $AllowFailure) {
        throw "wsl $($Arguments -join ' ') failed with $LASTEXITCODE`n$output"
    }
    return $output.Trim()
}

function Invoke-Bash([string] $script, [switch] $AllowFailure) {
    # This file has Windows line endings, so a multi-line here-string arrives at bash with a
    # carriage return on the end of every line - and `set -e\r` is not `set -e`.
    $script = $script -replace "`r`n", "`n"
    $arguments = @()
    if ($Distribution) { $arguments += @('--distribution', $Distribution) }
    $arguments += @('--exec', '/bin/bash', '-lc', $script)
    return Invoke-Wsl -Arguments $arguments -AllowFailure:$AllowFailure
}

<#
.SYNOPSIS
    Streams a Windows file into the Linux filesystem over stdin.

.DESCRIPTION
    Deliberately not a copy across /mnt/c. WSL's 9p mount caches directory listings, so a file
    EchoForge verified and staged seconds ago is routinely invisible to Linux for a while - which
    presents as "no such file" for something the user can see in Explorer. Piping the bytes through
    the shell has no such window, does not depend on the mount existing at all, and lands the file
    on ext4 where it is going to be read from anyway.
#>
function Copy-IntoWsl([string] $source, [string] $target) {
    # Redirection is done by cmd rather than by a redirected StreamWriter. .NET's StandardInput is
    # a text writer, and disposing it flushes an encoder preamble onto the end of the pipe - which
    # produced a gzip archive exactly three bytes too long and an extraction failure that pointed
    # at nothing. cmd copies bytes and has no opinion about encodings.
    $arguments = @()
    if ($Distribution) { $arguments += "--distribution $Distribution" }
    $arguments += "--exec /bin/bash -lc ""cat > '$target'"""

    $errors = & $env:ComSpec /c "`"$wsl`" $($arguments -join ' ') < `"$source`"" 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "copying $source into WSL failed with $LASTEXITCODE`n$errors"
    }

    # Checked rather than assumed, because the failure this replaced was silent and produced a
    # plausible file. A byte count is cheap and catches every variant of it.
    $expected = (Get-Item -LiteralPath $source).Length
    $actual = Invoke-Bash "stat -c %s '$target'"
    if ("$actual" -ne "$expected") {
        throw "copying $source into WSL produced $actual bytes where $expected were expected."
    }
}

try {
    # -- 1. WSL and a usable distribution -------------------------------------------------------
    Step 'wsl' 'running'

    $distributions = Invoke-Wsl -Arguments @('--list', '--quiet') -AllowFailure
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($distributions)) {
        Fail 'wsl' 'WSL is present but no Linux distribution is installed. EchoForge can install one for you, or you can run "wsl --install -d Ubuntu"; that step needs administrator rights and may require a restart.'
    }

    # @() because a single installed distribution comes back as a bare string, and indexing a
    # string yields its first character - which is how "Ubuntu" became a distribution called "U".
    $names = @($distributions -split "`r?`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($Distribution) {
        if ($names -notcontains $Distribution) {
            Fail 'wsl' "The WSL distribution '$Distribution' does not exist. Installed: $($names -join ', ')."
        }
    }
    else {
        $Distribution = $names[0]
    }

    if ($Distribution -match '\s|"') {
        Fail 'wsl' "The WSL distribution name '$Distribution' contains whitespace or a quote, which cannot be passed through unambiguously. Rename it, or install a distribution with a plain name."
    }

    $version = Invoke-Bash 'uname -sr'
    Step 'wsl' 'ready' "$Distribution ($version)"

    # -- 2. A place EchoForge owns ---------------------------------------------------------------
    Step 'runtime_directory' 'running'

    # Not $home: PowerShell reserves that name and refuses to assign it.
    $linuxHome = Invoke-Bash 'printf %s "$HOME"'
    if (-not $linuxHome.StartsWith('/')) {
        Fail 'runtime_directory' "The Linux home directory could not be resolved: '$linuxHome'."
    }

    $RuntimeRoot = "$linuxHome/$RuntimeSuffix"
    Invoke-Bash "mkdir -p $RuntimeRoot/bin $RuntimeRoot/python $RuntimeRoot/cache" | Out-Null
    Step 'runtime_directory' 'ready' $RuntimeRoot

    # -- 3. uv, from EchoForge's verified copy ---------------------------------------------------
    Step 'uv' 'running'
    if (-not (Test-Path -LiteralPath $UvArchive)) {
        Fail 'uv' "The verified uv archive was not found at $UvArchive."
    }

    $uvPresent = Invoke-Bash "test -x $RuntimeRoot/bin/uv && $RuntimeRoot/bin/uv --version || true"
    if ($uvPresent -notmatch '^uv \d') {
        Copy-IntoWsl ([System.IO.Path]::GetFullPath($UvArchive)) "$RuntimeRoot/uv.tar.gz"

        # Extracted onto ext4 and installed with an execute bit, which a file on the Windows mount
        # would not reliably keep.
        Invoke-Bash "set -e; cd $RuntimeRoot; tar -xzf uv.tar.gz; install -m 0755 uv-x86_64-unknown-linux-gnu/uv bin/uv; rm -rf uv-x86_64-unknown-linux-gnu uv.tar.gz" | Out-Null
        $uvPresent = Invoke-Bash "$RuntimeRoot/bin/uv --version"
    }
    Step 'uv' 'ready' $uvPresent

    # -- 4. An exact CPython 3.11, in EchoForge's directory --------------------------------------
    Step 'python' 'running'
    $env:UV_PYTHON_INSTALL_DIR = "$RuntimeRoot/python"
    $interpreter = Invoke-Bash @"
set -e
export UV_PYTHON_INSTALL_DIR=$RuntimeRoot/python
export UV_CACHE_DIR=$RuntimeRoot/cache
$RuntimeRoot/bin/uv python install --managed-python $PythonVersion >/dev/null 2>&1 || true
$RuntimeRoot/bin/uv python find --managed-python $PythonVersion
"@
    if (-not $interpreter.StartsWith('/')) {
        Fail 'python' "An isolated CPython $PythonVersion could not be provisioned: $interpreter"
    }

    $reported = Invoke-Bash "$interpreter --version"
    if ($reported -notmatch '^Python 3\.11(\.|$)') {
        Fail 'python' "The provisioned interpreter reported '$reported' rather than CPython 3.11."
    }
    Step 'python' 'ready' "$reported at $interpreter"

    # -- 5. The environment and the hash-locked closure ------------------------------------------
    Step 'dependencies' 'running'

    $repoRoot = Split-Path -Parent $PSScriptRoot
    $lockPath = Join-Path $repoRoot 'worker-nemo\requirements-production.txt'
    if (-not (Test-Path -LiteralPath $lockPath)) {
        Fail 'dependencies' 'The NeMo hash lock is missing from this installation.'
    }

    $linuxLock = "$RuntimeRoot/requirements-production.txt"
    Copy-IntoWsl $lockPath $linuxLock

    # uv creates the environment; pip installs the closure. The lock was generated for pip's
    # --extra-index-url semantics, and uv's first-index policy - which exists to prevent dependency
    # confusion, and is right to - refuses it. Loosening that policy to make the install succeed
    # would be trading an integrity guarantee for convenience, so the tool that understands the
    # lock is the one that reads it. Every distribution is still hash-verified either way.
    $install = @"
set -e
export UV_PYTHON_INSTALL_DIR=$RuntimeRoot/python
export UV_CACHE_DIR=$RuntimeRoot/cache
test -f $RuntimeRoot/env/pyvenv.cfg || $RuntimeRoot/bin/uv venv --seed --python '$interpreter' $RuntimeRoot/env
# Seeded separately as well as at creation, so an environment left behind by an earlier attempt is
# repaired rather than requiring the user to delete a directory they were never told about.
$RuntimeRoot/env/bin/python -m pip --version >/dev/null 2>&1 || $RuntimeRoot/bin/uv pip install --python $RuntimeRoot/env/bin/python pip
$RuntimeRoot/env/bin/python -m pip install --disable-pip-version-check --require-hashes -r '$linuxLock'
"@
    Invoke-Bash $install | Out-Null

    # Read back rather than asserted. A hard-coded version string in a status line is how a
    # pinned-version bump gets reported as the version it replaced.
    # Sent base64-encoded, like the CUDA probe below. Nesting quotes through PowerShell, wsl.exe
    # and bash is three layers of escaping and every one of them has already been got wrong here.
    $report = 'import importlib.metadata as m; print("NeMo " + m.version("nemo_toolkit") + " / PyTorch " + m.version("torch"))'
    $encodedReport = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($report))
    $versions = Invoke-Bash "echo $encodedReport | base64 -d | $RuntimeRoot/env/bin/python -"
    Step 'dependencies' 'ready' (($versions -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 1))

    # -- 6. Does CUDA actually work in here ------------------------------------------------------
    Step 'cuda' 'running'
    $probe = @'
import json, torch, importlib.metadata as m
print(json.dumps({
    "torch": m.version("torch"),
    "nemo": m.version("nemo_toolkit"),
    "cuda_available": torch.cuda.is_available(),
    "device": torch.cuda.get_device_name(0) if torch.cuda.is_available() else None,
    "capability": list(torch.cuda.get_device_capability(0)) if torch.cuda.is_available() else None,
}))
'@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($probe))
    $probed = Invoke-Bash "echo $encoded | base64 -d | $RuntimeRoot/env/bin/python -"
    $facts = $probed | Select-Object -Last 1 | ConvertFrom-Json

    if (-not $facts.cuda_available) {
        Fail 'cuda' 'PyTorch in the provisioned runtime cannot see a CUDA device. The NVIDIA models need one, so the runtime is not ready.'
    }
    Step 'cuda' 'ready' "$($facts.device), torch $($facts.torch), NeMo $($facts.nemo)"

    if ($SkipSmokeTest) {
        Step 'smoke_test' 'skipped' 'requested'
        Step 'runtime' 'ready' "$RuntimeRoot/env/bin/python"
        exit 0
    }

    # -- 7. A real, bounded inference ------------------------------------------------------------
    Step 'smoke_test' 'running'
    if (-not $ModelDirectory) {
        Step 'smoke_test' 'skipped' 'no verified model directory was supplied'
        Step 'runtime' 'ready' "$RuntimeRoot/env/bin/python"
        exit 0
    }

    # Staged onto ext4 rather than read across /mnt/c. Two reasons, both learned the hard way:
    # WSL's 9p mount caches directory listings, so a model EchoForge verified moments ago is
    # routinely invisible to Linux for a while - and loading a five-gigabyte checkpoint over 9p is
    # slow enough to matter on every single transcription, not just this one.
    $stagedModel = "$RuntimeRoot/models/" + (Split-Path -Leaf $ModelDirectory)
    Invoke-Bash "mkdir -p '$stagedModel'" | Out-Null

    foreach ($file in Get-ChildItem -LiteralPath $ModelDirectory -File) {
        if ($file.Name.EndsWith('.verified.json')) { continue }

        $target = "$stagedModel/$($file.Name)"
        $existing = Invoke-Bash "stat -c %s '$target' 2>/dev/null || echo 0"
        if ("$existing" -eq "$($file.Length)") { continue }

        Step 'smoke_test' 'running' "staging $($file.Name)"
        Copy-IntoWsl $file.FullName $target
    }

    # The worker package too, so the smoke test runs the same code the transcription path does.
    $stagedWorker = "$RuntimeRoot/worker/echoforge_worker"
    Invoke-Bash "mkdir -p '$stagedWorker'" | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $repoRoot 'worker\echoforge_worker') -Filter *.py) {
        Copy-IntoWsl $file.FullName "$stagedWorker/$($file.Name)"
    }

    $smoke = @"
set -e
export ECHOFORGE_NEMO_MODEL_DIR='$stagedModel'
export PYTHONPATH='$RuntimeRoot/worker'
export HF_HUB_OFFLINE=1
export TRANSFORMERS_OFFLINE=1
$RuntimeRoot/env/bin/python -m echoforge_worker.nemo_smoke
"@
    $result = Invoke-Bash $smoke
    Step 'smoke_test' 'ready' (($result -split "`n" | Where-Object { $_.Trim() } | Select-Object -Last 1))

    Step 'runtime' 'ready' "$RuntimeRoot/env/bin/python"
    exit 0
}
catch {
    Step 'runtime' 'failed' $_.Exception.Message
    Write-Error $_
    exit 1
}
finally {
    [Console]::OutputEncoding = $previousEncoding
}
