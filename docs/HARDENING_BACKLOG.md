# Hardening backlog — deferred qualification tests

These tests are **deferred, not passed, and not weakened.** They were postponed by explicit
product decision so that implementation could continue; every acceptance threshold below is the
original one from `ARCHITECTURE_AND_IMPLEMENTATION_PLAN.md`.

**Nothing in this file may be reported as a result.** A deferred test contributes no evidence.
Until every blocking item here is executed and passed, EchoForge is **not production-qualified**,
regardless of how many automated tests are green.

**Status legend:** `DEFERRED` — required, scheduled, not yet run. No other status is in use yet.

---

## H-01 · Chirp-based alignment qualification

| | |
|---|---|
| **Threshold** | Post-correction alignment error **≤ 100 ms after ten minutes**. |
| **Why it cannot be inferred** | Both tracks are padded to a shared stop instant, so equal durations prove nothing. Packet/QPC estimates cannot see analogue latency in either direction. Only a signal that travels the whole path measures the whole path. |
| **Equipment / manual action** | Real render endpoint and microphone in the same acoustic space. A chirp generator playing timed signals through the selected render endpoint, picked up acoustically by the microphone. |
| **Procedure** | Build the chirp harness. Play a known chirp at a fixed cadence for the run. Cross-correlate each chirp between the two recorded tracks after derivative correction. Emit one `{ "session_seconds": …, "offset_ms": … }` sample per chirp to `<session>/diagnostics/alignment-measurements.json`. |
| **Evidence to capture** | The measurements file, the raw session, the harness version, and the worst absolute offset. |
| **Evaluated by** | `AlignmentQualification.Evaluate` (implemented and unit-tested). The POC `validate` command reads the measurements file and prints PASS/FAIL; with no file it prints `NOT QUALIFIED`. |
| **Phase** | Phase 0 hardening, before production qualification. |
| **Status** | `DEFERRED` |

## H-02 · Continuous 60-minute drift qualification

| | |
|---|---|
| **Threshold** | Residual corrected drift **≤ 50 ms per hour**, demonstrated by **at least one continuous 60-minute run**. |
| **Why it matters** | An absolute reading at ten minutes does not predict three hours. At 50 ms/hr a three-hour session lands near 150 ms, inside the 250 ms acceptance limit with margin. |
| **Equipment / manual action** | One uninterrupted hour on the target hardware with H-01's chirp harness running. |
| **Procedure** | `dotnet run --project poc\EchoForge.AudioCapture.Poc -- record --system "<id>" --mic "<id>" --out <dir> --minutes 60`, with the chirp harness active, then `validate <session-dir>`. |
| **Evidence to capture** | Full session, measurements file, fitted ms/hour, peak queue depth, dropped-frame count, working-set trace. |
| **Phase** | Phase 0 hardening. |
| **Status** | `DEFERRED` |

## H-03 · Forced process-kill cycle on real hardware

| | |
|---|---|
| **Threshold** | **100% of finalized chunks preserved.** The active `.part.wav` is either repaired with no more than the configured flush interval (target ≤ 3 s) of lost tail, or explicitly quarantined with a recorded gap. No later-stage failure deletes audio. |
| **Equipment / manual action** | Live capture, then `Stop-Process -Force` after at least two finalized chunks per track. |
| **Procedure** | Start a recording. Wait for ≥ 2 chunks per track. Kill the process. Restart, run recovery, then `validate <session-dir>`. |
| **Evidence to capture** | Chunk inventory before and after, repair output, trimmed byte count, validator result. |
| **Note** | The recovery *logic* is implemented and covered by automated tests using synthetic abandoned files and injected failures, including the crash window between a WAV finalizing and its journal line being written. This item is the physical cycle only. |
| **Phase** | Phase 0 hardening / Phase 1 sign-off. |
| **Status** | `DEFERRED` |

## H-04 · Physical device unplug

