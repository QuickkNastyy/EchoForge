# Phase 0 report — dual-track capture proof

**Machine:** Windows 11 Pro 26200, .NET SDK 10.0.302, NAudio 2.3.0
**Endpoints used:** Headphones (Astro A50 Game) render · Headset Microphone (Astro A50 Voice) capture

## Status

| | |
|---|---|
| Implementation | **Complete enough to continue.** The capture and timeline design is settled and built. |
| Automated tests | **Passing.** 42 tests green; build clean with warnings as errors. |
| Real-hardware qualification | **Deferred** by explicit product decision. See `HARDENING_BACKLOG.md`. |
| Production qualification | **Not qualified.** No timing or durability gate has been measured. |

Phase 1 proceeds on the implemented capture system. It does **not** proceed on a claim that the
timing gates passed — they have not been run. Deferring a test is not passing it, and none of the
original thresholds have been weakened.

This records what Phase 0 has actually measured. Estimates are not promoted to results.

## Settled

### The lower-level NAudio timestamp API is reachable — no COM shim needed

The plan allowed for a native Core Audio shim if NAudio could not expose per-packet positions.
It can. Both of these compile and run against NAudio 2.3.0:

```csharp
capture.GetBuffer(out int framesRead, out AudioClientBufferFlags flags,
                  out long devicePosition, out long qpcPosition);
audioClient.SetEventHandle(handle);
```

`WasapiCapture`/`WasapiLoopbackCapture` and `DataAvailable` are not used anywhere in the
capture path. EchoForge owns the loop.

### Device position is unusable as a frame counter when the engine resamples

The single most important measurement of Phase 0. Raw packet headers from the headset
microphone:

```
dev 403154720 (+   0)  qpc 252650278821 (+  0.0 ms)  frames 480  bytes 1920
dev 403154880 (+ 160)  qpc 252650378734 (+ 10.0 ms)  frames 480  bytes 1920
dev 403155040 (+ 320)  qpc 252650478737 (+ 20.0 ms)  frames 480  bytes 1920
```

Each packet carries **480 frames** but the device position advances by **160**. The endpoint
captures natively at 16 kHz; the audio engine resamples to the 48 kHz shared mix format.
`devicePosition` counts frames at the *device's* rate, not the mix format's.

Building the timeline on device position produced a 3× error — tracks that recorded 10 seconds
of audio during a 30-second run, and a reported drift of 2,399,940 ms/hour.

**Consequence:** the session timeline is anchored on **`qpcPosition`**, which was clean and
evenly spaced at exactly 10.0 ms per packet. Device position is retained as a diagnostic and a
corroborating discontinuity signal only. `DriftEstimator` compares delivered frames against
elapsed QPC rather than device position against QPC.

### A stalled endpoint must be filled from the session clock, not from the next packet

The headset microphone stopped delivering packets partway through a 30-second run — headset
power management, not a fault. Because no packet ever arrives during a stall, a writer driven
only by packet arrival never learns that time passed, and the track simply ends early. Observed:
a 30-second run producing a 10-second microphone track.

**Consequence:** `PcmChunkWriter.AdvanceTo(qpc)` fills the timeline from the shared clock. The
writer thread calls it whenever it is idle, and the recorder calls it once at stop with a single
shared stop timestamp so both tracks end at the same instant.

### Both tracks must share one epoch

Each track originally used its own first packet as t=0, which makes alignment impossible by
construction. The epoch is now fixed once, before either endpoint opens, and passed to both
chunk writers.

### Verified behaviour

- Two endpoints captured simultaneously; separate immutable chunk series; no mixing.
- 60-second PCM16 chunks, finalized with SHA-256 and an atomic rename.
- Silence inserted from the clock; gaps recorded as discontinuities, never hidden.
- Every finalized chunk decodes with an independent reader (`validate` re-reads from disk and
  trusts nothing the recorder wrote).
- 42 unit tests green, covering chunk rotation, boundary splitting, silence advanced from the
  session clock, jitter rejection, overlap dropping, writer sealing after stop, WAV repair with
  partial-frame trimming, bounded-queue overflow accounting, drift-rate recovery, and
  alignment-gate evaluation.
- Capture threads perform no disk I/O, hashing, or UI work.

