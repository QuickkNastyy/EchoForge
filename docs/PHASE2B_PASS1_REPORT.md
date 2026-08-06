# Phase 2B, Pass 1 — pinned artifacts, verified downloads, derivatives, timing maps, windows

**Date:** 2026-08-06
**Scope:** everything production speech recognition needs before a model is loaded — and nothing
that loads one.

Pass 1 of Phase 2B does the expensive, provable half of production transcription: pinning the
artifacts, fetching them safely, converting the immutable recording into the audio a recogniser
wants, recording how to get back, and dividing the work up. **No inference runs.** faster-whisper
is not imported, CTranslate2 is not executed, Silero VAD is not run, and the deterministic
placeholder backend is still the only thing in EchoForge that produces a transcript.

## What was pinned

Eight entries, all in `artifacts/manifest.json`, all passing `scripts/verify-models.ps1`.

| Artifact | File | Size | Revision |
|---|---|---|---|
| `runtime.faster-whisper` | `faster_whisper-1.2.1-py3-none-any.whl` | 1,118,909 | `65882ee…` (SYSTRAN/faster-whisper `v1.2.1`) |
| `runtime.ctranslate2` | `ctranslate2-4.8.1-cp312-cp312-win_amd64.whl` | 19,220,789 | `0d8bcd3…` (OpenNMT/CTranslate2 `v4.8.1`) |
| `runtime.pyav` | `av-18.0.0-cp311-abi3-win_amd64.whl` | 27,556,236 | `54a4395…` (PyAV-Org/PyAV `v18.0.0`) |
| `stt.large-v3-turbo.model` | `model.bin` | 1,617,884,929 | `0a363e9…` |
| `stt.large-v3-turbo.config` | `config.json` | 2,263 | `0a363e9…` |
| `stt.large-v3-turbo.tokenizer` | `tokenizer.json` | 2,710,337 | `0a363e9…` |
| `stt.large-v3-turbo.vocabulary` | `vocabulary.json` | 1,068,114 | `0a363e9…` |
| `stt.large-v3-turbo.preprocessor` | `preprocessor_config.json` | 340 | `0a363e9…` |

**How each digest was established.** Sizes and SHA-256 values were read from the publisher's own
index — the PyPI JSON API and the Hugging Face model API — by parsing the response directly rather
than through any summarising step. Every entry except `model.bin` was then **downloaded and hashed
locally**, and the local digest matched. `model.bin` is 1.6 GB and uses the Git LFS object digest
the host publishes, which is the content digest of the file itself; that is the one value here not
independently re-computed, and it is called out in the entry's `provenance`.

Licence text is retained in `third_party/licenses/`, taken from each project at its pinned commit
(CTranslate2's wheel carries no `LICENSE` in its `dist-info`, so its text came from the repository
at the same commit). The gate now fails if a `license_file` names something that is not in the
repository.

### Two findings worth stating

**The model alias had already moved.** faster-whisper 1.2.1 still maps `large-v3-turbo` to
`mobiuslabsgmbh/faster-whisper-large-v3-turbo`, and Hugging Face now resolves that to
`dropbox-dash/faster-whisper-large-v3-turbo`. This is precisely the drift the architecture plan
predicted for convenience aliases. The manifest records the **resolved repository and commit**, and
the alias is never used at runtime.

**Silero VAD needs no entry.** faster-whisper ships it inside its own wheel as
`faster_whisper/assets/silero_vad_v6.onnx` (1,245,151 bytes), confirmed by extracting the wheel.
Pinning the wheel already pins the VAD model; adding a separate download would have invented an
artifact that does not exist.

### What could not be pinned, and why

1. **faster-whisper's remaining Python dependencies** — `onnxruntime`, `tokenizers`,
   `huggingface-hub`, `tqdm`, and their own transitive closure (numpy, protobuf, filelock, fsspec,
   requests, and so on). They are genuinely required for `import faster_whisper` to succeed.
   **Blocker:** the closure is dozens of platform-specific wheels, and hand-copying each digest is
   exactly the process most likely to introduce a wrong one. The correct mechanism is a
   hash-locked resolution produced by a resolver — the worker already has `uv.lock` for its
   development dependencies — imported into the manifest wholesale. That is a Phase 2B Pass 2 /
   Phase 6 task and was not attempted here rather than being done unreliably.
