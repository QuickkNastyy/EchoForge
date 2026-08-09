# Local model processing, revisions, and comparison

**Implementation status:** 2026-08-08
**Target:** Windows 11, NVIDIA RTX 5070 Ti class GPU, 16 GB VRAM
**Rule:** setup may use the network; inference never does.

This document is the current authority for EchoForge's model-processing implementation. The
phase reports remain useful historical records of what each pass established, but older statements
about a placeholder-only worker, one transcription model, implicit VAD defaults, or a deterministic
count-based overview no longer describe the application.

## ASR model registry

Model identity, backend/runtime, compute profile, VAD mode, and language are independent fields.
No caller outside the registry has to infer a runtime or window strategy from a model name.

| Model ID | Pinned model revision | Runtime | Language | Timing | Windows | Status |
|---|---|---|---|---|---|---|
| `whisper-large-v3-turbo` | `dropbox-dash/faster-whisper-large-v3-turbo@0a363e9161cbc7ed1431c9597a8ceaf0c4f78fcf` | faster-whisper 1.2.1 / CTranslate2 4.8.1 | Multilingual / auto | Native word timestamps | 600 s / 5 s overlap | Production, legacy-compatible fast option |
| `whisper-large-v3` | `Systran/faster-whisper-large-v3@edaa852ec7e145841d8ffdb056a99866b5f0a478` | faster-whisper 1.2.1 / CTranslate2 4.8.1 | Multilingual / auto | Native word timestamps | 600 s / 5 s overlap | Production, accuracy-oriented option |
| `parakeet-unified-en-0.6b` | `nvidia/parakeet-unified-en-0.6b@fe53cd885760c96b6a5f51a0bfd362cb4584a98b` | NeMo 3.0.0 / PyTorch 2.8.0+cu128, isolated Linux worker | English | Native word/segment timing when the pinned runtime supplies it; otherwise recorded honestly as window-approximate | 300 s / 5 s overlap | Experimental; qualified on an RTX 5070 Ti (loaded, ran, 5.04 GB peak) |
| `canary-qwen-2.5b` | `nvidia/canary-qwen-2.5b@b1469e1bba1cfe140205529c79c434ca47180960` plus pinned Qwen 3 1.7B tokenizer/config at `70d244cc86ccca08cf5af4e1e306ecf908b1ad5e` | NeMo 3.0.0 / PyTorch 2.8.0+cu128, isolated Linux worker | English | Window-approximate segments; no fabricated words | 35 s / 5 s overlap, never over 40 s | Experimental; qualified on an RTX 5070 Ti (loaded, ran, 10.32 GB peak) |

“Accuracy-oriented” is a product configuration label, not a claim that Large V3 beats every other
model on EchoForge recordings. The local evaluation corpus described below is the way to establish
that claim for a particular set of meetings.

Whisper supports CUDA FP16, CUDA INT8/FP16, and CPU INT8. NeMo models support CUDA FP16 and CUDA
BF16 where the GPU/runtime genuinely supports BF16; there is no NeMo CPU fallback. The requested
and actual compute profiles are both stored and displayed. A Whisper fallback is opt-in at the
request seam, recorded as a warning/fallback, and shown in a prominent UI notice.

The Windows Whisper environment includes NVIDIA's exact `nvidia-cublas-cu12` 12.9.2.10 wheel and
its pinned 12.9.86 NVRTC dependency. Setup verifies their size and SHA-256, and the worker activates
the private `nvidia/cublas/bin` directory only for its own process. CUDA capability is reported as
available only when CTranslate2 sees the adapter and both cuBLAS DLLs actually load; adapter
enumeration by itself is not treated as proof. CTranslate2 4.8.1 carries its own cuDNN 9 DLL.

On a usable CUDA machine with at least 8 GB reported VRAM, setup recommends full Whisper Large V3
with CUDA FP16. The RTX 5070 Ti target therefore gets that recommendation. Existing/migrated
installations keep a remembered manual model and compute choice; if full V3 is not installed,
Turbo remains the compatibility choice rather than triggering an inference-time download.

## What "installed" means

**Readiness is a capability, never a file listing.** A model reaches Ready only after the runtime it
needs has loaded it on this machine and it has produced output; the verdict is recorded against the
exact revision that produced it, and a record naming a different revision is discarded rather than
believed. The states a model can be in are `NotInstalled`, `InstallingRuntime`,
`InstallingDependencies`, `DownloadingModel`, `Verifying`, `Testing`, `Ready`, `RestartRequired`,
`Failed` and `RepairAvailable`. Weights on disk with no runtime that can load them show as
"Downloaded · not usable yet", which is what they are.

