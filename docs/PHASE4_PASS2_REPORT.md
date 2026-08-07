# Phase 4 Pass 2 — playback, date filtering, reprocessing, index maintenance, deletion

**Date:** 2026-08-07
**Scope:** the six things Pass 1 left. Phase 4 is complete. Phase 5 not started.

> **Phase 3 acceptance quality gate remains NOT RUN — pending human corpus data.** The
> human-corrected development corpus and the held-out release corpus still do not exist, so the
> gate has not been run and no summary-quality claim is made anywhere in this pass.

## What this pass added

| Pass 1 left | Now |
|---|---|
| Synchronized audio playback | Aligned two-track derivative, transport, real NAudio device |
| Evidence click seeks audio within 250 ms | Measured logically, held to a sample |
| Deletion workflow | Explicit, confirmed, Recycle Bin only, re-checked at execution |
| Date-range filtering in the UI | Two date pickers, a query rather than a rebuild |
| Reprocess from the library | Transcribe again / Generate summary again, through the coordinators |
| Index maintenance on change | Automatic, coalesced, and unable to affect a canonical operation |

## Playback derivative

`derived/playback/playback-v1/playback.wav` — **24 kHz, two channels, PCM16**, with a `TimingMap`
per track beside it and a `PlaybackDerivativeRecord` written last.

**It is not the chunks concatenated.** Concatenation answers "what was recorded" and gets *when*
wrong the moment anything interrupts a meeting: every pause closes up, everything after it is early
by the total length of the pauses, and a citation two hours in points at the wrong sentence by
minutes. So the file is laid out by **absolute session time**. Each chunk's first and last output
frame come from its own session position, silence is written wherever the timeline says time passed
with nothing captured, and the two tracks are placed independently against the same clock. Frame
*n* is session second *n / rate* by construction, so a rounding error in one chunk cannot push
anything after it. A test lays down sixty one-second 44.1 kHz chunks and finds the last one exactly
where its own second says it is.

**One channel per track, never mixed down.** Microphone is channel 0, system is channel 1. A mixed
file could not say afterwards which half was You, and the listening balance would be frozen into
bytes that cost a rebuild to change. A session missing a track still produces a two-channel file
with a silent side rather than a differently shaped one nothing else expects.

**24 kHz** is a deliberate middle: well above anything speech needs, and a three-hour meeting costs
about a gigabyte rather than two, against a raw session that already costs several.

Identity is `source_manifest_sha256` + `processing_version` + rate + channels, and reuse
additionally re-hashes the audio and both timing maps. Cancellation and failure discard only the
staging file: sources are opened read-only throughout, and a previously valid derivative is
untouched. Asserted by comparing every source chunk's digest before and after, including on the
cancellation and corrupt-source paths.

The layout arithmetic, the resampler, the timing-map shape and the WAV writing are the transcription
derivative's, reused rather than re-derived — which is what makes the file a transcript timestamp
lands on and the file a listener hears the same timeline rather than two that agree for now.

## Playback and mixing

The transport owns the position; the device owns nothing but frames. It pulls; it never decides
where it is. A seek moves one number and asks the device to drop what it has queued, so the logical
position is correct the instant the seek returns whether or not any hardware exists.

Play, pause, resume, stop, seek, scrub, current time, total duration, jump from a transcript
timestamp, jump from a citation. Stop returns to the start; playing again after the end starts the
meeting over; the reported position subtracts whatever the driver is still holding, so it is what is
audible rather than what has been handed over.

**The mix cannot clip, by arithmetic rather than by hoping.** With both tracks present each
contributes at most half of full scale, so their sum is at most full scale however loudly both
people talk at once — which is exactly the moment somebody is most likely to be replaying. A meeting
with one track is not halved: there is nothing to overlap with, so it plays at full scale. Mute You,
Mute Remote and per-track level are applied on the way to the device and never written into the
derivative, so muting is free, reversible, and cannot touch what a citation points at. A test
asserts the file's digest is unchanged after the mix is altered mid-playback.

The device is `NAudioPlaybackDevice`, at 200 ms across four buffers so a flush is cheap and a seek
is not preceded by a fifth of a second of the previous moment. A machine with no output device gets
`Failed` and a sentence, not an exception: the meeting stays readable.

## Evidence to audio

The evidence layer already produced an exact or degraded `PlaybackRequest`; this consumes it.

- **Resolved** — the transcript revision the citation *names* is opened (not the selected one), the
  segment is located and revealed, and the seek is exact.