2. **CUDA 12 and cuDNN 9 runtime libraries** for the two GPU profiles. **Blocker:** NVIDIA's
   redistribution terms need the release-time review the plan already schedules for Phase 6, and
   CUDA is explicitly out of scope for this pass. The GPU profiles therefore currently resolve to
   the same artifact set as `cpu-int8`; that is honest about what is pinned, and incomplete as a
   GPU profile until those libraries are added.

Nothing was weakened to make the gate pass. The gate gained checks rather than losing any.

### A gate defect fixed on the way

`schemas/artifact-manifest.schema.json` guarded against moving revisions with
`^(?i)(main|master|latest|…)$`. JSON Schema patterns are ECMA-262 and have no inline `(?i)` flag,
so that pattern **fails to compile rather than rejecting anything** — the schema claimed to forbid
`main` while permitting it. It had gone unnoticed because an empty manifest never exercised it and
nothing validated the schema itself. It is now spelled out in character classes, with a test that
compiles the schema and proves a moving reference is refused.

Separately, `artifacts/manifest.json` **was never tracked by Git**: the `.gitignore` rule for build
output shadowed it, so the gate read a file that existed only on this machine and a fresh clone had
nothing to verify against. Both are fixed.

## Downloads

`ArtifactRegistry` reads the manifest and nothing else. There is no URL elsewhere in the codebase,
so requesting an unlisted artifact is not merely refused — there is nothing it could denote. The
manifest is re-validated at run time as well as by the build gate, and a manifest failing any check
is refused whole: half a manifest is not a smaller allow-list, it is an unreviewed one.

- Downloads go to `<file>.partial`, resume with `Range` when the server allows it, and **start
  again** when a server answers `200` to a range request. Appending after a `200` would splice the
  start of the file onto the middle of itself — right length, entirely wrong content.
- Size is enforced twice: against the declared length before the body is read, and against a
  running total during it, because a response with no `Content-Length` has nothing to check up
  front.
- **An interrupted transfer keeps its bytes**; only a *complete* transfer with a wrong digest is
  quarantined to `<file>.rejected`. Conflating the two would make every dropped connection restart
  a 1.6 GB download from zero.
- Activation is a rename after verification. Nothing is ever presented as installed before then.
- Status is `NotInstalled / Downloading / Verifying / Installed / Invalid / Failed`. A file that is
  simply *present*, with exactly the right bytes, reads **Invalid** — unverified bytes have no
  standing. The proof is a marker written only after a digest matched, carrying the length and
  modification time so a later replacement stops counting. Re-hashing gigabytes on every status
  query would make the UI unusable, so `VerifyInstalledAsync` re-establishes it on demand; a test
  proves the cheap check can be fooled by an equal-length edit while the deep one cannot.
- Concurrency is handled at both levels: callers in this process queue on a per-artifact gate, and
  another process is kept out by a lock file, because two instances interleaving range requests
  into one partial file would produce plausible rubbish.
- Cancellation, timeout, 404/403, and an unreachable host are all outcomes with safe messages, not
  exceptions.
- Proxy and system credentials are used, so a managed machine can still install.
- Installed artifacts work with the network gone entirely — verified by a test that disposes the
  server first.

Plain HTTP is permitted **only on loopback**. That is what lets the whole suite run against a
hand-rolled local server: the interesting cases are misbehaviours — ignoring a range, closing
halfway, lying about a length — and none can be arranged reliably against a public host.

## Derivatives

16 kHz, mono, signed PCM16, written to
`<session>/derived/audio/<processing-version>/<track>.wav`.

**Laid out by session time, not by counting.** Each chunk's first and last output frame come from
its own absolute session position. Appending "however many frames this chunk resampled to" would
drift slightly at every boundary; three hours later a transcript timestamp would point at the wrong
sentence. A sixty-chunk 44.1 kHz test asserts each chunk still lands where absolute time says.

**Band-limited resampling.** Taking every third sample from 48 kHz would fold everything above
8 kHz back into the speech band. Each output sample is a 63-tap windowed-sinc weighted sum with the
cutoff set by the lower rate. Weights are precomputed per phase — one phase for 48 kHz, 160 for
44.1 kHz — so cost is per *output* sample. The phase of output frame *j* is `(j × inRate) mod
outRate`, computed from *j* every time and never accumulated, so two machines produce identical
bytes.

Channels are mixed by integer averaging, deterministically. Gaps are written as explicit silence
with explicit spans. The filter reads a little of the neighbouring chunks so a 60-second boundary
leaves no step — but refuses to read across an epoch boundary or a format change, where there is no
continuity to preserve.

