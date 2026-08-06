# Phase 2A, Pass 1 — worker protocol, supervisor, and deterministic worker

**Date:** 2026-08-06
**Scope:** the foundation Phase 2 transcription is built on, and nothing that requires a model.

This pass exists so the parts of local transcription that are *not* speech recognition can be
finished and proven first: the wire format, the process lifetime, the timing arithmetic, and the
shape of a canonical transcript. Those are the parts a model would otherwise obscure. When
faster-whisper arrives it replaces one class behind one interface; it does not move the protocol,
the supervisor, the schemas, or the transcript contract.

**No production inference artifact was downloaded, added, or referenced.** `artifacts/manifest.json`
is still deliberately empty and `scripts/verify-models.ps1` still passes, which is what forbids one.

## What exists

| Piece | Where |
|---|---|
| Worker protocol schema | `schemas/worker-protocol.schema.json` |
| Transcript schema | `schemas/transcript.schema.json` |
| Protocol messages and codec | `src/EchoForge.Contracts/Workers/` |
| Transcript and stage contracts | `src/EchoForge.Contracts/Transcripts/` |
| Session → request conversion, transcript validation | `src/EchoForge.Core/Transcripts/` |
| Supervisor, Job Object, Python discovery | `src/EchoForge.Infrastructure/Workers/` |
| Python worker | `worker/echoforge_worker/` |
| Worker tests | `tests/worker_tests/` |
| Host and end-to-end tests | `tests/EchoForge.UnitTests/Worker*.cs`, `TranscriptContractTests.cs` |

## The protocol

Version **1**, newline-delimited JSON over the child's stdin and stdout. One JSON object per line.
Blank and whitespace-only lines carry no meaning and are skipped by both sides. Messages never
carry transcript text or audio; bulk data is an immutable file referenced by path. Technical
diagnostics go to stderr and never to stdout.

Every message names `protocol_version` explicitly. A version the receiver does not speak is refused
**before its body is looked at**, on both sides. Parsing the fields of an unknown version is how a
build mismatch turns into a silent misinterpretation instead of a clear failure.

### Message flow

```
host                                   worker
 |-- hello ---------------------------->|      supported_protocol_versions, host_version
 |<------------------------------ ready |      supported_protocol_versions, backends
 |-- start_job ------------------------>|      exactly once, job_id + transcription request
 |<---------------------------- started |      backend, recognizes_speech
 |<--------------------------- progress |      zero or more
 |<---------------------------- warning |      zero or more, non-terminal
 |-- cancel --------------------------->|      optional, idempotent, any time after start_job
 |<-- result | error | cancelled -------|      exactly one, then the process exits
```

Agreement must be mutual. The host refuses a worker whose declared versions do not include one of
its own, and the worker refuses a host whose `hello` does not include one of the worker's. Reading
a message is not the same as being able to work together.

### Failure classes, kept apart

`unsupported_protocol_version`, `protocol_error`, `invalid_request`, `input_missing`,
`input_invalid`, `audio_unreadable`, `backend_unavailable`, `backend_failed`,
`output_write_failed`, `internal_error`.

**Timeout is deliberately not in that list.** A timeout is the host's verdict on a silent child,
never something a child claims about itself. On the host side the outcomes are `Succeeded`,
`Failed`, `Cancelled`, `TimedOut`, `ProtocolError`, `Crashed`, `LaunchFailed`, and `Busy` — kept
distinct because the host must react differently to each. A version mismatch that looked like a
flaky model would be diagnosed for weeks.

### Time and attribution

All request and transcript times are **session-relative seconds** on the single merged timeline.
Chunk offsets in a session snapshot are relative to their epoch, because that is what the recorder
can know without carrying a session-wide frame counter across a pause.
`TranscriptionRequestBuilder` is the only place that conversion happens.

Two rules govern it:

