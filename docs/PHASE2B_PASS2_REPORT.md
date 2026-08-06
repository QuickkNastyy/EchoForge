# Phase 2B, Pass 2 — production speech recognition

**Date:** 2026-08-06
**Scope:** the real recogniser, its locked environment, and everything between a window of audio
and a validated transcript.

## The locked production environment

`worker/requirements-production.in` names what EchoForge asks for (`faster-whisper==1.2.1`);
`scripts/lock-worker-runtime.ps1` resolves the closure with uv for Windows / CPython 3.12, picks
the one wheel that platform installs, downloads it, verifies its length and SHA-256 against the
index, extracts its licence, and writes the entries into `artifacts/manifest.json`.

**25 runtime wheels + 5 model files = 30 pinned artifacts.** Nothing was transcribed by hand.
`verify-models.ps1 -Downloaded` re-hashes all 25 wheels and passes; `lock-worker-runtime.ps1
-Check` re-resolves and fails if the committed manifest has drifted.

The generator refuses to write a partial manifest. Its first run stopped on four problems rather
than emitting 21 good entries: three wheels carry no licence text (retained from their projects at
the matching source tags), and `hf-xet` publishes `cp38-abi3`, which a fixed list of recent ABI
versions missed — the selector now reads the abi3 minor version instead of pattern-matching it.

`scripts/install-worker-runtime.ps1` re-verifies every wheel, builds a wheelhouse, and installs
with `--no-index`. Once the bytes are verified there is nothing left to fetch, and an installer
that could still reach the network could still install something the manifest never vouched for.
**Verified working on this machine**, including `ctranslate2.get_cuda_device_count() == 1`.

Two Windows PowerShell traps are handled where they bite: `ConvertTo-Json` is unreviewable in a
diff, and `Set-Content -Encoding utf8` writes a BOM that would make a valid manifest look like
malformed JSON to the schema test. Both go through `scripts/normalize-manifest.py`.

**NVIDIA CUDA and cuDNN are still deliberately unpinned.** EchoForge uses a system-installed
runtime when one is present and falls back to CPU INT8 when it is not; redistributing NVIDIA
binaries needs the release-time review scheduled for Phase 6.

## The real backend

`worker/echoforge_worker/whisper_backend.py` loads a CTranslate2 model **from a directory the host
verified**, with `local_files_only=True`. There is no repository id and no alias: faster-whisper's
aliases resolve to third-party conversions that can move, and one of them already had. Left off,
the library would quietly reach for the Hub and undo the pinning entirely.

The host stages the model: `ArtifactRegistry.TryStageModelDirectory` copies the verified files into
one directory, because CTranslate2 wants `model.bin` and the tokenizer side by side while the
artifact store keeps each file under its own revision so it can be verified alone. A partially
assembled directory is never produced.

**Transcribing never triggers a download.** A profile whose models are absent is refused with what
is needed and how large it is.

The recogniser is injectable, so window slicing, rebasing, gap handling, overlap removal, and
fallback are all proven without model weights on disk.

## Timestamp rebasing

Window results are rebased using each window's **own session start**, never a running total, and
clamped to the window that produced them — a recogniser running past its audio would otherwise
claim time belonging to the next window, and the de-duplicator could not tell that from real
overlap. Words are pinned inside their segments, because a rounding artefact is a poor reason to
fail a whole transcript.

**Speech landing in a gap is dropped.** A window sits on a derivative containing explicit silence
wherever recording was not running, and a recogniser given ten minutes including a pause will
occasionally emit across it; a segment there names a moment when nothing was captured. One
straddling the edge is trimmed instead — something was heard, just not for as long as claimed.

Nothing escapes the session duration, its window, or its epoch. Microphone stays `You`; system
stays `Remote`.

## Overlap de-duplication

Conservative in three ways, each tested:

- **Only inside a known shared region.** A phrase genuinely repeated later in a meeting is never
  touched.
- **Normalised text must match exactly.** Two windows disagreeing about punctuation or casing
  collapse; two producing different words do not, because keeping both is the safer error. The
  original text is always preserved — normalisation is only ever used for comparison.
