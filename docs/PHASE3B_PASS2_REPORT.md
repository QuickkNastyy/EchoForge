# Phase 3B Pass 2 — evaluation, bake-off machinery, and runtime measurements

**Date:** 2026-08-07
**Scope:** everything needed to evaluate local summarization rigorously. Stops at the point where
human-annotated meetings would be required, because there are none.

> ## Phase 3 acceptance quality gate **NOT RUN** — human-annotated corpus not supplied
>
> `tests/fixtures/summary-benchmark/development/` and `release/` are empty. No statement about
> Gemma's or Ministral's summary quality on real meetings exists in this repository, and none is
> made below. What was built is the machinery that will produce one.

## What is pinned

| | |
|---|---|
| **Comparison model** | `mistralai/Ministral-3-14B-Instruct-2512-GGUF` |
| Commit | `74fac473c43357d7fb2671713608183cc72496d0` |
| File | `Ministral-3-14B-Instruct-2512-Q4_K_M.gguf`, 8,239,593,024 bytes |
| SHA-256 | `824e0f3373e69b84f2cae46fdcb9bd1ebc6ab3bfc7acc125d818b7b8178cc613` |
| Licence | **Apache-2.0**, not gated |
| Verified | downloaded and re-hashed locally; the pinned digest matched |

The plan named this file and it exists exactly as described, 8.24 GB to the byte. Verified against
Hugging Face's own model index before the manifest was touched, for the same reason Gemma 4 was:
the previous pass found the plan right about Gemma and wrong about its licence, and neither could
have been known without checking.

The BF16 multimodal projector in the same repository is **deliberately not pinned** — EchoForge
summarises text, and it is a BF16 file at 879 MB.

Ministral belongs to the `summary-bakeoff` profile, which is deliberately **not** one of
`ProcessingProfile.SummaryProfiles`. Having a comparison candidate installed can never make it the
summariser; something has to ask for a bake-off explicitly.

**Artifact gate: PASS, 35 entries.**

## One pipeline, two models

`LocalSummaryBackend` now serves both. What differs lives in `worker/echoforge_worker/model_profiles.py`
and is only: the chat template (inside each GGUF), and the llama-server flags each model needs.

Shared and unchanged: the canonical transcript, the summary schema, the evidence allow-list, the
owner and date invariants, the prompt intent, the token budget policy, the recursive fold, the one
bounded repair, the revision storage, and the validator. If the two candidates were scored through
two pipelines, the pipelines would be part of the result.

The profile records why each model's reasoning settings are what they are — Gemma pins reasoning
off because its template enables thinking despite its card describing it as opt-in; Ministral
passes nothing because it has no thinking mode. "Not required" is written down so it stays
distinguishable from "forgotten". A test asserts no profile field could ever relax validation.

## The corpus format

`schemas`-shaped records in `EchoForge.Contracts/Evaluation`, three directories under
`tests/fixtures/summary-benchmark/`, and a validator that makes the dangerous mistakes impossible
rather than discouraged:

- **Synthetic data cannot masquerade as real data**, in either direction. A meeting marked
  synthetic in a development or release corpus is a rejection, not a warning.
- **Development and release cannot overlap**, checked by meeting ID *and* by transcript digest.
  The way this actually goes wrong is a re-exported meeting arriving twice under two names, which
  an ID check alone misses entirely.
- **Summary quality is only ever scored against a human-corrected transcript.** A summariser must
  not be marked down for a word the recogniser got wrong; that is the separate STT evaluation's
  job.
- Every gold decision and action must cite evidence, or it could never be matched and would score
  as a miss no model could avoid.
- Gold owners and dates obey the same invariants a summary does — an unknown owner has no name.
- The release corpus is refused by the CLI without `--acceptance-run`. Reading the held-out set by
  accident is not recoverable.

The corpus fingerprint covers the gold facts and deliberately not the notes: an annotator
clarifying their reasoning must not throw away completed runs, and a changed fact must.

## Scoring

**No model judges the model.** Set arithmetic and string normalisation only. A language model
deciding whether a language model was right would make an improvement in the judge
indistinguishable from an improvement in the summariser.

**Evidence anchors every match.** A prediction can only be tied to a gold fact when the two cite at
least one segment in common. That single rule is what keeps matching conservative: two
similarly-worded statements about different moments are two facts, and no amount of textual
resemblance may merge them. Wording is compared on top of that — exact normalised match, or any
annotator-listed alias, scores 1; otherwise Jaccard over content words against a threshold the
corpus declares (default 0.5). Assignment is one-to-one and greedy over a deterministically sorted
candidate list, so two runs produce the same match log.