| | |
|---|---|
| **Threshold** | The affected active chunk is finalized; the healthy track keeps recording; a persistent degraded state is visible; **no silent default-device switch**; completed chunks preserved. |
| **Equipment / manual action** | Physically disconnect the USB or wireless endpoint mid-recording, then reconnect. |
| **Already automated** | `IMMNotificationClient` registration, degraded transition, track naming, both-endpoints-lost stop, and default-device changes being reported but never followed are covered by synthetic tests driving a fake endpoint monitor. This item is the physical unplug only. |
| **Procedure** | Record both tracks. Unplug the microphone. Confirm the system track continues and the UI shows degraded. Reconnect explicitly and confirm a new epoch. Repeat for the render endpoint. |
| **Evidence to capture** | Session journal, epoch boundaries, screenshots of the degraded indicator, validator result. |
| **Phase** | Phase 1 sign-off. |
| **Status** | `DEFERRED` |

## H-05 · Sleep / resume

| | |
|---|---|
| **Threshold** | Active chunks finalized on suspend where possible; resume opens a **new epoch with an explicit gap**; audio during sleep is not fabricated. |
| **Equipment / manual action** | Suspend the machine mid-recording and wake it. |
| **Already automated** | Suspend finalizing both tracks, closing the epoch with `Suspended`, persisting the snapshot, and refusing to auto-restart on wake are covered by synthetic tests driving a fake power monitor. This item is the physical sleep only. |
| **Procedure** | Record both tracks. Sleep the machine for ≥ 60 s. Wake. Confirm the gap and new epoch, then stop and validate. |
| **Evidence to capture** | Journal entries for suspend/resume, epoch list, gap duration, validator result. |
| **Phase** | Phase 1 sign-off. |
| **Status** | `DEFERRED` |

## H-06 · Near-full-disk behaviour

| | |
|---|---|
| **Threshold** | Warn at **5 GB** free, controlled stop at **2 GB**. Preflight requires the larger of 2 GB or ten minutes of worst-case recording plus reserve. Every completed chunk and any partial bytes are preserved. The recovery-reserve file can be released to patch headers and journals. |
| **Equipment / manual action** | A quota-limited or small test volume. |
| **Procedure** | Point the session root at the constrained volume. Record until each threshold is crossed. Confirm the warning, then the controlled stop, then validate. |
| **Evidence to capture** | Threshold events in the journal, free-space trace, validator result, proof no chunk was deleted. |
| **Phase** | Phase 1 sign-off. |
| **Status** | `DEFERRED` |

## H-07 · Three-hour memory and queue soak

| | |
|---|---|
| **Threshold** | After the first ten minutes, private working-set growth attributable to capture is **≤ 200 MB**; per-track queues stay bounded to **≤ 5 s**; no unreported drops; the UI stays responsive. OS file cache reported separately. |
| **Equipment / manual action** | Three uninterrupted hours on the target hardware with generated or played-back speech. |
| **Procedure** | Record for three hours while sampling working set, queue depth, and dropped frames at a fixed interval. |
| **Evidence to capture** | Time series for working set and queue depth, peak values, dropped-frame count, validator result. |
| **Phase** | Phase 1 sign-off, repeated at Phase 6. |
| **Status** | `DEFERRED` |

## H-08 · Local model load/unload hardware matrix

| | |
|---|---|
| **Threshold** | Every installed production/experimental model selected for release loads through its declared backend and compute profile, processes a non-private safe fixture, reports requested and actual runtime/compute honestly, exits after the job, and returns dedicated VRAM to the pre-run idle range. No Windows shared-memory spill is accepted as a full-GPU fit. |
| **Models** | Whisper Large V3 Turbo FP16, Whisper Large V3 FP16, Parakeet Unified EN FP16/BF16 where supported, Canary-Qwen FP16/BF16 where supported, Gemma 4 12B, and gpt-oss-20b if offered on the target. |
| **Procedure** | Record `nvidia-smi` compute processes and dedicated memory before each run; process a repository-safe synthetic/spoken fixture; retain the content-free run telemetry; wait for the worker/llama process to exit; record the same GPU readings after exit. Run models sequentially. |
| **Evidence to capture** | Exact artifact/runtime revisions, requested/actual compute, model-load and processing time, peak VRAM, process IDs, exit outcome, before/after VRAM, and any fallback/OOM rung. No private recording or transcript content. |
| **Phase** | Local inference qualification. |
| **Status** | `DEFERRED` |

