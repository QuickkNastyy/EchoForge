# Phase 6 Pass 2 — a real installer, and an honest account of what still cannot ship

**Date:** 2026-08-07
**Scope:** turning the Pass 1 deployment foundation into an actual Windows installation, and running
every release/reliability gate that available hardware and credentials genuinely allow. It stops at
the gates that need a clean VM, a no-GPU machine, a non-ASCII user profile, a code-signing
certificate, three uninterrupted hours, or SmartScreen reputation — each of which is named below as
a blocker rather than quietly passed.

> **Engineering complete is not the same as release-qualified.** This pass makes EchoForge build a
> real, signed-capable installer and pass every gate that can be run here. It does **not** declare
> Phase 6 finished: several external gates could not be run in this environment and remain blockers.

> **Phase 3 summary-quality acceptance gate remains NOT RUN**, blocked on human corpus data that does
> not exist in the repository. Nothing in this pass touches it or makes any summary-quality claim.

---

## 1. The NuGet anomaly was the environment, not the repository

The Pass 1 hand-off reported `dotnet restore` failing with
`NuGet.targets(782,5): error : Value cannot be null. (Parameter 'path1')`, and suspected a
machine-wide NuGet configuration fault. It was reproduced and diagnosed here, and it is neither
machine-wide configuration nor an EchoForge defect.

- `dotnet nuget list source`, `dotnet nuget config paths`, and `dotnet nuget locals` all succeed and
  show a clean two-source configuration (`nuget.org` and the VS offline folder).
- A throwaway console project **outside** EchoForge restores cleanly. A single EchoForge project
  restores cleanly. Only a **multi-project / solution** restore fails, and **which** projects fail
  changes from run to run.
- The stack trace is the tell:
  `System.ArgumentNullException … at System.IO.Path.Combine … at
  NuGet.Build.Tasks.GetRestoreSettingsTask.<>c.<.cctor>b__87_0()` — a **static-constructor** lambda,
  whose result a `Lazy<T>` caches (including a thrown exception) for the life of the process.

That is a **poisoned persistent MSBuild worker node**: a build node whose `GetRestoreSettingsTask`
static initializer threw once (a transient null folder-path in the environment it was first spawned
with) and, because the failure is cached, re-throws for every restore routed to it thereafter. Fresh
nodes and fresh processes succeed, which is exactly the non-determinism observed.

**Resolution — environment only, no repository change:**

```
dotnet build-server shutdown
```

clears the poisoned nodes. After it, `dotnet restore EchoForge.slnx`, `dotnet build --warnaserror`
and the full test suite all pass. No EchoForge project file was altered to work around it, and no
package-source integrity was weakened. This is recorded so the next person who meets it reaches for
the one-line fix instead of editing the build.

---

## 2. Inno Setup, pinned and verified

The installer is built by a pinned compiler, staged by automation rather than assumed to be on a
developer's machine. `scripts/stage-inno.ps1` downloads it, checks it three ways, and lays it down as
a portable install that touches no registry and needs no administrator.