Physical qualification on the target machine (RTX 5070 Ti, 16 GB, driver 610.88), 2026-08-08:

| Model | Runtime that ran it | Result | Peak VRAM |
|---|---|---|---|
| Whisper Large V3 Turbo | faster-whisper / CTranslate2, CUDA | Installed and verified; previously qualified by real transcription | — |
| Whisper Large V3 | faster-whisper / CTranslate2, CUDA FP16 | Installed and verified; recovered remote speech an earlier model missed | — |
| Parakeet Unified EN 0.6B | NeMo 3.0.0 / PyTorch 2.8.0+cu128 in the provisioned WSL runtime | Restored on the GPU and produced a hypothesis | 5.04 GB |
| Canary-Qwen 2.5B | NeMo 3.0.0 / PyTorch 2.8.0+cu128 in the provisioned WSL runtime | Loaded and generated | 10.32 GB |
| Gemma 4 12B QAT Q4_0 | llama.cpp b10298, CUDA | Previously qualified; drives the default brief | — |
| gpt-oss-20b MXFP4 | llama.cpp b10298, CUDA, 16K context | Loaded on the GPU, answered at 160.9 tok/s, runtime exited cleanly | 15.62 GB |

The gpt-oss figure is the device total reported by `nvidia-smi` while the model was resident, so it
includes anything else on the card; it is labelled as such rather than presented as the model's own
footprint. No CPU or shared-memory fallback was used: the qualification refuses a CPU rung unless
it is explicitly asked for, and reports which rung ran.

## VAD policies

VAD is a named, persisted run setting and its effective parameters are stored with the transcript.

| Mode | Whisper behavior | Intended use |
|---|---|---|
| Accuracy | `vad_filter=false`; the complete prepared window reaches ASR | Maximum recall for quiet, clipped, brief, compressed, or uncertain speech |
| Balanced | Silero threshold `0.35`, negative threshold `0.20`, minimum speech `80 ms`, minimum silence `1000 ms`, padding `500 ms` | Skip obvious sustained silence conservatively |
| Fast | Silero threshold `0.50`, negative threshold `0.35`, minimum speech `200 ms`, minimum silence `500 ms`, padding `200 ms` | Throughput-oriented silence removal |
| Off | `vad_filter=false`, explicitly labelled diagnostic rerun | Compare the same recording with no VAD |

The parameters above match the `VadOptions` API shipped inside pinned faster-whisper 1.2.1. Accuracy
and Off are deliberately non-destructive for NeMo as well. Each run records source and presented
audio duration, VAD retained/excluded duration, speech-region count, ASR segment count, and windows
with nontrivial signal but no returned text. Diagnostics contain no audio or transcript content.

## Immutable transcript revisions

Every accepted run allocates and atomically activates a new `transcript.vN.json`; it never edits an
older revision. The selected revision is a durable preference, not a destructive replacement.
Schema v2 adds bounded run provenance while schema v1 remains readable.

A v2 revision identifies the source manifest SHA-256, ASR model and immutable model revision,
artifact identity, backend and runtime version, worker/protocol version, requested and actual
compute, language, VAD policy/parameters, planner strategy/version, window/overlap, timestamp
capability/precision, creation and processing time, real-time factor, peak VRAM, warnings, and
fallback count. Microphone remains `You`; system/loopback remains `Remote`.

The model-specific planning identity participates in checkpoint fingerprints. Results from another
model, VAD policy, compute choice, glossary, or window strategy cannot be mistaken for reusable
work. Source WAV chunks remain authoritative and unchanged.

## ASR comparison

The model comparison command runs selected installed models sequentially:

1. launch one short-lived worker;
2. load one model, transcribe both preserved tracks, validate and save a revision;
3. exit the worker and release the process/GPU allocation;
4. only then launch the next model.

The comparison view aligns revisions by track and approximate time, separates punctuation-only
changes from text changes, and gives unmatched regions a distinct high-visibility missing state.
It reports characters, words, segments, represented speech duration, timeline coverage, processing
time, peak VRAM, requested/actual compute, VAD, runtime, and warnings. More text is never treated as
an automatic win.

## Summary models and exact transcript linkage

| Model ID | Pinned revision/artifact | Runtime profile | Status |
|---|---|---|---|
| `gemma-4-12b-it-qat-q4_0` | official Google GGUF at `29d097773436b69ff9feafd636ab4cf873786537` | llama.cpp b10298, 32K full-GPU profile | Production |
| `gpt-oss-20b-mxfp4` | ggml-org GGUF conversion at `ef9b12f2ff56c69cf32153a02784e7a3c88bf524` | llama.cpp b10298, Harmony/Jinja, 16K then 8K full-GPU tiers | Experimental comparison option |
| `ministral-3-14b-instruct-q4-k-m` | official Mistral GGUF at `74fac473c43357d7fb2671713608183cc72496d0` | llama.cpp b10298 | Optional benchmark; never selected automatically |

