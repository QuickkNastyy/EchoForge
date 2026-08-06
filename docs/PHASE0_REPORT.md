# Phase 0 report — dual-track capture proof

**Machine:** Windows 11 Pro 26200, .NET SDK 10.0.302, NAudio 2.3.0
**Endpoints used:** Headphones (Astro A50 Game) render · Headset Microphone (Astro A50 Voice) capture
**Status:** capture path working; **timing gates not yet qualified**

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
- 29 unit tests green, covering chunk rotation, boundary splitting, silence insertion, jitter
  rejection, overlap dropping, WAV repair with partial-frame trimming, bounded-queue overflow
  accounting, and drift-rate recovery.
- Capture threads perform no disk I/O, hashing, or UI work.

Latest one-minute run: both tracks 00:01:00, two chunks each, all valid, zero dropped frames.

## Not yet qualified

| Gate | Status |
|---|---|
| ≤100 ms post-correction alignment at ten minutes | **Not measured.** Needs the chirp test. |
| ≤50 ms/hour residual drift over a continuous 60-minute run | **Not run.** |
| 250 ms over three hours | Not run. |
| Process kill after ≥2 chunks, restart, recover active part | Repair is unit-tested; the real kill/restart cycle has not been exercised. |
| Device unplug, sleep/resume, near-full disk | Not exercised. |
| Bounded memory over a three-hour soak | Not measured. |

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

1. Run the 60-minute qualification capture and read the residual drift gate.
2. Build the chirp offset harness and separate the fixed offset from the drift rate.
3. Exercise kill/restart, device unplug, and sleep/resume against real devices.
4. Only then move to Phase 1.
