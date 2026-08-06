# EchoForge

A private, local-first Windows meeting utility:

**Record → Transcribe locally → Summarize locally → Extract actions.**

Audio never leaves the machine. After first-run setup, the entire workflow runs
offline.

## Status

Pre-implementation. The architecture and phased plan are complete; no code has
been written yet.

Next step is **Phase 0** — proving that the selected playback endpoint and the
microphone can be captured simultaneously into separate, valid, timeline-aligned
files, and that an interrupted recording recovers cleanly. Phase 0 is a blocking
gate: no GUI work begins until it passes.

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

## Recording consent

Recording laws vary by jurisdiction and by participant location. EchoForge has
no automatic or hidden recording path and shows a persistent indicator while
capture is active, but obtaining the consent required for a given meeting is the
user's responsibility. Nothing here is legal advice.
