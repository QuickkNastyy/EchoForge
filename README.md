# EchoForge

A private, local-first meeting assistant for Windows:

**Record a meeting → Process it → Read what happened and what you need to do.**

Audio never leaves the machine. After first-run setup, the entire workflow runs
offline.

EchoForge is one window with three destinations — **Record**, **Recordings**, **Settings**.
Recording is just recording: no model pickers, no compute profiles, no glossary. Processing a
meeting is one action that uses the defaults chosen once in Settings. What comes out is a
**meeting brief**: a few paragraphs of what actually happened, then a numbered plan of what to do
next, in the order the meeting supports, with everybody else's work and every speculative idea kept
out of it.

## Status

Dual-track capture, crash recovery, immutable transcript/summary revisions, production local
Whisper inference, whole-meeting briefs with an ordered action plan, one-click provisioning for
every advertised model, and model comparison are implemented. Physical long-duration capture
qualification remains deliberately deferred in
[`docs/HARDENING_BACKLOG.md`](docs/HARDENING_BACKLOG.md); deferred is not passed.

[`docs/MEETING_INTELLIGENCE.md`](docs/MEETING_INTELLIGENCE.md) describes what a brief contains,
what counts as an action item and what does not, how ordering is grounded, and how long meetings
are handled without losing their last twenty minutes.

Current local ASR choices are:

- Whisper Large V3 Turbo (production, multilingual, faster-whisper/CTranslate2);
- Whisper Large V3 (production, multilingual, accuracy-oriented);
- NVIDIA Parakeet Unified EN 0.6B (experimental, English, isolated NeMo runtime);
- NVIDIA Canary-Qwen 2.5B (experimental, English, short-window isolated NeMo runtime).

**Install means ready.** For the NVIDIA models that includes building the entire isolated Linux
runtime they need — an exact CPython 3.11 installed by a pinned `uv`, the hash-locked NeMo/PyTorch
closure, a CUDA probe and a real inference on this GPU — with no shell, no `pip`, and no
environment variable for the user to set. A model is only shown as Ready once it has produced
output here; weights on disk with no runtime that can load them is reported as exactly that.

Current local summary choices are Gemma 4 12B QAT Q4_0, optional gpt-oss-20b MXFP4, and the
optional Ministral 3 14B benchmark profile. Each summary is tied to the exact transcript revision
it read. See [`docs/MODEL_PROCESSING_AND_COMPARISON.md`](docs/MODEL_PROCESSING_AND_COMPARISON.md)
for exact pins, capabilities, VAD behavior, lifecycle guarantees, and limitations.

`artifacts/manifest.json` lists every file EchoForge may download, each pinned to an
immutable revision with a verified size and SHA-256; `scripts/verify-models.ps1` is the
gate that keeps anything else out. No model is downloaded until you ask for one.

## Plan

[`docs/ARCHITECTURE_AND_IMPLEMENTATION_PLAN.md`](docs/ARCHITECTURE_AND_IMPLEMENTATION_PLAN.md)
is the authoritative document — stack decisions, data schemas, failure/recovery
strategy, the eight-phase implementation plan, acceptance criteria, and risks.

## Approach at a glance

- **C# 14 / .NET 10 / WPF**, modular monolith, one window with three pages.
- **NAudio over WASAPI shared mode** — one loopback client on the selected render
  endpoint plus one microphone capture client, running concurrently.
- Immutable **60-second PCM16 WAV chunks**, system and microphone kept as
  separate tracks, aligned on one monotonic QPC + audio-clock timeline.
- A model/backend registry over **faster-whisper/CTranslate2** and an isolated
  **NVIDIA NeMo/PyTorch** worker. Model identity, compute, VAD, and language are separate.
- Accuracy VAD is non-destructive; Balanced/Fast use explicit pinned Silero parameters; Off is
  available for diagnostic reruns.
- A short-lived **llama.cpp** child process for summarization. No permanent
  local service.
- ASR and summary comparison run models sequentially; only one GPU-heavy model is resident.
- Canonical storage is **versioned JSON plus an append-only JSONL journal**.
  SQLite is a rebuildable index, never the source of truth.
- Every decision, action item and plan step **cites transcript segment IDs**. Unsupported
  owners and dates stay `null` — the plan-step schema has nowhere to put one.
- The final brief reads the **whole meeting** when it fits, and an ordered digest covering all of
  it when it does not. It may reason about order; it may not invent a commitment.

## Repository

Source lives here. Recordings, models, transcripts, and logs live under
`%LOCALAPPDATA%\EchoForge` and are never committed.

## Building and testing

```powershell
dotnet build C:\EchoForge\EchoForge.slnx -c Debug --warnaserror
dotnet test C:\EchoForge\EchoForge.slnx -c Debug
powershell -NoProfile -ExecutionPolicy Bypass -File C:\EchoForge\scripts\run-worker-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File C:\EchoForge\scripts\verify-models.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File C:\EchoForge\scripts\lock-nemo-runtime.ps1 -Check
```

Optional, local, and needing several gigabytes of weights plus a GPU:

```powershell
python C:\EchoForge\scripts\evaluate-meeting-briefs.py --model gemma-4-12b --model gpt-oss-20b
```

The Windows worker suite needs [uv](https://docs.astral.sh/uv/) and CPython 3.12. The
.NET tests that launch the worker skip with an explanatory message when it is absent;
everything else still runs. Optional NeMo ASR runs in a WSL2/Linux runtime EchoForge provisions
itself from Settings; see [`worker-nemo/README.md`](worker-nemo/README.md). Without either
inference runtime, recording, playback, and existing revisions continue to work.

## Recording consent

Recording laws vary by jurisdiction and by participant location. EchoForge has
no automatic or hidden recording path and shows a persistent indicator while
capture is active, but obtaining the consent required for a given meeting is the
user's responsibility. Nothing here is legal advice.
