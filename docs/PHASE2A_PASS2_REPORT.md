# Phase 2A, Pass 2 — revisioned storage, coordination, application surface, exports

**Date:** 2026-08-06
**Scope:** everything needed to produce, keep, choose, and export a transcript — using the
deterministic worker from Pass 1, and nothing that requires a model.

Pass 1 proved the pipe. This pass makes what comes out of it durable, choosable, and exportable,
and puts it in front of a user. **No production inference artifact was downloaded, added, or
referenced.** `artifacts/manifest.json` is still deliberately empty and `scripts/verify-models.ps1`
still passes, which is what forbids one.

## Revision storage layout

```text
%LOCALAPPDATA%\EchoForge\sessions\YYYY\MM\<session-id>\
├─ events.jsonl                       # authority: recording *and* processing history
├─ session.json                       # rebuildable recording projection
├─ processing.json                    # rebuildable processing projection
├─ tracks\…                           # immutable source audio, never touched by processing
└─ transcript\
   ├─ transcript.v1.json              # an activated revision, immutable
   ├─ transcript.v2.json
   └─ transcript.v3.json.staging      # an attempt in flight, or the remains of one that died
```

The authority model is the one recording already uses, for the same reason. **The journal says
which revisions were activated; the files say which ones still exist.** A transcript file that no
`transcription_activated` event vouches for is not a revision, and a revision whose file has gone
is not selectable. Trusting either half alone is how a half-written file ends up presented as the
transcript of a meeting.

`processing.json` is a projection in exactly the sense `session.json` is: convenient, rebuildable,
and never asked whether something was activated. Deleting it loses nothing. A test asserts that a
projection claiming a revision the journal never activated does not win.

### Journal events

`transcription_queued`, `transcription_started`, `transcription_activated`, `transcription_failed`,
`transcription_cancelled`, `transcription_revision_selected`, `transcription_staging_discarded`.

They carry identities, digests, counts, and codes only. **No transcript text enters the journal**,
and a test walks every field of every event to prove it. Progress is not journalled either — a line
per progress update would turn a recovery ledger into a log, and progress is worth nothing after a
restart anyway. It lives in the projection.

### What each revision records

Revision number, job ID, creation time, relative path, transcript SHA-256, **source manifest
SHA-256**, segment count, duration, backend, model ID, profile, worker version, protocol version,
and `recognizes_speech`. The two digests are what make "the audio changed underneath this
transcript" and "this file is not the one we activated" detectable rather than assumed.

## Atomic activation

1. The worker writes to `transcript.vN.json.staging`, itself atomically (`.partial`, fsync,
   rename) — so its own crash cannot leave a half-file wearing the staging name.
2. The supervisor verifies the reported digest against the bytes on disk.
3. The coordinator deserialises, checks the session ID, checks the source manifest digest, and runs
   the full transcript validator.
4. The store re-hashes the staged bytes, refuses an empty file, refuses an existing revision
   number, flushes to durable storage, and `File.Move`s into place.
5. **Only then** is `transcription_activated` appended.

Ordering that last step matters. A crash between the rename and the append leaves a file nothing
vouches for, which startup discards. A crash the other way round would leave the journal claiming a
revision that does not exist.