Sources are opened read-only throughout. A chunk that disagrees with its metadata **fails the job**
rather than being skipped: skipping would produce a derivative silently missing a minute, with
every later timestamp wrong by the difference. Output is staged and moved atomically, hashed, and
reused only when the source manifest, options, processing version, size, and digest all match.
`ProcessingVersion` is part of the identity, so changing the resampler produces new derivatives
instead of reusing incompatible ones.

## Timing maps

Persisted beside each derivative as `<track>.timing.json` and hashed with it. A map is a list of
spans that **tile the derivative without overlapping**, each one either `Source` — naming the
chunk, epoch, source frame, and source rate — or `Gap`.

Guarantees, each asserted by a test:

- Every derivative frame resolves to exactly one source position, or to an explicit gap.
- **No span straddles an epoch gap**, so no transcript time can be attributed across one.
- Rounding does not accumulate: chunk positions come from absolute session time.
- Mixed source rates across epochs each convert correctly and are recorded per span.
- `SessionSecondsAt` and `FrameAt` round-trip, and `Resolve` maps back into the correct chunk.

## Windows and checkpoints

Ten-minute windows, five-second overlap, planned per track and per epoch and never across either.
Tracks are never combined before transcription — that would destroy the only deterministic speaker
signal EchoForge has. Source 60-second chunk boundaries are ignored entirely; they are a storage
decision.

Each window records a stable ID, track, epoch, derivative path and digest, start and end frame,
session-relative start and end, overlap before and after, and an **input fingerprint** covering
everything the result depends on. Checkpoints are `Pending / Running / Succeeded / Failed /
Cancelled`; a succeeded one is reused only when its fingerprint still matches, and a failed or
cancelled one drops back to pending without disturbing windows that already finished. The plan is
saved to `<session>/derived/windows/<planning-version>/plan.json`.

Writing the tests removed a knob that did nothing: a `MinimumWindowSeconds` option meant to
suppress degenerate final windows **could never fire**, because a final window runs from the
previous window's end minus the overlap and is therefore always at least as long as the overlap.
The option is gone and the property is asserted directly.

Overlap text de-duplication is **not** implemented, as specified. The metadata it will need —
`OverlapBeforeSeconds` and `OverlapAfterSeconds` per window — is recorded.

## Integration

`TranscriptionCoordinator.PrepareAsync` installs artifacts, builds derivatives, and plans windows,
then stops. It is deliberately separate from `Request`, which still runs the deterministic
placeholder exactly as before.

- Recording still wins: preparation is refused while capture is live, and counts as the one heavy
  job, so it cannot run alongside a transcription.
- Source chunks are re-verified before anything is downloaded.
- Preparation is optional composition. Without a valid manifest or a Python runtime the panel does
  not appear and recording is unaffected.

The window distinguishes, separately from the transcription stage: mock transcription, artifacts
missing (with the download size), downloading and verifying, preparing audio, planning windows, and
ready for production transcription — with "Recognition itself is not implemented in this build"
stated on screen.

## Verification

| Check | Result |
|---|---|
| `dotnet build EchoForge.slnx -c Debug --warnaserror` | 0 warnings, 0 errors |
| `dotnet test EchoForge.slnx -c Debug` | **461 passed**, 0 failed, 0 skipped |
| `scripts/run-worker-tests.ps1 -Frozen` | **77 passed** |
| `scripts/verify-models.ps1` | PASS, 8 entries |
| Application launch | Window opens; production panel, both preparation buttons, and the "not implemented" statement present |

## Remaining Phase 2 work

**Phase 2B, Pass 2 — inference**

1. Import the hash-locked dependency closure (`onnxruntime`, `tokenizers`, `huggingface-hub`,
   `tqdm`, transitives) into the manifest from a resolver-produced lock, and install the worker's
   production environment from it.
2. A `faster-whisper` backend behind the existing Python `TranscriptionBackend` interface, loading
   the pinned CTranslate2 model from the registry's install path — no alias, no network.
3. Run the planned windows, write per-window checkpoints, and rebase window timestamps onto the
   session timeline through the timing map.
4. Overlap de-duplication across the five-second seams.
5. Silero VAD, word timestamps, language detection, glossary and initial prompt.
6. CUDA preflight, adaptive batch sizing, `int8_float16` retry on OOM, CPU INT8 fallback with an
   explicit non-silent notice — and the CUDA/cuDNN artifacts those profiles need.
7. A model and profile selector in the UI, and a real hardware summary.

**Later phases, untouched:** summarization (3), results and library (4), diarization (5), packaging
(6), cloud (7), and live transcription, which the plan excludes entirely.