- **The better-supported result survives**: word timings first, then length, then earliness. The
  window that heard the whole sentence beats the one that caught its tail.

Deterministic regardless of input order.

## Checkpoints and resume

The worker owns per-window checkpoints, written atomically to
`derived/windows/<planning-version>/results/<window-id>.json` the moment a window succeeds. A
checkpoint is reused only when the window's **full input fingerprint** matches — covering the
audio, derivative, profile, boundaries, and planning rules — so it cannot survive a change to any
of them. The model is loaded only if some window actually needs running, so a fully checkpointed
track costs nothing on a resume. A test proves that a failure in window three re-runs only window
three on retry.

## VAD, language, glossary

Silero VAD comes from inside the pinned faster-whisper wheel (`silero_vad_v6.onnx`) — no separate
artifact exists or is needed. `vad_filter` is on by default. Word timestamps are on by default.
Segment confidence stays `null`: Whisper reports an average log probability, which is not a
calibrated confidence, and recording it as one is a lie the schema forbids.

Language is automatic by default and can be forced; a forced language wins over detection. A
glossary and an optional prompt are joined into Whisper's initial prompt, which **biases and does
not guarantee** — the UI tooltip says exactly that.

## Compute profiles and fallback

CPU INT8, GPU `int8_float16`, GPU FP16. CUDA availability is decided by asking CTranslate2 rather
than inspecting drivers: what matters is whether the library that will run the model can use a
device, so a machine with a GPU and a mismatched cuDNN correctly reports zero.

Out of memory retries a smaller batch on the same device (8 → 4 → 2 → 1); **anything else abandons
the GPU immediately**, because a missing cuDNN will not be fixed by a smaller batch. A CPU failure
is final and is raised. The requested profile, actual profile, fallback reason, device, compute
type, batch size, and runtime versions are all recorded, and the UI shows a fallback notice — a job
that asked for the GPU and finished on the CPU took far longer for a reason worth knowing.

All of this is tested by injection: CUDA unavailable, initialisation failure, and OOM, with no
NVIDIA hardware required.

## UI

Backend, compute profile, language, and glossary selectors; download and preparation status;
window progress; fallback notice; cancel; transcribe again; revision selection; exports. Backend
labels always say whether the thing recognises speech, **in both directions** — the placeholder is
labelled as recognising none. Profile and language controls disable for the placeholder. Recording
still takes priority, and nothing slow runs on the UI thread.

## Verification

| Check | Result |
|---|---|
| `dotnet build -c Debug --warnaserror` | 0 warnings, 0 errors |
| `dotnet test` | **470 passed**, 0 failed, 0 skipped |
| `scripts/run-worker-tests.ps1 -Frozen` | **120 passed** |
| `scripts/verify-models.ps1` | PASS, 30 entries |
| `scripts/verify-models.ps1 -Downloaded` | PASS, 25 wheels re-hashed |
| `scripts/lock-worker-runtime.ps1 -Check` | manifest matches a fresh resolution |
| `scripts/install-worker-runtime.ps1` | production environment installs offline and imports |
| Application launch | window opens with backend, profile, language, and glossary controls |

## Remaining Phase 2 limitation

**The end-to-end production smoke test against the real 1.6 GB model has not been run in this
session.** `scripts/smoke-production-backend.ps1` and `scripts/smoke_production_backend.py` exist
and will run it: they assemble the model directory from verified artifacts, refuse if anything is
missing or mismatched, and drive the real backend over a two-window fixture with a five-second
overlap, asserting the compute outcome and that every timestamp is well formed.

The model download was still in progress when this pass closed. Everything else in the production
path is exercised by tests; what remains unproven on this machine is specifically that the pinned
weights load and CTranslate2 executes them here. Run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\smoke-production-backend.ps1
```

Accuracy is a separate matter and is not claimed. The plan's STT evaluation — word and name
accuracy, timestamp accuracy, hallucination on silence and music — needs real recorded meetings and
is a Phase 3 gate, not something a synthetic fixture can establish.