## H-09 · Held-out ASR and summary meeting bake-off

| | |
|---|---|
| **Threshold** | No universal winner is assumed. A consented, human-corrected held-out corpus produces WER/normalized WER, short-utterance and speech-region recall, proper-name/acronym/numeric accuracy, You/Remote attribution, timestamp error, structured-fact accuracy, unsupported-claim counts, evidence validity, owner/date accuracy, and human usefulness ratings. |
| **Procedure** | Run every ASR over the same source recordings, then run every compared summarizer over the same exact transcript revision. Use `AsrEvaluationCorpus`/`AsrScorer` for reference-backed ASR metrics and the existing summary corpus/scorer for facts/evidence. Keep development and held-out meetings separate. |
| **Evidence to capture** | Corpus identity/digests, model/runtime/settings provenance, content-free telemetry, aggregate metrics, and human adjudication notes kept with the private corpus rather than general diagnostics. |
| **Phase** | Model quality qualification. |
| **Status** | `DEFERRED` |

---

## What is *not* deferred

These are implemented and covered by automated tests, and are not a substitute for anything above:

- Packet timestamping, QPC-anchored timeline, silence advanced from the session clock.
- Chunk rotation, boundary splitting, hashing, atomic finalization, writer sealing after stop.
- WAV header patching, repair with partial-frame trimming, independent validation.
- Bounded-queue overflow accounting and peak depth.
- Drift-rate estimation from packet timestamps (**an estimate, never a gate**).
- Alignment gate evaluation logic, which correctly reports `NOT QUALIFIED` with no measurements.
- Recovery logic exercised with synthetic abandoned files and injected failures.

## gpt-oss-20b cannot summarise while generation is grammar-constrained

**Status:** diagnosed, not fixed. Gemma 4 12B remains the default and is unaffected.

Selecting gpt-oss-20b and reprocessing leaves the brief unchanged. The model is reached and does
run — the session journal records `summary_failed / summary_invalid_after_repair`, and the staged
telemetry names `backend: gpt-oss-20b` — but it returns nothing usable, twice, so the previous
revision stays activated.

What it actually returns, captured with `ECHOFORGE_DUMP_REPLIES` against a real 163-segment
transcript:

```
<|channel|>analysis<|message|><|end|>{"key_points": [], "decisions": [], "action_items": [], ...}
```

Sixty-six to eighty-six completion tokens, `finish_reason: stop`, every array empty. The model is
not failing to format an answer; it is declining to look for one.

The cause is a collision between Harmony and schema-constrained decoding. gpt-oss opens every
answer on its `analysis` channel, and `response_format: json_schema` constrains generation to the
schema from the first token — so the channel it wants to open is not a legal continuation. It
closes the channel immediately and emits the cheapest object the grammar accepts. Gemma has no
such channel, which is why the same pipeline, prompt and schema work for it.

Ruled out by experiment:

- reasoning off (`--reasoning off --reasoning-format none --reasoning-budget 0`) — still empty;
- context, VRAM and the fallback ladder — the 16K rung starts and generates;
- prompt length — 4188 prompt tokens against a 16384 context.

The obvious repair is to stop constraining generation for models with a reasoning channel and let
the existing lenient parser and repair pass handle shape, since the host validator — not the
grammar — is what actually decides whether a claim may be shown. That was attempted and is **not**
committed: with the grammar removed the run failed earlier still, at `backend_unavailable` during
PREPARING, and the reason was not established. It needs a worker-side stderr path before it is
worth another attempt; the supervisor currently surfaces nothing when the server refuses to start.

Reproduce with `dotnet run scripts/diagnose-summary.cs -- <session-folder> gpt-oss-20b`.
