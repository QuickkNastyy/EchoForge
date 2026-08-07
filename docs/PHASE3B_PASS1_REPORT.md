# Phase 3B Pass 1 — production local summarization

**Date:** 2026-08-07
**Scope:** replace the deterministic placeholder with a real local language model, without
relaxing anything Phase 3A refuses. Stops before the annotated real-meeting corpora.

Phase 3A was built guardrails-first specifically so this pass would be a swap rather than a
rewrite. It was: the schema, validator, evidence engine, chunker, recursive fold, bounded repair,
revision storage and coordinator are unchanged in what they *decide*. What changed is what
produces the candidates they judge.

## What is pinned

| | |
|---|---|
| **Model** | `google/gemma-4-12B-it-qat-q4_0-gguf` |
| Commit | `29d097773436b69ff9feafd636ab4cf873786537` |
| File | `gemma-4-12b-it-qat-q4_0.gguf`, 6,975,879,296 bytes |
| SHA-256 | `93567e57a8fe10b23569b9d9ec38cd005deedf71e29477c421a4b83f418a538b` |
| Licence | **Apache-2.0**, not gated |
| **Runtime** | ggml-org/llama.cpp release **b10298** → commit `15586e2d7165570fb3aa7c26e0d442e289ef69de` |
| CUDA build | `llama-b10298-bin-win-cuda-13.3-x64.zip`, 146,523,069 bytes, `6e60ceb4…` |
| CUDA runtime | `cudart-llama-bin-win-cuda-13.3-x64.zip`, 390,970,417 bytes, `1462a050…` |
| CPU build | `llama-b10298-bin-win-cpu-x64.zip`, 18,360,602 bytes, `e64e26cd…` |

**The plan's naming held up.** It specified "the official Gemma 4 12B QAT Q4 GGUF" and estimated
6.98 GB. That artifact exists exactly as described — same publisher, same quantization, and the
size matches to the byte. This was verified against Hugging Face's own model index rather than
assumed, because a plan written ahead of a release is exactly the kind of document that ages into
a plausible-sounding name for something that does not exist.

Two things did need correcting against upstream:

- **The licence changed generation.** Gemma 3 shipped under the Gemma Terms of Use with a use
  policy attached; Gemma 4 is Apache-2.0. Carrying the Gemma 3 assumption forward would have
  propagated obligations that no longer apply and missed the ones that do.
- **CUDA 13.3, not 12.4.** The target GPU is an RTX 5070 Ti — Blackwell, `sm_120`. CUDA 12.4 has
  no kernels for it. A 12.4 build would fall back to PTX JIT or fail, and "it worked on the
  developer's machine" is not a property to ship.

**llama.cpp is pinned by release tag, not by commit.** The tag is what the asset URL is addressed
by, and a manifest that pins one identifier while fetching another pins nothing. Making that
truthful meant teaching three separate checks — the JSON Schema, the Python gate test, and
`ArtifactManifestReader` — that a complete sequential build tag is not an abbreviated hash. The
seven-character floor exists to reject a shortened SHA; `b10298` shortens nothing. It is allowed
as its own shape rather than by lowering the floor, so an abbreviated SHA is still refused.

**The vision projector is deliberately not pinned.** The same repository ships
`mmproj-gemma-4-12b-it-qat-q4_0.gguf`. EchoForge summarises text, and loading a vision tower would
spend VRAM on a capability it never uses.

**The NVIDIA runtime is pinned, and Phase 2 deliberately did not pin CUDA.** That earlier decision
does not carry over: the Phase 2 stack is a CUDA 12 build of CTranslate2, so nothing already
installed for it supplies CUDA 13, and the alternative is a summary path that works or fails
according to whatever happens to be on the machine. Downloading it for local use is not
redistribution; shipping it in an installer is, and that review is still Phase 6's.
`third_party/licenses/nvidia-cuda-redistributable-NOTICE.md` records this so the review starts
from a written decision rather than a discovery.

## Download and install

