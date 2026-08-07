# Phase 6 Pass 1 — a deployable runtime

**Date:** 2026-08-07
**Scope:** making EchoForge installable and runnable on a machine nobody has prepared. Stops before
the signed installer and the clean-VM qualification, which are Pass 2.

> **Phase 3 acceptance quality gate remains NOT RUN — pending human corpus data.** The
> human-corrected development corpus and the held-out release corpus still do not exist. Nothing in
> this pass changes that, and no summary-quality claim is made anywhere in it.

**Phase 5 diarization was deliberately skipped.** Microphone is You and system is Remote, by
construction of the recorder. Nothing in this pass touches that.

## What an installed EchoForge no longer needs

| Before | Now |
|---|---|
| A Python on `PATH` | Ships CPython 3.12.13, pinned and verified |
| `worker\` beside the repository | The worker package is in the publish output |
| `artifacts\manifest.json` in the repository | The manifest is in the publish output |
| A walk upwards looking for `EchoForge.slnx` | `AppLayout`, resolved from the executable |
| `uv`, a developer venv, a .NET SDK | Nothing. The runtime is inside the package |

## Self-contained publish

`scripts/package.ps1` → `build\package\EchoForge`, **864 files, 166 MB**, `win-x64`,
self-contained.

**Not trimmed and not single-file, and neither is a formality.** WPF loads XAML by reflection, the
SQLite provider resolves `e_sqlite3` by name, and NAudio's WASAPI interop is COM: a trimmed build of
this application fails at the first window rather than at build time. Single-file would bundle the
managed assemblies and change nothing about the parts that matter, because the app-local Python, the
worker package and llama.cpp are separate files and separate processes by design.

The script gates before it publishes — artifact manifest, build with `--warnaserror`, the whole test
suite — then checks the staged layout for the runtime, WPF, the native SQLite, the worker package,
the manifest and the licence texts, and writes a `package.json` the installer and the smoke test can
both read.

## .NET runtime strategy

Self-contained `win-x64`. The `RuntimeIdentifier` is passed on the publish command line rather than
set in the project, because pinning it in the project makes every ordinary Debug build produce a
self-contained copy of the framework and slows the test loop for no benefit.

x64 only: every pinned inference artifact is `win_amd64`, and an installer that ran on ARM64 would
install an application that cannot transcribe.

## App-local Python

| | |
|---|---|
| **Artifact** | `python.cpython-3-12-13` |
| **Distribution** | [python-build-standalone](https://github.com/astral-sh/python-build-standalone), release `20260805` |
| **File** | `cpython-3.12.13+20260805-x86_64-pc-windows-msvc-install_only.tar.gz` |
| **Version** | CPython **3.12.13** |
| **Size** | 46,188,985 bytes |
| **SHA-256** | `d731ce7dddcfad4a9521aac48626ca06326003fe4771a366e0fce6eb58709451` |
| **Licence** | PSF-2.0, retained as `third_party/licenses/cpython-3.12.13-LICENSE.txt` |

Downloaded and hashed locally, and the digest matches the `SHA256SUMS` file published with the same
release. The `install_only` build rather than python.org's embeddable package, because the
embeddable package cannot create virtual environments and the virtual environment is the whole
mechanism. Not the Microsoft Store Python.

It unpacks to `%LOCALAPPDATA%\EchoForge\runtime\python\20260805\`, in a directory named for the
pinned revision so a re-pin installs beside the old one. Activation is a directory rename after
everything is on disk: an interrupted unpack leaves a `.building` directory the next attempt
discards, never a half-populated runtime that looks complete because `python.exe` was written first.

## Worker environment

`%LOCALAPPDATA%\EchoForge\runtime\worker-env\`, built by `WorkerEnvironmentInstaller`.

1. Every wheel in the manifest's runtime closure is verified — length, then SHA-256.
2. The verified files are copied into one flat wheelhouse, because `--find-links` wants one place.
3. A virtual environment is created from the app-local interpreter.
4. `pip install --no-index --find-links <wheelhouse> -r requirements-production.txt`.
5. The imports are **run**, not assumed: `faster_whisper`, `ctranslate2`, `av`, `onnxruntime`.
6. A stamp records the interpreter revision and every wheel digest.
7. The directory is renamed into place.

**`--no-index` is the design, not a precaution.** Once the wheels are verified there is nothing left
to fetch, and an installer that could still reach a package server could still install something the
manifest never vouched for. It is also what makes the installation work on a machine that has never
had a network connection.

The stamp is what makes a re-pinned closure invalidate the environment rather than silently running
against whatever was installed last time. Verified on this machine:

```
python 3.12.13 · faster-whisper 1.2.1 · ctranslate2 4.8.1 · onnxruntime 1.28.0 · cuda-devices 1
```

`scripts/install-worker-runtime.ps1` now delegates to this. It used to resolve packages its own way
with `uv` and a system Python, which meant a developer machine and an installed machine were built
by two different implementations and only one of them was ever tested.

## Path resolution

`AppLayout` is the only place that decides where anything is.

| Concept | Location |
|---|---|
| Application binaries and resources | beside the executable |
| Worker package | `<app>\worker` |
| Pinned manifest | `<app>\artifacts\manifest.json` |
| Licences and notice | `<app>\third_party\` |
| Sessions (canonical) | `%LOCALAPPDATA%\EchoForge\sessions` |
| Verified downloads | `…\models` |
| Interpreter, worker env, wheelhouse | `…\runtime\…` |
| Config, logs, diagnostics, staging | `…\config`, `…\logs`, `…\diagnostics`, `…\temp` |

Staging is under the data root rather than `%TEMP%` so activation is a rename rather than a copy
across volumes. `ECHOFORGE_DATA_ROOT` and `ECHOFORGE_APP_ROOT` redirect both for tests and for the
published smoke.

## Hardware detection

`WindowsHardwareProbe`, behind `IHardwareProbe` so tests describe machines instead of running on
them. **Nothing guesses**; anything unreadable stays null and is listed in `Unavailable`.

| Fact | Source |
|---|---|
| Adapters, vendor, model, dedicated VRAM | DXGI `IDXGIFactory1::EnumAdapters1` + `GetDesc1` |
| NVIDIA driver version | `nvidia-smi`, absent exactly when there is no NVIDIA driver |
| CPU brand string, AVX2, AVX-512 | `CPUID` leaves `0x80000002-4`, `X86Base`/`Avx2`/`Avx512F` |
| Physical memory | `GlobalMemoryStatusEx` |
| Free disk | `DriveInfo` on the data root's volume |
| Microphones and playback endpoints | the recorder's own WASAPI catalog |
| **CUDA** | `ctranslate2.get_cuda_device_count()` in the installed worker environment |

That last one is the important one. An NVIDIA card that enumerates and a CUDA stack that runs are
different facts, and the difference is a driver too old for the pinned CTranslate2 or a laptop whose
discrete adapter is switched off. Read on this machine:

```
AMD Ryzen 7 7800X3D, 16 logical cores, AVX2 + AVX-512, 63.1 GB
NVIDIA GeForce RTX 5070 Ti, 15.6 GB, driver 610.88
CUDA: Available
```

## Recommendation logic

`ProfileRecommender`, deterministic and explainable. **Unknown is never treated as yes.**

| Situation | Transcription | Summaries |
|---|---|---|
| CUDA confirmed, VRAM ≥ 4 GB | `cuda-fp16` | `summary-cuda-q4` at ≥ 10 GB |
| CUDA confirmed, VRAM unreadable | `cuda-int8-float16` | `summary-cpu-q4` |
| NVIDIA present, CUDA unconfirmed | `cpu-int8` | by VRAM |
| No NVIDIA | `cpu-int8` | `summary-cpu-q4` |
| No AVX2 | recording only | recording only |
| RAM < 16 GB / disk < 20 GB | unaffected | not recommended |

It is not tuned to any machine. The thresholds are about what the models need: 1.6 GB of weights
plus working memory for the recogniser, 6.5 GB plus a 32K KV cache for the summariser. Every
recommendation carries its reasons, and "EchoForge could not tell how much memory your GPU has, so
it chose the safe option" is a sentence that changes what a reasonable person does next.

On this machine it recommends `cuda-fp16` and `summary-cuda-q4`, and says why.

## Setup and capability states

Components: `PythonRuntime`, `WorkerEnvironment`, `SpeechModel`, `SummaryRuntime`, `SummaryModel`,
`BenchmarkModel`. States: `NotInstalled`, `Downloading`, `Verifying`, `Installing`, `Ready`,
`Corrupt`, `Incompatible`, `NotNeeded`.

Capabilities are staged, never all-or-nothing:

| Level | Needs |
|---|---|
| **Recording** | nothing. Record, play back, browse, search, export |
| **Transcription** | interpreter + worker packages + speech model |
| **Summarization** | interpreter + worker packages + llama.cpp + summary model |
| **Benchmarking** | the comparison model. Never required, never recommended |

The comparison model reports `NotNeeded` when absent rather than missing, and never counts towards
what the recommended setup still has to download. A red light beside an eight-gigabyte bake-off
candidate is an invitation to fetch it for no reason.

The manifest remains the only authority for hashes. `RuntimeRegistry` is a view composed on demand
and remembers nothing between calls: a cached "ready" that outlived the file it described is exactly
how an application ends up launching a model that is no longer there.

## Download, install, repair

Reuses `ArtifactRegistry` unchanged: resume from a `.partial`, cancel keeping what arrived, verify
before activation, quarantine a digest mismatch, one writer per artifact across processes.
Restarting setup after a completed artifact re-fetches nothing.

**Repair verifies before it downloads.** A file that is present, the right length, and simply has no
proof recorded against it — what an artifact installed by something other than this application
looks like — is repaired by hashing it, not by fetching 1.6 GB again. Repair is per component and
never touches another component, and nothing in it goes near a session, a transcript revision or a
summary revision. Tested.

This pass found five speech-model files in exactly that state on the development machine, present
and correct with no verification marker, and repairing them cost a re-hash and no bytes.

## Offline

`OfflineEnvironment` is applied to every child process, workers and installers alike:

```
HF_HUB_OFFLINE=1   HF_HUB_DISABLE_TELEMETRY=1   HF_HUB_DISABLE_IMPLICIT_TOKEN=1
TRANSFORMERS_OFFLINE=1   HF_DATASETS_OFFLINE=1
PIP_NO_INDEX=1   PYTHONNOUSERSITE=1   PYTHONUTF8=1   PYTHONIOENCODING=utf-8
```

and `HF_ENDPOINT`, `HUGGINGFACE_CO_URL_HOME`, `PIP_INDEX_URL`, `PIP_EXTRA_INDEX_URL` are **removed**
rather than overridden.

These are set because the libraries do not agree by default: `huggingface_hub` will check whether a
model has been updated, and one call like that turns a local transcription into a request naming the
model a private meeting is being run through. `faster-whisper` imports it even when the model path is
local.

Tested: the flags reach a real interpreter; `PYTHONPATH` is forced to the shipped worker package so
nothing else on the machine can be imported; and an installed artifact is usable through a registry
whose HTTP handler **throws on any request**, which is what an offline workflow has to survive.

There is no telemetry in EchoForge. The telemetry variables above disable other people's.

## Diagnostics

Built from an **allow-list**, not filtered. There is no path through the collector that opens a
transcript revision, a summary revision, a journal or a session title: it names the fields it
collects one at a time. Redaction by exclusion is the only kind that stays correct when somebody
later adds a field.

It carries the version, the .NET runtime, whether the layout is published, the hardware summary,
component and artifact status with pinned digests, the chosen profiles, the interpreter and package
versions, the offline policy, session **count**, and index health.

It carries no transcript, no summary, no prompt, no meeting title, no session ID, no audio path, no
device name — endpoints are counted, because an endpoint is named after somebody's headset or their
employer — and no secret. Tests plant meeting content in every file a careless collector might read
and assert none of it appears, and assert separately that what support actually needs is still
there, because redaction that removed everything useful is the same failure with a different shape.

Writing one is always explicit. Nothing uploads it.

## Third-party notices

`third_party/NOTICE.md` is new: bundled components, the interpreter, every inference runtime, the
CUDA redistributables and the three models, each against the licence text retained in
`third_party/licenses/`. It states plainly which components are downloaded rather than bundled, and
records that Ministral is optional, never installed by default, and listed only because the
packaging layout makes it available.

## Packaging scaffold

`packaging/inno/EchoForge.iss` compiles a per-user installer: `PrivilegesRequired=lowest`, x64 only,
the whole staged package. The uninstaller removes the application and nothing else —
`%LOCALAPPDATA%\EchoForge` is left alone, because uninstalling is not a request to destroy
somebody's meetings.

**It is a skeleton and is not claimed otherwise.** No signing, no upgrade policy, and it has not
been installed anywhere.

## Verification

| Check | Result |
|---|---|
| `dotnet build -c Debug --warnaserror` | 0 warnings, 0 errors |
| `dotnet test` | **823 passed**, 0 failed, 0 skipped |
| `scripts/run-worker-tests.ps1 -Frozen` | **218 passed** |
| `scripts/verify-models.ps1` | PASS, **36 entries** (35 → 36) |
| `scripts/verify-models.ps1 -Downloaded` | PASS, 36 verified |
| `scripts/package.ps1` | 864 files, 166 MB staged |
| `scripts/smoke-published.ps1` | **PASS** |
| `scripts/smoke-setup.cs` | **PASS** |
| `scripts/smoke-summary.cs` | PASS |
| `scripts/smoke-production-backend.ps1` | PASS |
| `scripts/smoke-library.cs` | PASS |
| Development build launch | opens, exits 0 |
| Published application launch | opens, exits 0 |

68 new .NET tests. All 755 earlier tests unchanged and passing.

### The Python gate earned its keep

`test_every_download_url_is_https_and_names_its_revision` failed when the interpreter was first
pinned: the release API reports the download URL percent-encoded, so the filename was not literally
in the URL. Rather than loosen a frozen check, the unencoded URL was downloaded and hashed — byte
identical — and pinned instead.

### Published-layout smoke

The one that can catch what the others cannot. It copies the package into
`…\echoforge published smoke <id> ünïcode\Program Files\EchoForge`, gives it a fresh data root,
sets `PATH` to Windows only, removes `ECHOFORGE_PYTHON`, `DOTNET_ROOT`, `PYTHONPATH`, `PYTHONHOME`
and `VIRTUAL_ENV`, and runs it. It launches, composes its window, survives startup, exits 0, creates
its directories under the data root, writes nothing into the installation directory, and no shipped
file names the repository it was built in.

## Real-machine smoke

Development machine, all of it against real hardware and the real installed stack:

- App-local CPython 3.12.13 installed from the pinned archive.
- Worker environment built offline from the verified closure; CTranslate2 sees 1 CUDA device.
- Hardware detection read the CPU, memory, disk, three adapters and their VRAM, the NVIDIA driver
  and the audio endpoints.
- Recommendation: `cuda-fp16` and `summary-cuda-q4`, explained.
- Setup window loads, populates its component, capability and hardware lists, and shows the install
  and repair actions.
- Diagnostics written and scanned against this machine's **real** library: no audio path, no
  transcript, no summary.
- Production transcription and production summarisation still pass their smoke tests.
- Library smoke passes, including a synthetic session recycled and not resurrected by a rebuild.

No real user data was modified.

## What Pass 2 still owns

Nothing here makes EchoForge distribution-ready, and publishing successfully is not the same as
being shippable.

- Build and **sign** the Inno installer; SmartScreen reputation.
- Standard-user install and uninstall on a clean Windows 11 VM; confirm the uninstaller leaves
  `%LOCALAPPDATA%\EchoForge` intact.
- First-run setup downloading over that machine's own connection, including an interrupted resume.
- The **network-blocked end-to-end run** on that machine: record, transcribe, summarise, export,
  restart, repeat, with the adapter disconnected.
- Upgrade and downgrade policy over an existing installation.
- Sleep, resume, forced power-off during a recording; the three-hour soak.
- Antivirus and SmartScreen checks before and after signing.
- A machine with no discrete GPU, and a non-ASCII **user profile** name.
- Final licence review of everything actually distributed.

`docs/CLEAN_VM_TEST.md` and `scripts/vm-precheck.ps1` are the harness for that; the precheck refuses
to report a pass on a machine that has an SDK, a Python, Ollama, a CUDA toolkit or an existing
EchoForge data directory, because a clean-VM test on a machine that is not clean proves nothing and
fails silently.

## Phase 3 corpus gate

Unchanged, and stated again because it is a release blocker:

- Human-corrected **development** corpus: **NOT supplied**
- Held-out **release** corpus: **NOT supplied**
- Phase 3 summary-quality acceptance gate: **NOT RUN**

Phase 3's implementation and evaluation infrastructure are complete; the gate is blocked on data
that has to come from outside the repository. It does not block Phase 6 engineering, and nothing in
this pass makes any summary-quality claim.