- **Degraded** — the seek uses the time stored with the citation, the transport says the position is
  approximate, and **the selected revision is not changed**. Looking the segment ID up in whichever
  transcript happens to be selected would show a reader a sentence the summary never saw, with every
  appearance of authority. A test asserts precisely that it does not.

One rule for every jump, from the timeline, a transcript timestamp or a citation alike: **seeking
cues the moment and leaves the transport as it found it.** Clicking a citation while reading in
silence does not suddenly make noise; clicking one while listening carries straight on.

### The 250 ms criterion

Measured as a logical fact — requested session time in, playback position out — because that is the
part that can be wrong. Waiting for a speaker would test the machine the suite runs on and prove
nothing about the mapping.

| Moment | Result |
|---|---|
| Start of meeting | within one sample |
| Middle of a chunk | within one sample |
| Either side of a chunk boundary, and on it | within one sample |
| Immediately after a gap | within one sample |
| Inside a later epoch | within one sample |
| Exact evidence | within one sample |
| Approximate evidence | within one sample of its stored time |
| Manual timeline seek | within one sample |

The suite asserts both the architecture's **250 ms** criterion and a tighter internal bound of one
sample period (41.7 µs at 24 kHz); a regression that stayed inside 250 ms would still be a
regression. A second test seeks and then renders, checking the audio that comes out belongs to the
chunk that moment came from — a seek that reported the right number while playing the wrong moment
would fail.

## Date-range filtering

Two date pickers and a Clear, and **a query rather than a rebuild**: the index already knows when
every meeting was, and discarding it to ask a narrower question would turn picking a date into a
full re-read of every transcript on disk.

The whole difficulty is one sentence: meetings are stored as instants and remembered as days. A
meeting at nine in the evening happened on one date to the person who was in it and on the next date
in UTC. `LibraryFilter.ForLocalDates` does the conversion once — local midnight to the last instant
before local midnight the following day, both ends inclusive.

**A latent bug fixed on the way.** The bounds were being compared as round-trip timestamp *strings*,
which orders correctly only while every row happens to carry the same UTC offset. Index schema
version 2 adds `created_utc_ticks`, and every date comparison and ordering now uses it. The cost of
the bump is a rebuild, which is exactly what a disposable cache is for.

A reversed range matches nothing and says so, rather than being silently swapped: somebody who typed
the dates the wrong way round should be told. Search and the range apply together.

## Reprocessing from the library

`Transcribe again` and `Generate summary again` on the open meeting, through the coordinators that
already own them. **No processing lifecycle is reimplemented.** Recording still outranks both, only
one heavy job runs at a time across the whole application, revisions are still allocated before work
starts and activated only after validation, and every refusal arrives in the coordinator's own
words. The backend and profile come from the choices already made on the main window, so
reprocessing from the library means the same thing as pressing the button there.

What is added is selecting the revision that resulted. Somebody who had explicitly chosen an older
revision would otherwise get a new transcript sitting unselected, and the summary written from the
old one would never be marked stale — which is the signal that a re-transcribed meeting needs a
re-generated summary.

Source audio digests are unchanged; every earlier revision stays present and readable; a stale
summary still resolves its citations against its own revision. Tested against the real worker.

## Automatic index maintenance

Ordinary workflows no longer need Refresh or Rebuild. The index catches up after transcript
activation and selection, summary activation and selection, speaker renames, recording and recovery
finalization, reprocessing, and deletion.

**The rule that shapes it: a failed index update must never undo a canonical operation.** A
transcript that activated, activated. If re-indexing then fails — the file locked, the disk full,
the database gone — the only permitted consequence is that search is briefly out of date. So updates
are fire-and-forget, every failure is caught and recorded rather than thrown, and nothing that
changes a session awaits this or checks what it returned. A test activates a transcript against a
deliberately broken index and asserts the transcript is activated, selected, readable and unchanged.

Requests for one meeting **coalesce**: one update runs at a time per session, and anything asked for
while it runs collapses into exactly one more pass, which re-reads the final state and is therefore
correct however many requests were folded into it. Fifty notifications and sixty concurrent ones
from three threads both settle cleanly. Sessions whose update failed are listed for retry, and a
full rebuild still repairs anything missed.

## Deletion

Explicit, confirmed, reversible, and re-checked at the last moment.

