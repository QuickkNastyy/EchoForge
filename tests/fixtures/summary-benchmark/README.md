# Summary benchmark corpora

Three directories, and the difference between them is the whole point.

```text
summary-benchmark/
  synthetic/      written by hand, to test the scorer. Never evidence about a model.
  development/    human-annotated meetings, for iteration. Never an acceptance result.
  release/        human-annotated meetings, held out. The acceptance gate, and nothing else.
```

## What is here today

**Only `synthetic/`.** It contains one written meeting whose transcript nobody recorded and whose
gold facts nobody annotated — it exists so the scoring code has something to be tested against,
and every meeting in it is marked `"synthetic": true`.

`development/` and `release/` are **empty**. The human-corrected corpora are supplied separately.
Until they exist:

- the Phase 3 acceptance quality gate has **not run**, and cannot;
- no statement about Gemma's or Ministral's summary quality on real meetings is available;
- the bake-off can compare machinery, latency and memory, but not quality.

Nothing in this repository fabricates that data. A written meeting scored well is a statement
about the scorer's arithmetic, and quoting it as a quality result would be the most convenient
possible lie for whoever is trying to ship.

## The rules the validator enforces

- A meeting marked `synthetic` **cannot** sit in a development or release corpus, and a meeting in
  a synthetic corpus must be marked. Neither is a warning; both are rejections.
- Development and release **cannot overlap**, checked by meeting ID *and* by transcript digest —
  because the way this usually goes wrong is the same meeting arriving twice under two names after
  a re-export, not somebody copying a file on purpose.
- Every meeting outside the synthetic set must be scored against a **human-corrected** transcript
  (`"transcript_fidelity": "human_corrected"`). Summary quality is never measured against raw
  recogniser output: a summariser must not be marked down for a word the recogniser got wrong, and
  that is what the separate STT evaluation is for.
- Every gold decision and action must cite evidence. Without it a fact can never be matched, and
  it would score as a miss no model could have avoided.
- Gold owners and dates obey the same invariants a summary does. An unknown owner has no name.

## Adding an annotated meeting

1. Put the human-corrected canonical transcript under `<corpus>/transcripts/<meeting-id>.json`.
2. Add a meeting entry with a **stable** `meeting_id`, the transcript's SHA-256, the meeting date
   if it is known, `"transcript_fidelity": "human_corrected"`, and the gold facts.
3. Give every gold fact a stable ID within the meeting. IDs appear in the match log, so renaming
   one makes two evaluations incomparable.
4. Where a real summary would reasonably word a fact differently, list the accepted wordings in
   `aliases`. That is the intended way to keep matching strict — the alternative is loosening the
   similarity floor for every fact at once.
5. Record deliberate unknowns explicitly. "Someone will write it up" is a meeting declining to
   assign work, and `owner: null` with `owner_status: "unknown"` is what says so.
6. Validate before running anything:

```bash
dotnet run scripts/evaluate-summaries.cs -- validate
```

## Release discipline

The release corpus is read only when a candidate is believed ready. The evaluation CLI refuses it
unless `--acceptance-run` is passed, and a report generated from it is the only thing that may be
described as an acceptance result.

Prompts are never tuned against release contents. If a release meeting has to be moved into
development to debug something, it leaves the release set permanently and is replaced.