**Revision numbers are allocated before any work starts and never reused.** An attempt that dies
leaves a staging file carrying the number it was writing; reusing that number would make two
different runs indistinguishable. Startup discards every `.staging` and `.partial` file under
`transcript\`, so a crash before activation leaves the previous revision selected and nothing else
changed.

**A failed or cancelled attempt can never retract a good revision.** An attempt that produced
nothing has no standing to replace one that did. Every failure path ends with the previously
selected revision still selected, its bytes unchanged, and the staged file discarded.

## Recording priority: cancel and requeue

The policy is one sentence: **capture always wins, and the request is never dropped.**

- A request made while capture is live is **durably queued** (`transcription_queued` is journalled)
  and starts by itself when capture stops.
- Capture starting mid-job **cancels the worker at a safe boundary** and queues the request again
  as a **fresh attempt with its own revision number**, so the journal can tell the two runs apart.
- The coordinator is driven by the recorder's own `StateChanged` event, not by polling, so the
  reaction is immediate and a test can know exactly when the worker was asked to stop.

Suspending the worker instead was considered and rejected: a suspended Python process still holds
its memory and, once a real model is loaded, its GPU allocation — precisely the resources the
policy exists to release.

**Only one job runs at a time**, across all sessions. A second request is refused as `Busy`.
Transcription is the heavy stage, and two of them would compete for the same GPU while telling the
user both were progressing.

## Source verification

Before a job starts — and again immediately before the worker runs, because a deferred job may
have waited a long time — every source chunk is checked: the session is in a settled state, no
epoch is still open, every chunk file exists, resolves inside the session, and **still matches the
SHA-256 recorded when it was finalized**. A session that fails is refused with a safe sentence and
left exactly as it is. Nothing in the processing path writes, moves, or repairs source audio.

## Application surface

- `Transcribe`, `Transcribe again`, `Cancel`, and `Export…`.
- Current stage, progress percentage, and a description naming the track — "Transcribing your
  microphone — chunk 1 of 2".
- Selected version, plus a selector listing every previous successful version; choosing one is
  durable.
- A standing warning that this build's backend performs no speech recognition. It shows **before
  anything has run**, because it is about what the user is about to get, not only about what they
  already have. Each version in the list is labelled as a placeholder too.
- A backend summary taken from the worker's own handshake — its build, its interpreter, its
  available backends — rather than inferred by the host, which cannot know which Python resolved.
- Actions disable during recording, during startup recovery, while shutting down, and while
  another job holds the coordinator. Availability uses `CaptureMayBeLive`, so they stay disabled
  while capture threads are winding down rather than the instant Stop is pressed.

Nothing slow runs on the UI thread: requesting hashes the audio, reading a revision verifies its
digest, exporting writes a file, and Python discovery starts processes. All of it is off-thread,
and a test asserts the command returns in under 250 ms while a job runs for seconds.

Transcription is **optional composition**. On a machine with no usable Python 3.12 the panel simply
does not appear and the recorder works exactly as before. A missing processing dependency must not
stop anyone recording a meeting.

## Exports

Canonical JSON, plain text, SubRip, and WebVTT, written from the validated selected revision.

- **Canonical JSON is copied byte for byte**, never re-rendered. Re-serialising could change number
  formatting or key order, and the exported file would no longer hash to the digest its revision
  was activated under.
- **Deterministic**: no local time, no culture-dependent formatting, a fixed `\n` line ending, and
  no byte-order mark. Exporting the same revision twice gives identical bytes.
- **Valid cues**: sorted chronologically, never negative, never backwards, never zero-length (a
  zero-length segment is widened to 1 ms rather than emitted as a flash). Newlines inside segment
  text are flattened, because one would end a cue early and turn the rest into a malformed timing
  line.
- Speaker labels (`You:` / `Remote:`) on every cue; WebVTT cues are identified by segment ID so a
  cue traces back to the exact segment of the exact revision.
- Written to a temporary neighbour and moved into place. **An existing file is never replaced**
  unless the user confirmed it in the save dialog.
- An export reads the canonical revision and writes somewhere else, so a failed export cannot
  damage what it was reading — asserted directly by a test.

## Verification

| Check | Result |
|---|---|
| `dotnet build EchoForge.slnx -c Debug --warnaserror` | 0 warnings, 0 errors |
| `dotnet test EchoForge.slnx -c Debug` | **381 passed**, 0 failed, 0 skipped |
| `scripts/run-worker-tests.ps1 -Frozen` | **72 passed** |
| `scripts/verify-models.ps1` | PASS (0 entries — no model may be downloaded) |
| Application launch | Window opens; transcription panel and placeholder warning present |

Two corrections fell out of writing the tests, both worth naming:

- Progress now reports **inline on the supervisor's ordered read loop** rather than through
  `Progress<T>`, which posts each report as a separate work item and can deliver them out of order.
  The visible symptom would have been a progress bar that jumps backwards.
- The **live job state is overlaid by the coordinator**. The store reconstructs from the journal
  and cannot tell a running attempt from one whose process died, so it reports the latter — correct
  after a restart, wrong while the work is under way. Only the coordinator knows a job is alive.

## What Phase 2 still needs

Pass 2 completes the storage, coordination, application, and export work. Remaining:

**Phase 2B — production speech recognition**

1. Pin faster-whisper, CTranslate2, PyAV, and the model snapshots in `artifacts/manifest.json`
   with immutable revisions, exact filenames, sizes, SHA-256 digests, and retained licences.
   Nothing may be downloaded before that entry exists.
2. Model registry, resumable download to `.partial`, digest verification, atomic activation.
3. A `faster-whisper` backend behind the existing `TranscriptionBackend` interface. The protocol,
   the supervisor, the schemas, the storage, and the UI do not change when it arrives.
4. 16 kHz mono aligned audio derivatives per track, plus the derivative-to-source time map so
   transcript timestamps seek the immutable audio.
5. Ten-minute per-epoch transcription windows with five-second overlap, overlap de-duplication,
   and per-window checkpoints.
6. Silero VAD, real word timestamps, language detection, optional glossary and initial prompt.
7. CUDA preflight, adaptive batch sizing, `int8_float16` retry on OOM, and the CPU INT8 fallback
   with an explicit, non-silent notice.
8. A model and profile selector in the UI, and a real hardware summary.

**Not started, by design:** summarization (Phase 3), results and library (Phase 4), remote
diarization (Phase 5), packaging (Phase 6), cloud processing (Phase 7), and live transcription,
which the plan excludes entirely.
