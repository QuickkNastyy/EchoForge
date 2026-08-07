# Phase 3A — local summarization foundation

**Date:** 2026-08-06
**Scope:** everything summarisation needs that is not a language model. Stops before llama.cpp and
Gemma.

The ordering is the point. When a model arrives it replaces one class behind one interface, and
everything that decides whether a claim may be shown to a user is already written, already tested,
and already refusing things. Building the guardrails after the generator is how a summary feature
ends up shipping claims nobody checked.

## Schema and evidence guarantees

`schemas/summary.schema.json`. Every factual item carries `certainty` of `explicit`, `inferred`, or
`unknown`. **These are not a confidence scale**: explicit means the transcript says so, inferred is
a reading of it shown separately, unknown is the honest answer. Nothing may raise a value later — a
synthesis pass promoting an inference to explicit would invent support that never existed.

Owner and due date carry **their own** status, because a task can be perfectly explicit while
nobody said who would do it. Coupling them to the item's certainty would force a choice between
dropping a real action and inventing an owner for it.

Enforced by `SummaryValidator`, which is the final authority — not the model:

- Every decision and action cites at least one segment, whatever certainty it claims.
- Every explicit item cites at least one segment.
- Every citation resolves **in the exact transcript revision it names**. The durable identity is
  the pair `transcript_revision` + `segment_id`; a bare segment ID is stable only inside one
  revision.
- Citation times must be the segment's own, and the display timestamp must be derived from it. A
  model that mis-states a timestamp would send the reader to the wrong audio and look
  authoritative doing it.
- `owner_status: unknown` implies `owner: null` — there is no such thing as an unknown owner with a
  name. `explicit` requires a name actually given.
- Same for due dates, plus: an ISO date may only be emitted when the meeting date is known.
- The validator **refuses rather than repairs**. Dropping the unsupported half of a claim and
  keeping the rest produces a summary nobody wrote and nobody reviewed.

Owner and date inference default to **off**.

## Chunking and checkpoints

`TranscriptChunker` cuts on segment boundaries, never inside one: a segment is the unit evidence is
cited by, so splitting one would make every citation out of that chunk subtly wrong. Adjacent
chunks share segments so a decision stated across a boundary is seen whole by at least one
extraction.

Chunks name their first and last segment rather than carrying text, which keeps the protocol's rule
that messages carry no meeting content — the worker reads the transcript file it was pointed at.

An oversized segment becomes its own chunk rather than being dropped: refusing to emit it would
silently remove it from the summary, which is worse than handing the model something larger than it
asked for. A test proves every segment of a 500-segment transcript appears in at least one chunk.

Each chunk's `input_fingerprint` covers the session, transcript revision, source manifest,
boundaries, prompt version, chunk size, overlap, inference settings, and meeting date — so a
checkpoint cannot survive a change to any of them. Tests prove a changed prompt version and a
changed transcript revision both invalidate every fingerprint.

The plan asks for 8K–12K *tokens*; this measures characters. The placeholder has no tokenizer, and
a character budget is stable and wrong in a predictable direction. A real tokenizer arrives with
the model that needs one.

## Deterministic backend

`mock-summary` reads the real transcript, classifies segments by marker phrases, quotes what was
said, and cites the segment it quoted. It **composes nothing**. `produces_summaries` is false on
the `started` message, in the summary document, in the revision record, and in the app; the
overview says so in the file itself.

Test modes added: `malformed_summary` (schema-shaped, cites a segment that does not exist) and
`truncated_summary` (stops after three fields). The existing transcription protocol is unchanged —
`summarize` is a second job kind sharing the handshake and nothing else, and a `start_job` carrying
neither request or both is refused by the codec.

Writing the tests found a real defect: the owner extractor matched *"Someone will write it up"* and
recorded **Someone** as an explicit owner — exactly the unsupported-owner failure the certainty
model exists to prevent, and more dangerous for looking like a real extraction. Indefinite pronouns
in a name position are now refused.

Relative dates resolve only against a known meeting date and only when unambiguous. "End of the
month" stays unknown with the wording kept, because which day it means is genuinely open.