| Fact | Value |
|---|---|
| Tool | Inno Setup **7.0.2**, x64 edition |
| File | `innosetup-7.0.2-x64.exe` |
| Source | `github.com/jrsoftware/issrc` release `is-7_0_2` (the project's own GitHub releases) |
| Size | 17,020,192 bytes |
| SHA-256 | `5ad54ca3def786f8f4212552e54cc6d8d61329e2d24a1cfee0571d42c2684ff1` |
| Authenticode | **Valid**, `CN=Pyrsys B.V.` (Inno's current maintainer) via Sectigo, timestamped |
| Signer thumbprint | `E0AB19C8D38CBF9C44709925122A7A02F8C70CB7` |
| Licence | Inno Setup licence (modified BSD/zlib style) — free for commercial installer building; copyright notices preserved |
| Staged compiler | `build/tools/inno-7.0.2/ISCC.exe` (portable, git-ignored) |

The pinned facts are recorded in `build/tools/inno-tool-identity.json` and copied into the release
manifest. The paid "commercial licence" on the download page buys support and an update window; it is
not required to use the compiler — upstream `license.txt` grants use "for any purpose, including
commercial applications". **Flagged for human legal sign-off** before public distribution, as with
any third-party licence.

---

## 3. The installer

`packaging/inno/EchoForge.iss` is now a production per-user installer, not a scaffold.

- **Per-user, no elevation.** `PrivilegesRequired=lowest`; installs under
  `%LOCALAPPDATA%\Programs\EchoForge`. No service, no driver, no machine-wide component, no startup
  task. A standard user can install it.
- **x64 only.** `ArchitecturesAllowed=x64compatible` and `ArchitecturesInstallIn64BitMode`, because
  every pinned inference artifact is `win_amd64`. `MinVersion=10.0.22000` (Windows 11).
- **Stable upgrade identity.** A fixed `AppId`; `UsePreviousAppDir`; `CloseApplications` so an upgrade
  can replace a running EchoForge. An upgrade replaces rather than installing a second copy, and does
  not duplicate shortcuts or Add/Remove-Programs entries.
- **Downgrade refused.** `InitializeSetup` reads the previously-installed version from Inno's per-AppId
  data store and declines to install over a newer one, because an older EchoForge cannot safely read a
  data root a newer one wrote. The refusal is explicit, not silent.
- **Uninstall preserves user data by default.** Nothing in the uninstaller points at
  `%LOCALAPPDATA%\EchoForge`; recordings, transcripts, summaries, models and the app-local runtime are
  left in place so a reinstall finds them again. An optional "also delete my data" path exists, is
  **twice-confirmed**, defaults to keeping the data, and a **silent uninstall never reaches it**.
- **Refuses an incomplete input.** Compile-time `#error` guards reject a package that is missing
  `package.json`, the executable, the manifest, the worker package, the notice, or — the important one
  — the self-contained .NET runtime and WPF. A half-staged directory fails at build, loudly.
- **Add/Remove Programs, shortcuts, metadata.** Start-menu shortcut, optional unticked desktop
  shortcut, correct version/publisher, uninstall registration, uninstall display icon from the app.

**Compiled installer:** `EchoForge-0.6.0-win-x64.exe`, **53,158,025 bytes (50.7 MB)**,
SHA-256 `b3bcf1f4621a530eca3eadf5176fe7eafd3c997e2886a2104541ff3ad96c1697` (unsigned development build).

### An integrity defect this pass found and fixed

Compiling the first real installer revealed that the published package carried
`worker\.venv\` — **535 stray `.py` files** from the developer's virtual environment (pytest,
pygments, and the rest) — because the App project's content glob was over all of `worker\`. The
worker ships as its `echoforge_worker` package only; the runtime environment is built app-locally at
setup, never bundled. Excluding the dev environment dropped the package from **864 files / 166 MB to
329 files / 159 MB** and removed development-only dependencies that had no business in a distribution
(and no entry in the third-party notices). Fixed in the App project; guarded by a new package check
and a test.

---

## 4. The release pipeline

One ordered pipeline (`scripts/release.ps1`), so a release is produced the same way each time and a
signed artifact is never modified after signing:

1. build, test, publish, stage (`package.ps1`)
2. validate the package (`validate-package.ps1`)
3. sign the payload binaries (`sign.ps1`)
4. compile the installer (`build-installer.ps1`)
5. sign the installer
6. verify every signature
7. hash the final artifact
8. write the release manifest

- **`validate-package.ps1`** is the installer's input gate: self-contained win-x64, version
  single-sourced from `Directory.Build.props`, every load-bearing file present, no foreign-architecture
  natives, no repository path leaked into a shipped file, no signing material, no developer virtualenv.
- **`sign.ps1`** signs and timestamps with a certificate that must come from the environment
  (`ECHOFORGE_SIGNING_THUMBPRINT` or a PFX path + password), verifies the result, and **never**
  generates a self-signed certificate or reads a key from the repository. In `-Release` with no
  certificate it fails with the blocker message rather than shipping unsigned.
- **`release.ps1 -Release`** with no certificate still writes the unsigned installer and the manifest
  for inspection, then exits non-zero — a release can never be *declared successful* without
  signatures.

**Release manifest** (`build/installer/release-manifest.json`) records non-secret facts only:
version, installer filename/size/SHA-256, package facts (329 files, self-contained), build tools
(.NET SDK 10.0.302, Inno 7.0.2 with its hash/thumbprint/provenance/licence), signing status, the
artifact-manifest digest, the Python/llama.cpp/faster-whisper identities, and the default and optional
model identities. No user data.

---

## 5. Tests

`tests/EchoForge.UnitTests/InstallerTests.cs` — 16 tests. Fourteen are ordinary unit tests that need
neither Inno nor a certificate: version single-sourcing, stable AppId, x64-only, per-user privilege,
no hardcoded Program Files, uninstall never targeting the data root, twice-confirmed opt-in data
removal, downgrade refusal, the self-contained compile-time guards, the signing hook with no embedded
certificate, signing secrets from the environment only, release-mode unsigned refusal, no signing
material in the repository, and notice/licence staging. Two are marked `[PackagingFact]` and skip
when the compiler is not staged: one compiles the installer from a complete stub package and expects
success, the other removes the self-contained runtime from the stub and expects the guard to reject
it.

Existing deterministic coverage this pass relies on rather than duplicating:
- **Interrupted download and resume** — `ArtifactRegistryTests`:
  `AnInterruptedDownloadKeepsWhatItGotAndResumes` (a real truncated transfer, `.partial` kept,
  resumed by a range request), `AServerThatIgnoresRangeRequestsIsHandledByStartingAgain`,
  `APartialFileLongerThanThePinnedSizeIsDiscardedRatherThanResumed`, against a local `LoopbackHttpServer`.
- **Crash / forced-termination recovery** — `FinalizationCrashWindowTests` kills at each finalization
  seam and asserts the audio survives; `MultiEpochRecoveryTests`,
  `RuntimeInstallationTests.AnInterruptedUnpackLeavesNothingThatLooksInstalled`.
- **No-GPU / CPU path** — `RecommendationTests` drives the recommender with injected hardware
  (empty GPUs, `CudaAvailability.Unknown`, no AVX2) and asserts the CPU profiles and the
  recording-only fallback.

---

## 6. Reliability harness

`scripts/soak.ps1` is a deterministic soak harness. It launches the published application under a
disposable data root with a Windows-only PATH, samples working set, handles, threads and any
inference **child** processes (tracked by parentage and PID, so unrelated processes on a shared
machine are never blamed on EchoForge), and at the end fails on sustained memory growth, a runaway
handle count, or a leaked inference process. Default is a short smoke; `-Hours 3` runs the gate.

**Smoke run (this session):** a ~90-second smoke of the published application over 9 samples —
working set stable at **143 → 144 MB (×1.01)**, handle count flat (peak 728), no inference children,
**PASS**. This proves the harness and the idle-shell stability; it is a smoke, not the gate.

**The three-hour soak is a duration, not a label. THREE-HOUR SOAK NOT RUN** — it was not run in this
session. The harness is deterministic and ready to run on a machine that can give it three
uninterrupted hours. The harness exercises the running application (startup, recovery, the composed
window, background timers); it does not drive a live recording workload, which needs real audio and a
window a headless run cannot supply — that path is covered by the crash-window and lifecycle unit
tests instead.

---

## 7. Security scanning

- **Windows Defender** (active, real-time protection on) scanned the compiled installer:
  **no threats found** (`MpCmdRun -Scan -ScanType 3`).
- **SmartScreen reputation: NOT ESTABLISHED.** SmartScreen reputation is earned by a signed binary
  accumulating distribution history; an unsigned, never-distributed installer has none, and a local
  "it launches" is not the same thing. This is an external blocker, not a claim that can be made here.

---

## 8. Authenticode signing

The signing hook is implemented end to end: `sign.ps1`, the `.iss` `SignTool` route for the installer
and its uninstaller, and the release pipeline's ordering (sign binaries → compile → sign installer →
verify → hash). It reads credentials only from the environment, never self-signs, and refuses to call
an unsigned build a release.

**AUTHENTICODE RELEASE SIGNING BLOCKED — no distribution code-signing certificate available.** No real
distribution certificate exists in this environment. No self-signed certificate was generated, no key
or password was committed, and no SmartScreen trust is claimed. To produce a signed release, set
`ECHOFORGE_SIGNING_THUMBPRINT` (or `ECHOFORGE_SIGNING_PFX` + `ECHOFORGE_SIGNING_PFX_PASSWORD`) from a
secure location outside the repository and run `scripts/release.ps1 -Release`.

---

## 9. Licence review

Reviewed against what is actually bundled and what is actually downloaded:

- All **36** manifest artifacts name a licence **and** a retained licence file, and every one of those
  files is staged in the package (33 licence texts ship). `verify-models.ps1` fails the build if any
  artifact lacks retained licence text.
- `third_party/NOTICE.md` ships and is accurate: it distinguishes bundled components (.NET/WPF,
  NAudio, SQLite native, the worker package) from downloaded ones (the inference wheels, llama.cpp,
  the CUDA redistributables, the models), and states plainly that the models and CUDA libraries are
  downloaded rather than bundled.
- After the venv fix, the only Python shipped is the 17-file `echoforge_worker` package; no
  undocumented third-party Python remains in the distribution. (Before the fix, pytest/pygments and
  others shipped without a notice — the fix closed that gap incidentally.)

**Matters flagged for human legal sign-off** (facts recorded, no legal conclusion drawn): the Gemma
Terms of Use (not a standard open-source licence; accepted by the user at model-download time), the
NVIDIA CUDA redistributable terms (downloaded only for the GPU summary profile), the .NET
redistributable terms, and the Inno Setup licence for the generated installer stub. Ministral remains
optional and never installed by default.

---

## 10. Gate status — what ran, and what is blocked

| Gate | Status |
|---|---|
| NuGet restore anomaly | **Diagnosed & resolved** (poisoned MSBuild node; `build-server shutdown`) |
| Inno Setup pinned/verified | **Done** — 7.0.2, hash + Authenticode verified |
| Production installer builds | **Done** — compiles, guards enforced |
| Per-user install (no elevation) | **Done** in the installer design; **install/run on a clean machine NOT RUN** |
| Upgrade identity | **Done** in design; **live upgrade over an install NOT RUN** (needs a clean machine) |
| Downgrade policy | **Done** — refusal implemented and tested (logic); **live NOT RUN** |
| Uninstall preserves data | **Done** in design + test; **live uninstall NOT RUN** |
| Package input validation | **Done** — `validate-package.ps1` + compile-time guards + tests |
| Interrupted download / resume | **Covered** by deterministic `ArtifactRegistry` tests; **live first-run download NOT RUN** |
| Crash / forced-termination recovery | **Covered** by crash-window + recovery unit tests; **VM power-off NOT RUN** |
| No-GPU / CPU path | **Covered** by injected-hardware recommender tests; **physical no-GPU machine NOT RUN** |
| Soak (resource stability) | Smoke **run**; **THREE-HOUR SOAK NOT RUN** |
| Antivirus (Windows Defender) | **Run** — no threats on the installer |
| Release manifest | **Done** |
| Licence review | **Done** (accurate; items flagged for legal) |
| **Clean Windows VM qualification** | **NOT RUN** — no qualifying clean VM (Windows Sandbox / Hyper-V unavailable) |
| **Standard-user install/uninstall on a clean machine** | **NOT RUN** — needs the clean VM |
| **Network-blocked end-to-end run** | **NOT RUN** — needs the clean machine; offline env behaviour covered by unit tests |
| **Non-ASCII user profile** | **NOT RUN** — cannot safely create such a Windows profile here (published-layout non-ASCII *path* is covered by the Pass 1 smoke) |
| **Sleep / resume** | **NOT RUN** — cannot suspend the host in this session |
| **Authenticode release signing** | **BLOCKED** — no distribution code-signing certificate |
| **SmartScreen reputation** | **NOT ESTABLISHED** — requires a signed, distributed binary over time |
| **Phase 3 summary-quality gate** | **NOT RUN** — blocked on human corpus data (separate from Phase 6) |

---

## 11. Remaining Phase 6 blockers

Engineering for Phase 6 Pass 2 is complete: EchoForge builds a real, signable, per-user installer,
with the pipeline, validation, tests, harness and manifest around it. It is **not** release-qualified,
because these gates could not be run here and remain blockers:

1. **A clean Windows 11 VM** — for standard-user install/uninstall, first-run download and resume over
   the machine's own connection, network-blocked end-to-end, and restart-still-works-offline. No VM
   tooling (Windows Sandbox / Hyper-V) is available on this host.
2. **A machine with no discrete GPU** — the CPU path is covered in automation, but "tested on a no-GPU
   machine" needs one.
3. **A non-ASCII Windows user profile** — the installation *path* is covered; the *profile* path is not.
4. **A real distribution code-signing certificate** — signing is implemented and refuses to fake it.
5. **Three uninterrupted hours** for the soak gate.
6. **SmartScreen reputation** — follows from a signed, distributed binary over time.
7. **Sleep/resume and VM power-off** on a machine that can be suspended and snapshotted safely.

And, separate from Phase 6 and unchanged:

8. **The Phase 3 summary-quality acceptance gate** remains **NOT RUN**, blocked on the human-corrected
   development corpus and the held-out release corpus, neither of which exists in the repository.