Every summary revision stores the exact transcript revision and transcript SHA-256 it read plus the
summary model/revision and runtime telemetry. Old schema-v1 summaries still render unchanged.

The production pipeline is:

1. chunked evidence-backed fact extraction;
2. validation of segment IDs, evidence, owner/date status, and certainty;
3. hierarchical deduplication/synthesis of validated facts;
4. final narrative JSON passes over validated facts and their transcript evidence, packed with the
   active GGUF tokenizer so fact-dense meetings also fit reduced 8K/16K runtime tiers;
5. validation that every narrative block cites known fact IDs and only their validated evidence;
6. safe fact-text fallback or omission when narrative output cannot be repaired; every such
   fallback is recorded on the immutable revision and shown in the UI.

The user sees substantive `Summary`, `Main Topics`, `Important Details`, and `Follow-Ups` prose when
supported, plus the existing decisions, actions, owners, dates, questions, risks, and blockers.
Empty sections are omitted. Private reasoning output from gpt-oss is not exposed.

Summary comparison holds the transcript constant and runs models sequentially. It compares prose
and every structured category while retaining evidence links. The data model naturally permits
any installed ASR revision to be summarized by any installed summary model; it does not launch a
Cartesian product automatically.

## Installation and offline boundary

Setup lists every optional model, exact download size, installation/verification state, maturity,
and runtime usability. Optional models are never force-downloaded. All production artifacts use
immutable repository revisions, expected sizes, SHA-256 digests, provenance, and retained license
records from `artifacts/manifest.json`. Missing artifacts fail before inference; workers force
offline Hugging Face/Transformers behavior.

The Windows faster-whisper environment and the WSL2/Linux NeMo environment are isolated. NeMo is
not added to the faster-whisper wheel environment. The NeMo weights can be installed independently,
but the UI reports them as runtime-unavailable until an exact Linux Python is configured through
`ECHOFORGE_NEMO_WSL_PYTHON` (and optionally `ECHOFORGE_NEMO_WSL_DISTRIBUTION`). See
`worker-nemo/README.md`.

The repository includes a reproducible 185-package Linux/CPython 3.11 hash lock. Use
`scripts/lock-nemo-runtime.ps1 -Check` to verify it and `scripts/install-nemo-runtime.ps1` to create
the isolated environment from an explicit CPython 3.11 interpreter. The installer can use a local
wheelhouse; model inference itself is always offline.

## Process and VRAM lifetime

Idle means no ASR model and no summary model is resident. ASR runs in a child worker; summaries run
through a child worker that owns an ephemeral `llama-server`. Windows Job Objects terminate process
trees. For WSL, the ready handshake also records the Linux PID and `/proc` start token, so forced
cleanup kills only the exact guest worker before terminating the Windows `wsl.exe` tree. Cancellation,
OOM, protocol failure, app shutdown, and worker crash all converge on process termination. The OS
process boundary is the authoritative GPU cleanup mechanism.

## Local evaluation

`AsrEvaluationCorpus` accepts a source recording/session, optional human-corrected transcript, and
optional known proper names, acronyms, and numeric expressions. `AsrScorer` computes edit-count WER,
normalized WER, short-utterance recall, proper-name/acronym/numeric accuracy, speech-region recall,
You/Remote attribution accuracy, and mean timestamp boundary error. With no reference, EchoForge
offers side-by-side human comparison and makes no WER claim. The existing summary corpus/scorer
covers factual accuracy, omission, unsupported claims, decision/action recall, owner/date accuracy,
evidence validity, and human usefulness review.

## Known limitations

- Canary and Parakeet require the separately qualified WSL2 NeMo environment and remain
  experimental until safe-fixture and real-meeting bake-offs are complete.
- Canary has approximate window timing and no word timing. Parakeet timing metadata is downgraded
  honestly when its runtime does not return native timestamps.
- Model boundaries are fixed and overlapping, not speech-aware. Fixed coverage is safer than
  letting a boundary VAD remove low-energy audio; future speech-aware boundaries must preserve the
  same complete-coverage invariant.
- The comparison view aligns by time/track and emphasizes omissions; it is not a full editorial
  diff, and synchronized audio seeking/scrolling is not yet implemented in that separate window.
- No model is labelled “best.” That requires an EchoForge corpus and hardware measurements.