**Eligibility is checked twice and the second check is the real one.** Between opening a
confirmation and answering it, a recording can start, a transcription can begin, or recovery can
claim the session — and a greyed-out button cannot express that, because it was drawn before any of
it happened. The authority is asked again immediately before the folder moves. A test opens the
confirmation while idle, starts work, confirms, and asserts the deletion is refused and the folder
is still there.

The authority is composed, not merged into a flag: session state (recording, paused, degraded,
finalizing, recovering), the session lease — the same open-handle authority recovery consults, which
cannot go stale — and what only the running application knows, being a live recorder and the two
heavy jobs. The filesystem is the last line: a folder whose files are open cannot be moved, whatever
any check concluded a moment earlier.

**Recycle Bin only.** `SHFileOperation` with `FOF_ALLOWUNDO` falls back to permanent deletion,
silently, on any volume without a Recycle Bin — a network share, some removable drives, a volume
where policy disabled it — and that is invisible from the return code. So the volume is asked first
via `SHQueryRecycleBin`, and one that cannot recycle is **refused** rather than deleted
irreversibly. The result is not taken on trust either: the operation is asked whether it was
aborted, and the folder is checked for having actually gone.

The whole session travels together — chunks, journal, projections, transcript revisions, summary
revisions, speaker aliases, derived audio — so it stays restorable as a set. The path is checked
against the store root and for being a session folder before anything happens; models and runtimes
live outside any session and are asserted untouched. **The index row goes only after the folder has
actually moved**: removing it first would leave a meeting that exists on disk and cannot be found.
A rebuild does not resurrect it, because deletion was a real move rather than an index change.

Unicode session names and names with spaces and brackets delete the same as any other.

## A Pass 1 defect this pass found

**The meeting library window could not be opened at all.** `LibraryWindow.xaml` asked for
`{StaticResource Panel}`, which is defined nowhere, and `{StaticResource BoolToVis}`, which was
defined in *MainWindow's* resources. A missing `StaticResource` is not a compile error and not a
binding warning — it throws when the window loads. Every view-model test passed and the Meetings
button was present, which is what Pass 1 verified; nothing had ever loaded the window.

`BoolToVis` moved to `Application.Resources` where both windows can see it, and the background uses
the `Surface` brush that exists.

The unit suite is the wrong place to catch this: a WPF `Application` and an audio endpoint are
process-wide, thread-affine things, and putting either in a run with 750 other tests crashed the
test host. So `scripts/smoke-library.cs` builds a synthetic meeting, loads the real window against
the real application resources, renders it, and asserts the transport, the date pickers and the
actions are there. Reverting the fix makes it fail.

## Verification

| Check | Result |
|---|---|
| `dotnet build -c Debug --warnaserror` | 0 warnings, 0 errors |
| `dotnet test` | **755 passed**, 0 failed, 0 skipped |
| `scripts/run-worker-tests.ps1 -Frozen` | **218 passed** |
| `scripts/verify-models.ps1` | PASS, 35 entries |
| `scripts/smoke-library.cs` | PASS — real device, real window, real Recycle Bin |
| Application launch | opens, title `EchoForge`, closes cleanly (exit 0) |

113 new .NET tests. All Phase 1–3 tests unchanged and passing.

The smoke script exercises what the suite deliberately cannot: an audio output device really opened
and played, the library window really loaded, and a synthetic meeting really recycled. It touches
nothing but a temporary directory it creates.

## Phase 4 completion criteria

| Criterion | Met |
|---|---|
| Any valid session can be located and opened from the library | yes |
| Search finds expected transcript and summary text | yes |
| Evidence opens the exact transcript revision it cites | yes |
| Evidence playback reaches the correct audio within 250 ms | yes — within one sample |
| Transcript and summary exports work | yes |
| Speaker alias affects presentation only | yes |
| Deletion is explicit and safe | yes |
| Transcription can be rerun without rerecording | yes |
| Summarization can be rerun without rerecording | yes |
| Previous revisions remain available | yes |
| Source hashes remain unchanged | yes |
| SQLite stays rebuildable | yes |
| JSON and session folders remain canonical | yes |

**Phase 4 is complete. No blocker remains.**

## Phase 3 corpus gate

Unchanged and still outstanding, stated again because it is a release blocker and nothing in this
pass touched it:

- Human-corrected **development** corpus: **NOT supplied**
- Held-out **release** corpus: **NOT supplied**
- Phase 3 summary-quality acceptance gate: **NOT RUN**

Phase 3's implementation and evaluation infrastructure are complete; the gate is blocked on data
that has to come from outside the repository. It does not block Phase 4, and Phase 4 makes no
summary-quality claim.
