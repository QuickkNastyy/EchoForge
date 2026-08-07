# The clean-machine test

**Status: the harness exists; the run belongs to Phase 6 Pass 2.**

This is the test that decides whether EchoForge can be given to somebody. Everything else is
performed on a machine that has a .NET SDK, a Python, a CUDA toolkit and a repository on it, and
none of that will be true for the person who installs it.

## Why it has a precheck

A clean-VM test performed on a machine that is not clean proves nothing, and it fails silently:
everything works, for the wrong reason, and the defect ships. `scripts/vm-precheck.ps1` therefore
refuses to report a pass when it finds a .NET SDK, a system Python, Ollama, a CUDA toolkit, `uv`,
or an existing `%LOCALAPPDATA%\EchoForge`. It can be overridden with `-AllowDirty`, and the result
then says so on every line that matters.

## The machine

- Windows 11, a supported build, freshly installed.
- **No** .NET SDK or runtime.
- **No** Python of any kind, including the Microsoft Store stub.
- **No** CUDA toolkit. An NVIDIA *driver* is what EchoForge needs and is normal to have; the
  toolkit is a developer install and must not be present.
- **No** Ollama, `uv`, or Visual Studio.
- A standard user account, not an administrator.
- At least 40 GB free. The full model set is roughly 30 GB and the installation wants room to
  unpack.

Two variants are worth running, because they exercise different code paths:

1. **NVIDIA machine.** Confirms the CUDA profiles are recommended and actually run.
2. **No discrete GPU.** Confirms the CPU fallback is recommended, is honest about being slow, and
   works.

A third is worth running once: a user profile whose name contains non-ASCII characters and a
space. The published smoke test already covers that for the *installation* path; the profile path
is what it does not cover.

## What to copy onto it

From a machine that has run `scripts/package.ps1`:

```
EchoForge\            <- build\package\EchoForge, the whole staged package
smoke-published.ps1
vm-precheck.ps1
```

Both scripts must sit beside the `EchoForge` directory, because `vm-precheck.ps1` runs the smoke
test from its own directory.

## Running it

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\vm-precheck.ps1
```

It confirms the machine is clean, then runs the published smoke test: the application launches with
nothing on PATH, resolves its manifest, worker package and native SQLite from beside its own
executable, writes only under its own data root, and exits cleanly.

## What Pass 2 still has to do on that machine

The precheck and the smoke test cover the layout. They do not cover the things that can only be
learned by using the application on a machine that has never seen it:

- Install with the Inno installer as a standard user, and uninstall, and confirm the uninstaller
  leaves `%LOCALAPPDATA%\EchoForge` alone.
- Run first-run setup and download the models over that machine's own connection, including
  interrupting one and resuming it.
- Record, transcribe, summarise, export.
- **Then block the network entirely** — firewall rule or a disconnected adapter — and do all of it
  again. Nothing in the meeting workflow may fail.
- Restart the machine and confirm the application still works offline.
- Sleep, resume, and a forced power-off during a recording.
- A three-hour soak.
- SmartScreen and the common antivirus products, before and after signing.
- Upgrade and downgrade over an existing installation.

None of those are claimed by Pass 1, and none of them can be claimed from a developer machine.
