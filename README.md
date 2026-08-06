# EchoForge

A private, local-first Windows meeting utility:

**Record → Transcribe locally → Summarize locally → Extract actions.**

Audio never leaves the machine. After first-run setup, the entire workflow runs
offline.

## Status

**Phase 0** (dual-track capture and recovery) is implementation-complete and
automated-test green. Its long-duration and physical-hardware acceptance runs are
deliberately deferred and tracked in
[`docs/HARDENING_BACKLOG.md`](docs/HARDENING_BACKLOG.md); until those happen Phase 0
is **not production-qualified**. Deferring a test is not passing it.

**Phase 1** (the recording application, session storage, crash recovery) is accepted.

**Phase 2A, Pass 1** is complete: the NDJSON worker protocol, the transcript schema
and contracts, the Windows Job Object worker supervisor, and a deterministic Python
worker that performs **no speech recognition** and says so in everything it writes.
See [`docs/PHASE2A_PASS1_REPORT.md`](docs/PHASE2A_PASS1_REPORT.md) for what exists and
what Phase 2A still needs.

No production model, CUDA component, or inference runtime has been added.
`artifacts/manifest.json` is intentionally empty, and `scripts/verify-models.ps1`
is the gate that keeps it that way.

## Plan

[`docs/ARCHITECTURE_AND_IMPLEMENTATION_PLAN.md`](docs/ARCHITECTURE_AND_IMPLEMENTATION_PLAN.md)
is the authoritative document — stack decisions, data schemas, failure/recovery
strategy, the eight-phase implementation plan, acceptance criteria, and risks.

## Approach at a glance

- **C# 14 / .NET 10 / WPF**, modular monolith.
- **NAudio over WASAPI shared mode** — one loopback client on the selected render
  endpoint plus one microphone capture client, running concurrently.
- Immutable **60-second PCM16 WAV chunks**, system and microphone kept as
  separate tracks, aligned on one monotonic QPC + audio-clock timeline.
- **faster-whisper / CTranslate2** for speech-to-text, with a CPU fallback path.
- A short-lived **llama.cpp** child process for summarization. No permanent
  local service.
- Canonical storage is **versioned JSON plus an append-only JSONL journal**.
  SQLite is a rebuildable index, never the source of truth.
- Every decision and action item **cites transcript segment IDs**. Unsupported
  owners and dates stay `null`.

## Repository

Source lives here. Recordings, models, transcripts, and logs live under
`%LOCALAPPDATA%\EchoForge` and are never committed.

## Building and testing

```powershell
dotnet build C:\EchoForge\EchoForge.slnx -c Debug --warnaserror
dotnet test C:\EchoForge\EchoForge.slnx -c Debug
powershell -NoProfile -ExecutionPolicy Bypass -File C:\EchoForge\scripts\run-worker-tests.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File C:\EchoForge\scripts\verify-models.ps1
```

The Python worker suite needs [uv](https://docs.astral.sh/uv/) and CPython 3.12. The
.NET tests that launch the worker skip with an explanatory message when it is absent;
everything else still runs.

## Recording consent

Recording laws vary by jurisdiction and by participant location. EchoForge has
no automatic or hidden recording path and shows a persistent indicator while
capture is active, but obtaining the consent required for a given meeting is the
user's responsibility. Nothing here is legal advice.