Reported per meeting and aggregated: action and decision precision and recall (separately and
combined), owner and date exact precision, evidence validity, evidence coverage, key-point
coverage, contradiction handling, unsupported explicit owner and date counts, unknown owner/date
preservation, failure rate, latency, peak VRAM.

**Zero denominators are never hidden.** A meeting with no gold decisions has no decision recall:
`Ratio.Value` is null, and aggregation adds counts rather than averaging percentages. Reporting 0%
would blame a model for missing what was never there; reporting 100% would let a corpus of empty
meetings clear the gate.

**Readability is absent on purpose.** It is a human judgement, and an automatic stand-in would be a
number pretending to be an opinion. The report says so where the metric would have been.

## Acceptance thresholds and the bake-off rule

The plan's targets are executable, not prose: ≥95% combined precision, ≥85% combined recall, 100%
evidence validity, zero unsupported explicit owners or dates. `AcceptanceVerdict.IsAcceptanceRun`
is false for anything that is not the release corpus, and the statement string says which kind of
data produced it — a development number and an acceptance number are the same shape on a page.

The bake-off composite is **preregistered in code before any held-out run**:

| Component | Weight |
|---|---|
| Combined precision | 0.35 |
| Combined recall | 0.25 |
| Evidence validity | 0.20 |
| Owner precision | 0.10 |
| Date precision | 0.10 |

Precision outweighs recall because a confident claim nobody made is worse than a missed one. The
challenger takes the default only by clearing **+5.0 composite points** with no material
regression: failure rate up more than 5 points, peak VRAM up more than 10%, or any increase in
unsupported explicit owners or dates all block the switch regardless of the margin. The incumbent
wins ties, near-ties, and anything the rule cannot decide.

## Runtime instrumentation

`worker/echoforge_worker/measurements.py`, written to a sidecar **beside** the summary
(`<summary>.telemetry.json`) rather than inside it. Durations for total, model load, extraction,
synthesis and repair; prompt and completion tokens; prompt-processing and generation tokens/second
taken from llama.cpp's own `timings` rather than from wall-clock arithmetic, because one elapsed
figure hides which stage was slow; requested against actual context; requested GPU layers; KV cache
type; the runtime tier that actually ran; every fallback step; OOM retries; peak VRAM with its
source.

Peak VRAM falls back to the whole-device figure and **says so in the same record**: Windows in
WDDM mode does not report per-process compute memory through `nvidia-smi`, and a number that
overstates by an unknown amount is only useful if nobody can mistake it for a measurement of the
model.

A test walks every field of the telemetry record and fails if any string could hold text that came
out of a meeting or a model.

## Fallback ladder — actually exercised on this machine

All five tiers started, answered a real schema-constrained question, and left no process behind.

| Tier | Context | Load | Generation | Survivors |
|---|---|---|---|---|
| `cuda-32k` | 32768 | 3.97 s | 70.1 tok/s | 0 |
| `cuda-16k` | 16384 | 3.92 s | 69.6 tok/s | 0 |
| `cuda-8k` | 8192 | 4.46 s | 70.0 tok/s | 0 |
| `cuda-8k-partial` (24 layers) | 8192 | 2.90 s | **9.9 tok/s** | 0 |
| `cpu-8k` | 8192 | 2.33 s | **5.2 tok/s** | 0 |

**This confirms the ladder's ordering was right.** Reducing context costs essentially nothing in
throughput (70 → 70 → 70); moving layers off the GPU costs 7×. Giving up context before giving up
the GPU was the correct call, and it is now measured rather than asserted.

The tiers were started **directly** rather than by exhausting the GPU. Deliberately provoking an
out-of-memory condition on a machine somebody is using is a bad way to learn something a direct
start demonstrates equally well. What this does **not** prove is the transition between tiers under
real memory pressure; that path is unit-tested with an injected failure, and is named here rather
than implied.

## Q8 versus Q4 KV cache — measured, default unchanged

Identical model, context, offload, prompt and seed:

| KV cache | Peak VRAM (device total) | Generation | Valid items |
|---|---|---|---|
| `q8_0` | 10.90 GB | 66.8 tok/s | 3 |
| `q4_0` | 10.53 GB | 70.4 tok/s | 3 |