- **Epoch placement is forced monotonic.** An epoch never begins before the previous one's audio
  ends, so a clock that jumped backwards across a suspend cannot produce overlapping epochs and
  make every downstream bound check meaningless.
- **Epoch length comes from the audio, not the wall clock.** A wall-clock epoch can outlast the
  audio it produced — a device died halfway through — and a transcript must not be able to place a
  segment where nothing was captured.

Speaker attribution is **not a request parameter**. Microphone content is `You` and system content
is `Remote`, derived from `source_track` on both sides of the pipe. There is no field a caller
could fill in wrongly, and the JSON Schema enforces the same pairing independently of the code.

## The supervisor and the Job Object

`WorkerSupervisor` starts one worker, has one conversation, and guarantees nothing survives.

- The Job Object is created **before** the process and assigned immediately after start. The
  worker's first act is to wait for `hello` on stdin, so it does nothing at all in the gap between
  starting and being contained.
- `KILL_ON_JOB_CLOSE` means the tree dies when the handle is released, even if the host crashes
  without calling anything. Terminating explicitly is the fast path; the handle is the guarantee.
- Killing the child alone would not be enough later: a worker that loads an inference runtime will
  start helpers, and a stranded process holding VRAM is exactly what makes the *next* job fail for
  no visible reason. A test stub spawns such a helper and refuses to finish, and the test observes
  the helper's heartbeat stop.
- If a Job Object cannot be created the run continues with a direct process-tree kill and records
  that it is running without the stronger guarantee. Failing a job because a containment mechanism
  was unavailable would be worse than running with the weaker one and saying so.

Other supervisor behaviour worth stating plainly:

- **Cancellation** sends `cancel`, waits a grace period for the worker to stop at a safe boundary,
  and only then kills the tree. A cancelled job leaves its sources and any previous revision
  untouched, and writes no transcript.
- **Timeout** kills immediately with no grace period. It has already had all the time it was given.
- **stderr is drained continuously** into a bounded tail. Merely redirecting it would let a chatty
  child fill the pipe and block forever, and that deadlock would look exactly like a slow model.
- **The result's digest is verified against the bytes on disk.** A worker that describes a file it
  did not write is a broken worker, not a failed transcription, and activating a canonical revision
  on an unverified file is not acceptable.
- **Anything said after the terminal message** — a duplicate result, late progress, a garbled line
  — is recorded in `ProtocolViolations` but does not retract verified work. The transcript was
  written and checked before the extra chatter arrived; discarding it because the child was rude
  afterwards would be the worse failure.
- **User-facing text is generated from the outcome and error code alone.** It cannot contain a
  path, a session identifier, worker output, or stderr, because those may carry meeting content.
  The technical detail goes to the log instead.

**Recording has priority.** `ICaptureActivityGate` is checked before launch; while capture may be
live the supervisor answers `Busy` without starting anything. `RecordingCaptureGate` asks
`RecordingController.CaptureMayBeLive` rather than `IsCapturing`, so the answer stays conservative
while capture is stopping. A queue can be built on this seam without relaxing the rule to add it.

## The Python worker

Python 3.12, standard library only. No runtime dependencies exist in this pass; `pytest` and
`jsonschema` are development dependencies, locked in `worker/uv.lock`.

The worker runs one job and exits. There is no service, no port, and no state that outlives the
process. Losing stdin counts as a cancel, so a worker whose host has gone does not keep
transcribing for nobody even if the Job Object somehow failed.

Source WAVs and their metadata are opened read-only and never rewritten. The transcript is written
to a temporary neighbour, flushed, fsynced, and moved into place atomically, so a crash mid-write
cannot leave a half-transcript wearing the real name.

### The placeholder backend performs no speech recognition

`MockBackend` reads the real audio, finds where energy exists on a fixed window grid, and derives
filler words from a SHA-256 of those exact bytes. Identical audio produces a byte-identical
transcript; silence produces no segments at all.

It says so in three places that are hard to miss:

- `recognizes_speech: false` on the `started` message,
- `model.recognizes_speech: false` in the transcript,
- every segment's text begins with a `[mock]` marker.

Segment `confidence` and word `probability` are `null`, because no calibrated score exists and
inventing one would be worse than having none. The language is `und` — undetermined, not detected.

The backend interface takes a **whole track**, not one chunk at a time. That is deliberate:
60-second source chunk boundaries are not speech boundaries, and the production recogniser forms
its own ten-minute windows with overlap across them. An interface shaped around single chunks would
have to be rewritten to allow that, and the protocol would move with it.

### Fault injection

Twelve modes — delay, crash, nonzero exit, invalid JSON, unknown message, malformed progress,
malformed result, duplicate result, output after completion, stderr noise, and hang — are locked
behind `ECHOFORGE_WORKER_ALLOW_TEST_MODES=1`. The application never sets it. A mode requested
without it is **refused with a warning rather than silently ignored**, because a test that believed
it injected a fault and did not would pass for the wrong reason.

## Verification

| Check | Result |
|---|---|
| `dotnet build EchoForge.slnx -c Debug --warnaserror` | 0 warnings, 0 errors |
| `dotnet test EchoForge.slnx -c Debug` | **292 passed**, 0 failed, 0 skipped |
| `scripts/run-worker-tests.ps1 -Frozen` | **72 passed** |
| `scripts/verify-models.ps1` | PASS (0 entries — no model may be downloaded) |

The .NET suite includes a real `C# → Python → C#` protocol smoke test, not only mocks of the
process layer. Tests needing the worker skip with a message naming what to install when Python 3.12
is absent, rather than failing in a way that reads like a defect in EchoForge.

Transcripts are validated by `jsonschema` against the published schema in the Python suite, so
"schema-valid" means something other than "self-consistent", and by `TranscriptValidator` on the
host for the invariants a schema cannot express: ordering, epoch containment, word containment, and
attribution.

## Running the tests

```powershell
dotnet test C:\EchoForge\EchoForge.slnx -c Debug
powershell -NoProfile -ExecutionPolicy Bypass -File C:\EchoForge\scripts\run-worker-tests.ps1
```

Python is discovered in this order: `ECHOFORGE_PYTHON`, then `py -3.12`, then `python3.12`,
`python3`, and `python` — accepting only 3.12 or newer. Nothing is hard-coded to a machine.

## What Phase 2A still needs

Pass 1 stops at the foundation. None of the following exists yet:

1. **Revision storage and activation.** `transcript.vN.json` naming, atomic activation of a new
   revision, retention while a summary references one, and recording the outcome on
   `TranscriptionStage` in the session snapshot and journal. The stage type exists; nothing writes
   it yet.
2. **Application UI.** No Transcribe or Transcribe again action, no model or profile selector, no
   per-track progress, no cancel button, no hardware summary. `RecordingCaptureGate` exists but is
   not wired into the app's composition.
3. **A queue behind the busy seam.** The supervisor refuses to run while capture is live; nothing
   yet remembers the request and runs it afterwards.
4. **SRT, VTT, and TXT exports.** The canonical JSON exists; the cue builders do not.

And these are Phase 2B or later, deliberately untouched here:

- Production faster-whisper and CTranslate2 integration, and the `artifacts/manifest.json` entries
  that must exist before anything may be downloaded.
- 16 kHz mono aligned audio derivatives, and the derivative-to-source time map.
- Ten-minute transcription windows with five-second overlap, overlap de-duplication, and per-window
  checkpoints.
- Silero VAD, word timestamps from a real recogniser, language detection, and glossary or initial
  prompt support.
- CUDA preflight, adaptive batch sizing, `int8_float16` retry, and the CPU INT8 fallback path.
- Model registry, download, resumption, and hash verification.
- Summarization, diarization, search and library, cloud processing, and live transcription.