Through the existing `ArtifactRegistry`, unchanged: `.partial` files, range-request resume, size
before hash, quarantine on mismatch, atomic activation, verification markers. `scripts/fetch-artifacts.cs`
is a thin front door to it and contains no second downloader — a file fetched by the script and a
file fetched by the app are installed by the same code and held to the same standard.

`LlamaRuntimeStager` unpacks the archives, because llama.cpp ships as a zip and something has to.
It only ever extracts an artifact the registry has already hashed, unpacks to a neighbour and
swaps it in so an interrupted unpack cannot leave a directory that looks complete, and stamps the
result with the source digests so a re-pin invalidates it. **A `llama-server.exe` placed in the
staging directory by hand is not accepted** — an unverified binary is not a degraded runtime, it
is an unknown one, and a test asserts it.

Model absence never degrades anything else: a production run with no model refuses *before*
allocating a revision, and the placeholder keeps working.

## Server, not one-shot

`llama-cli` can generate once and exit, which looks more supervisable. It is not, for this
workload. Summarising a meeting is many generations — one per chunk, then per synthesis fold, then
possibly a repair — and each invocation would reload 6.98 GB. A fifteen-chunk meeting would spend
nearly two minutes re-reading the same weights. The server loads once, answers for the job, and
exits with it; the supervision problem is identical either way, and the Job Object is still what
guarantees the outcome.

One slot, 32K context, Q8 KV cache, loopback only, port taken by binding rather than guessed,
fixed seed, greedy decoding, offline environment flags, bounded stderr, startup and generation
timeouts, cancellation checked between every step, and `stop()` in a `finally`. A test asserts no
`llama-server` process survives the job.

**Reasoning is off, and finding out why took a real failure.** The model card says thinking is
*triggered* by a `<|think|>` token at the start of the system prompt. The chat template inside this
GGUF turns it on regardless: with reasoning at its default the model spent its entire 2048-token
reply budget inside `reasoning_content` and returned an empty message, so every extraction failed
as "ran out of room". Both `--reasoning off` and `--reasoning-budget 0` are now pinned, so a future
template change cannot quietly re-enable it. This is recorded at length because it is the kind of
thing that looks like a tuning detail and is actually the difference between working and not.

## Tokenizer and budgeting

The placeholder counted characters because it had no tokenizer. The production backend asks the
pinned GGUF's own tokenizer through the server's `/tokenize` endpoint, and counts the system
prompt and the JSON schema — not only the transcript — because those are real tokens the
transcript does not get to use. A chunk the host planned by characters that turns out not to fit
is split further **on segment boundaries**; it is never truncated, because the fix for "too big"
is more pieces, not less meeting. A single segment larger than the whole context is still sent
rather than dropped.

Backend, runtime profile and seed are now part of the chunk fingerprint. The tokenizer travels
inside the GGUF, so naming the backend names the tokenizer, and a checkpoint cannot survive a
change to either.

## Prompts

`worker/prompts/{extract-v1,synthesize-v1,repair-v1}.txt`, version-controlled, and the version is
already part of the fingerprint and the revision record.

They restate the Phase 3A contract rather than inventing one: cite only supplied IDs, keep
explicit/inferred/unknown apart, never guess an owner, never compute a calendar date (EchoForge
knows the meeting date and the model does not), keep contradictions. Synthesis may only merge —
never invent, never raise a certainty, never drop to shorten. Repair fixes structure and is told
plainly that removing an unsupported item is the correct repair and that the validator has not
been relaxed.

None of them contains `<|think|>`, asserted by a test.

## Schema-constrained generation, and what it does not do

`response_format: json_schema` with a schema deliberately smaller than
`schemas/summary.schema.json`. The model supplies facts and citations; identity, timestamps,
display strings and the final document are assembled by code that cannot get them wrong. The
action schema has **no `due_date` field at all** — a field the model never sees is one it cannot
mis-state.

**A grammar constrains shape, not truth.** Schema-constrained decoding will produce a beautifully
formed citation to a segment that does not exist. So candidates are filtered against the IDs
actually handed to that call, and then validated again, independently, by the host. Nothing in
Phase 3A was loosened.