## Revision storage

```text
summary/
  summary.v1.json
  summary.v2.json
  summary.v3.json.staging
```

Same authority model as transcripts: the journal says which revisions were activated, the files say
which still exist, neither half alone is sufficient. Staging → fsync → atomic rename → **then**
journal, so a crash between the rename and the append leaves a file nothing vouches for (startup
discards it) rather than a journal claiming a summary that does not exist.

Recorded per revision: revision, job ID, timestamps, relative path, output digest, **source
transcript revision and digest**, prompt version, backend, model ID, worker version,
`produces_summaries`, decision and action counts, and evidence-validation status.

Failure and cancellation never retract a good revision. Reprocessing creates a new immutable one; a
test asserts revision 1's bytes are unchanged afterwards.

**No summary prose enters the journal** — identities, digests and counts only, asserted by a test
that walks every field of every event.

A summary whose transcript revision is no longer the selected one is **stale**: visible, never
silent, and still fully readable, because its evidence still points at its own revision.

## Coordinator

`SummaryCoordinator` requires a selected transcript revision that is present, readable, and passes
`TranscriptValidator`. Summarising a session with no transcript is not a degraded case; there is
nothing to summarise.

It shares the transcription coordinator's gate rather than keeping its own — two coordinators each
politely checking their own state would still start two jobs. Recording outranks both. Every result
is validated against the transcript before activation, which is the last point a refusal costs
nothing.

## UI

Generate summary / Generate again / Cancel, current stage, progress with a per-chunk description,
selected revision with a version list, a stale notice, an actionable error, and a placeholder
warning shown **before anything has run** — because the placeholder is what would run. Nothing slow
touches the UI thread; a test asserts the command returns in under 250 ms.

## Verification

| Check | Result |
|---|---|
| `dotnet build -c Debug --warnaserror` | 0 warnings, 0 errors |
| `dotnet test` | **506 passed**, 0 failed, 0 skipped |
| `scripts/run-worker-tests.ps1 -Frozen` | **144 passed** |
| `scripts/verify-models.ps1` | PASS, 30 entries |
| Application launch | window opens with the summary panel, Generate summary, and the placeholder warning |

The flaky test reported at the start of this pass is fixed: it rewrote an equal-length file and
assumed `LastWriteTimeUtc` would move, which depends on filesystem timestamp resolution. The
timestamp is now set explicitly, so what is under test is the cheap marker check rather than the
clock underneath it. Its deep-verify sibling still deliberately restores the timestamp, so the two
together document both halves.

## Remaining Phase 3 work

**Phase 3B — the model.**

1. Pin a llama.cpp Windows release and the official `google/gemma-4-12B-it-qat-q4_0-gguf` file
   (commit, filename, size, SHA-256, licence) into `artifacts/manifest.json` through the existing
   gate.
2. Launch `llama-server.exe` on loopback with a random port, one slot, 32K context, Q8 KV cache,
   thinking off, offline mode, and tear it down at job end. Unload STT before loading the LLM.
3. Prompt files (`extract-v1`, `synthesize-v1`, `repair-v1`) and schema-constrained generation.
4. A `gemma` backend behind the existing `SummaryBackend` interface. The schema, validator,
   chunker, storage, coordinator and UI do not change when it arrives.
5. **Recursive synthesis** for meetings whose validated digests still exceed 32K. The current
   pipeline extracts and merges per chunk; the hierarchical fold is not implemented.
6. **The one bounded malformed-JSON repair attempt.** The worker can produce malformed output and
   the host refuses it correctly, but the single re-ask has not been written — a malformed summary
   currently fails rather than being retried once.
7. GPU OOM handling: reduced operational context and re-chunking, then partial offload.
8. The two-corpus quality gate — a 3–5 meeting development set and a held-out 10–20 meeting release
   set, with the Gemma-versus-Ministral bake-off. That needs annotated real meetings and is the
   Phase 3 acceptance gate; nothing synthetic substitutes for it.

Items 5 and 6 are the two places this pass stopped short of the full pipeline description, and both
are named as unimplemented rather than approximated.