Q4 is marginally cheaper and marginally faster — roughly 0.37 GB and 5%. **The default stays at
Q8.** The plan says not to change a production default from synthetic tests, and the thing Q4 would
plausibly cost is summary quality, which is exactly what cannot be measured without the corpus.
The measurement is recorded so the decision can be made when it can be made properly.

## Gemma versus Ministral — machinery only

Both models were run end to end through the real coordinator on the synthetic fixture. **This is a
machinery comparison. It is one written meeting, and it is not evidence about summary quality.**

| | Gemma 4 12B | Ministral 3 14B |
|---|---|---|
| Ran end to end | yes | yes |
| Context achieved | 32768 | 32768 |
| Median wall clock | 17.4 s | 18.5 s |
| Peak VRAM (device total) | 10.68 GB | 13.91 GB |
| Failure rate | 0 | 0 |
| Unsupported explicit owners | 0 | 0 |

The bake-off rule was applied to these numbers and returned **do not switch** — the composite
margin was negative, and Ministral's peak VRAM was 30% higher, which the rule treats as material on
a 16 GB card. That outcome is a demonstration that the rule executes, not a finding about either
model.

### What the comparison actually found: a defect in EchoForge

Ministral's first run produced a **completely empty summary** — not one false positive. The cause
was not the model. The prompt rendered transcript lines as `[segment-000123] Speaker: text` and
told the model to cite "the exact segment IDs in the brackets". Gemma read the brackets as
delimiters; Ministral read them as part of the identifier and emitted `"[segment-000123]"`. The
evidence allow-list correctly refused every citation, and correctly emptied the summary.

**The guardrail worked exactly as designed.** The prompt was ambiguous, and an ambiguity two models
resolve differently is a defect in the question rather than in either answer. Segment IDs now stand
alone at the start of the line with nothing wrapped around them, and the prompt says plainly that
brackets are never part of an ID. Both models were re-run against the corrected prompt.

This is precisely what a bake-off is for, and it would not have been found by testing one model.

A second finding, worth recording before real annotation begins: with thin gold aliases both models
scored far below what their output deserved, because generative paraphrase rarely matches an
annotator's wording. **Annotated corpora must carry accepted variants**, or recall will be
systematically understated for every candidate equally — which looks like a quality result and is
not one. The synthetic gold was given realistic aliases (written as paraphrases of the fact, not
copied from model output) and both models improved.

## Evaluation CLI

```bash
dotnet run scripts/evaluate-summaries.cs -- validate
dotnet run scripts/evaluate-summaries.cs -- run --corpus development --compare
dotnet run scripts/evaluate-summaries.cs -- run --corpus release --compare --acceptance-run
python scripts/bench-summary-runtime.py --ladder --kv
python scripts/make-synthetic-corpus.py
```

Checkpointed per meeting/model pair and keyed by a fingerprint covering the corpus gold, the model
revision, the prompt versions and the run settings — a resumed evaluation that reused a result
produced under a different prompt would be two half-experiments reported as one, with the seam
invisible. A failed run never erases a successful earlier one. Reports are written as JSON and as
Markdown, and both name the corpus kind.

## Verification

| Check | Result |
|---|---|
| `dotnet build -c Debug --warnaserror` | 0 warnings, 0 errors |
| `dotnet test` | **584 passed**, 0 failed, 0 skipped |
| `scripts/run-worker-tests.ps1 -Frozen` | **218 passed** |
| `scripts/verify-models.ps1` | PASS, **35 entries** |
| `evaluate-summaries.cs validate` | synthetic OK; development and release absent, as expected |
| `bench-summary-runtime.py --ladder --kv` | 5/5 tiers started; no surviving process |
| Application launch | window opens with the summary panel; closes cleanly (exit 0) |

## Remaining Phase 3 blocker

**One, and it is not a code problem.**

The human-corrected **development** corpus (3–5 meetings) and the held-out **release** corpus
(10–20 meetings) do not exist. Everything that consumes them is built, tested and reproducible:
the format, the validator, the scorer, the thresholds, the preregistered composite, the runner, the
checkpointing and the reports.

Until those meetings are annotated:

- the Phase 3 acceptance quality gate **cannot run**;
- the Gemma-versus-Ministral decision **cannot be made**, only executed once numbers exist;
- no claim about local summary quality is available, and this repository makes none.

Secondary, and unblocked by anything: prompt iteration against the development corpus once it
exists, and re-measuring Q8 against Q4 KV cache for quality rather than only for memory and speed.
