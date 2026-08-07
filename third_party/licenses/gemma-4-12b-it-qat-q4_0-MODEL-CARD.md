# Gemma 4 12B Instruction-Tuned, QAT Q4_0 GGUF — retained model card and licence record

**Artifact ID:** `summary.gemma-4-12b-it-qat-q4-0`
**Repository:** <https://huggingface.co/google/gemma-4-12B-it-qat-q4_0-gguf>
**Pinned commit:** `29d097773436b69ff9feafd636ab4cf873786537`
**File:** `gemma-4-12b-it-qat-q4_0.gguf`
**Size:** 6,975,879,296 bytes
**SHA-256:** `93567e57a8fe10b23569b9d9ec38cd005deedf71e29477c421a4b83f418a538b`
**Publisher:** Google (the official `google/` organisation, not a community re-quantization)
**Gated:** no
**Verified:** 2026-08-07

## Licence

**Apache-2.0**, as stated by the model card and by the Hugging Face model index for the pinned
commit.

This is a change from Gemma 3, which shipped under the separate *Gemma Terms of Use* with a use
policy attached. Gemma 4 was released on 2 April 2026 under Apache 2.0. The distinction matters
enough to write down: EchoForge's obligations under Apache 2.0 are attribution and licence
retention, and there is no acceptable-use addendum to propagate to the user. Do not copy Gemma 3
era licence assumptions forward.

The full Apache 2.0 text is retained alongside this file as
`third_party/licenses/Apache-2.0-LICENSE.txt`.

## What EchoForge uses, and what it deliberately does not

The repository contains two files. EchoForge pins **only** the weights:

| File | Pinned | Why |
|---|---|---|
| `gemma-4-12b-it-qat-q4_0.gguf` | yes | The model. Tokenizer and chat template are inside it. |
| `mmproj-gemma-4-12b-it-qat-q4_0.gguf` | **no** | The vision projector. EchoForge summarises a text transcript; loading a vision tower would spend VRAM on a capability it never uses. |

The tokenizer and the chat template live **inside the GGUF**, which is the property that keeps
them coupled to the weights. There is no separate tokenizer file to drift out of step, and no
place where a template could be edited without changing the digest this manifest pins.

## Quantization

Q4_0, produced by Google using **quantization-aware training** rather than post-training
quantization of a released checkpoint. That is the reason this file is preferred over a community
Q4_K_M of the same model despite Q4_K_M usually scoring better at equal bit-width: the QAT weights
were trained to be quantized, and they come from the model's author.

## Generation settings, and where EchoForge departs from the card

The card recommends `temperature=1.0, top_p=0.95, top_k=64` for general use. EchoForge does not use
those for extraction. It generates at **temperature 0 with a fixed seed**, because the task is
reading facts out of a transcript and citing the segments they came from, not writing prose — and
because a summary that changes between two runs over the same transcript cannot be reviewed
against its own evidence. The recommended sampling values are appropriate for open-ended
generation and are recorded here so the departure is a decision rather than an oversight.

## Reasoning / thinking mode

Gemma 4 enters a reasoning mode when the system prompt begins with the `<|think|>` control token,
and emits its reasoning in a `<|channel>thought ... <channel|>` block. **EchoForge never emits that
token.** Thinking is therefore off by construction rather than by a flag that could be missed: the
prompts in `worker/prompts/` contain no `<|think|>`, so there is nothing to disable. The plan
excludes reasoning mode, and a thinking block inside schema-constrained output would also break
the grammar.

## Context

The 12B model supports a 256K context window. EchoForge asks for 32K, which is the plan's target
and a fraction of what the model allows — the binding constraint is 16 GB of VRAM, not the model.