Latest one-minute run: both tracks 00:01:00, two chunks each, all valid, zero dropped frames.

### Stop and Dispose are idempotent

A reviewed defect: `TrackPipeline.Dispose()` called `Stop()` unconditionally, so disposing after
an explicit stop padded the timeline to "now", manufactured silence, and finalized a fresh chunk —
mutating a session that had already been validated and written to `session.json`.

`PcmChunkWriter.Complete()` now **seals** the writer. A sealed writer ignores further audio,
silence, and overflow reports. `TrackPipeline` tracks started/stopped/disposed state separately,
so a second stop or a later dispose does nothing, while a pipeline whose `Start` failed part way
through can still be cleaned up. Regression tests capture a SHA-256 fingerprint of the track
directory and assert it is byte-for-byte identical after a post-stop advance, write, overflow
report, second complete, and dispose.

## Deferred — not measured, not passed

Every item below is tracked in `HARDENING_BACKLOG.md` with its original threshold, required
equipment, procedure, and the evidence that must be captured.

| Gate | Backlog | Status |
|---|---|---|
| ≤100 ms post-correction alignment at ten minutes | H-01 | `DEFERRED` — needs the chirp harness |
| ≤50 ms/hour residual drift over a continuous 60-minute run | H-02 | `DEFERRED` |
| Forced process kill, restart, recover active part | H-03 | `DEFERRED` — recovery logic is automated-tested; the physical cycle is not |
| Physical device unplug | H-04 | `DEFERRED` |
| Sleep / resume | H-05 | `DEFERRED` |
| Near-full disk | H-06 | `DEFERRED` |
| Three-hour memory and queue soak | H-07 | `DEFERRED` |
| 250 ms over three hours (final acceptance) | — | `DEFERRED` |

### Why track duration cannot stand in for any of these

The validator previously derived the alignment and drift gates from the difference in final WAV
lengths. That was wrong and has been removed. Both tracks are padded to a shared stop instant, so
**equal durations are guaranteed by construction and say nothing about whether the audio lines
up**. Packet/QPC figures are also insufficient: they cannot see analogue latency in either
direction. Only a signal that travels the whole path measures the whole path.

`validate` now prints duration difference as a labelled diagnostic and reports both timing gates
as `NOT QUALIFIED` unless a session supplies signal-based measurements at
`<session>/diagnostics/alignment-measurements.json`. When that file exists the gates are evaluated
by `AlignmentQualification`, which is implemented and unit-tested now, so the chirp harness can be
added later without touching the capture system.

### The open measurement

Track lengths differ by a stable **~32 ms** — 46.6 ms on a 30-second run, 31.8 and 31.9 ms on
two 60-second runs. It does **not** grow with duration, which is the signature of a fixed
start offset rather than clock drift. Most likely the initial buffer one endpoint hands over
when the client starts. It has to be characterised before the alignment gate can be read,
because a constant offset and a drift rate need opposite fixes: an offset is corrected once at
alignment, a drift rate is corrected continuously.

The chirp test is the instrument for this — a known signal played through the render endpoint
and picked up acoustically gives a true end-to-end offset, which the current length comparison
cannot.

## How to run it

```bash
dotnet run --project poc\EchoForge.AudioCapture.Poc -- devices
```

```bash
dotnet run --project poc\EchoForge.AudioCapture.Poc -- diagnose --endpoint "<id>"
```

```bash
dotnet run --project poc\EchoForge.AudioCapture.Poc -- record --system "<id>" --mic "<id>" --out <dir> --minutes 60
```

```bash
dotnet run --project poc\EchoForge.AudioCapture.Poc -- validate <session-dir>
```

`validate` prints the gate results and exits non-zero on failure. The 60-minute qualification
run is the next thing that has to happen, and it has to happen on this hardware.

## Next

Phase 1 is proceeding now on the implemented capture system, by product decision.

The hardening work below must be completed before EchoForge can be called production-qualified.
It is tracked in `HARDENING_BACKLOG.md` and must not be dropped:

1. Build the chirp offset harness and separate the stable ~32 ms fixed offset from any drift rate (H-01).
2. Run the 60-minute qualification capture and read the residual drift gate (H-02).
3. Exercise kill/restart, device unplug, sleep/resume, near-full disk, and the three-hour soak (H-03 – H-07).