## Fallback

A documented ladder, most capable first: `cuda-32k` → `cuda-16k` → `cuda-8k` → `cuda-8k-partial`
→ `cpu-8k`. Context is halved before layers move off the GPU, because a shorter context costs more
chunks — which EchoForge handles correctly and visibly — while moving layers to system memory costs
throughput on every token. Every step down emits a protocol warning, reaches the user as "this run
used a reduced context", and the revision records the profile and context that actually ran.

This is not theoretical on the test machine: 6.8 GB of its 16 GB was already in use before
anything loaded.

## Storage and UI

Revisions now record the runtime profile and the context actually used, alongside the existing
model, prompt version, digests, repair attempt and synthesis levels. Mock and production revisions
remain distinguishable in the version list.

The panel names which summariser the next run would use, shows the model's install state with its
real size, and offers the download — which is refused while recording, like everything else. The
production model is preferred only when it is actually installed; defaulting to it otherwise would
make the first click fail for a reason the user never chose.

The overview no longer calls itself a placeholder when it is not one. That text was hard-coded from
when only one backend existed, and a production summary describing itself as a placeholder is
exactly as misleading as the reverse. The smoke test caught it.

## Verification

| Check | Result |
|---|---|
| `dotnet build -c Debug --warnaserror` | 0 warnings, 0 errors |
| `dotnet test` | **536 passed**, 0 failed, 0 skipped |
| `scripts/run-worker-tests.ps1 -Frozen` | **202 passed** |
| `scripts/verify-models.ps1` | PASS, **34 entries** |
| Application launch | window opens with the summary panel and the model selector; closes cleanly |

### Real production smoke test — **passed on this machine**

`dotnet run scripts/smoke-summary.cs`, ~60 s end to end on an RTX 5070 Ti, through the real
coordinator with nothing stubbed. The fixture contains a decision, a reversal of it, an action
with a named owner and an explicit date, an unassigned action, an open question, and a price
change that is discussed and explicitly deferred.

Every assertion passed, including the ones that matter most:

- both contradictory decisions survived — the meeting changed its mind and the summary says so;
- `Alex` / `2026-08-14` came through as an explicit owner and an explicit date;
- *"Someone will have to write it up"* did **not** produce an owner called Someone;
- the deferred price change did **not** appear as a decision;
- every citation resolved in the transcript revision it named;
- no `llama-server` process survived.

**This establishes machinery, not quality.** One fixture proves the pipeline runs locally and that
the guardrails hold. It proves nothing about summary quality on real meetings, and no factual
accuracy claim is made from it.

Measured, for the record: prompt processing ~4574 tok/s, generation ~20 tok/s, model load ~7 s.
The workload is prompt-heavy and generation-light, so this sits inside the plan's 1–10 minute
estimate for a one-hour meeting.

## Remaining Phase 3 work

1. **The two annotated corpora and the acceptance gate** — a 3–5 meeting development set and a
   held-out 10–20 meeting release set, scoring factual precision/recall, owner and date precision,
   evidence validity, coverage, contradiction handling, latency and peak VRAM. This is the Phase 3
   acceptance gate and nothing synthetic substitutes for it.
2. **The Gemma-versus-Ministral bake-off**, run on the release corpus with identical transcript,
   schema, evidence rules and token budget. Note that the plan cites
   `mistralai/Ministral-3-14B-Instruct-2512-GGUF`; that name has **not** been verified upstream in
   this pass and should be checked before any work depends on it, exactly as the Gemma 4 naming was.
3. **A prompt-iteration pass against the development corpus.** The prompts are careful but have
   been exercised against one fixture.
4. **Exercising the fallback ladder on real hardware.** Its rungs are unit-tested and the
   step-down path is wired, but only `cuda-32k` has actually run here.
5. **Peak VRAM and latency instrumentation** recorded per run rather than measured by hand.
6. **Q8 versus Q4 KV cache benchmark**, which the plan asks for and which needs the corpus to be
   meaningful.
