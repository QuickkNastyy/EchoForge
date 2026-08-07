# Ministral 3 14B Instruct 2512, Q4_K_M GGUF — retained model card and licence record

**Artifact ID:** `summary.ministral-3-14b-instruct-2512-q4-k-m`
**Repository:** <https://huggingface.co/mistralai/Ministral-3-14B-Instruct-2512-GGUF>
**Pinned commit:** `74fac473c43357d7fb2671713608183cc72496d0`
**File:** `Ministral-3-14B-Instruct-2512-Q4_K_M.gguf`
**Size:** 8,239,593,024 bytes
**SHA-256:** `824e0f3373e69b84f2cae46fdcb9bd1ebc6ab3bfc7acc125d818b7b8178cc613`
**Publisher:** Mistral AI (the official `mistralai/` organisation, not a community re-quantization)
**Gated:** no
**Verified:** 2026-08-07

## Why this file exists in the manifest

This model is **not** EchoForge's summariser. It is the comparison candidate the architecture plan
requires: Phase 3 includes a preregistered bake-off against Gemma 4 12B on annotated real meetings,
and a comparison you cannot run is not a comparison. Pinning it means the bake-off is reproducible
by anyone with the repository, rather than depending on whatever happened to be on one machine.

It belongs to the `summary-bakeoff` profile, which is deliberately **not** one of
`ProcessingProfile.SummaryProfiles`. Having it installed can never cause EchoForge to summarise
with it; something has to ask for a bake-off explicitly.

## Licence

**Apache-2.0**, as stated by the model card and the Hugging Face model index for the pinned commit.
The full text is retained alongside as `third_party/licenses/Apache-2.0-LICENSE.txt`, shared with
the Gemma 4 entry.

Both bake-off candidates being Apache-2.0 is worth recording, because it means the outcome of the
comparison cannot be forced by a licensing constraint on one side.

## Quantization, and why Q4_K_M here against Q4_0 there

The plan names Q4_K_M for Ministral and Google's QAT Q4_0 for Gemma. That asymmetry is deliberate
on both sides and should not be "corrected":

| | Gemma 4 12B | Ministral 3 14B |
|---|---|---|
| Quantization | Q4_0, quantization-aware **training** | Q4_K_M, post-training |
| Size | 6,975,879,296 bytes | 8,239,593,024 bytes |
| Publisher's own quant | yes | yes |

Each is the best Q4-class file its own author publishes. Gemma's QAT weights were trained to be
quantized, which is why Q4_0 is preferred there despite Q4_K_M normally scoring better at equal
bit-width; Ministral publishes no QAT variant, so Q4_K_M is its strongest comparable.

The 1.26 GB size difference is not noise to be normalised away — it is part of what the comparison
measures. The architecture's constraint is 16 GB of VRAM with a 32K context, and a candidate that
wins on quality while leaving less headroom has not obviously won.

## Multimodal projector

The repository ships `Ministral-3-14B-Instruct-2512-BF16-mmproj.gguf` (879,258,784 bytes).
**Deliberately not pinned.** EchoForge summarises text, and it is a BF16 file — larger, relative to
the quantized weights, than Gemma's projector. Loading it would spend VRAM on a capability neither
candidate is being judged on.

## Tokenizer and chat template

Both live inside the GGUF, as with Gemma, so they cannot drift away from the weights this manifest
pins. Ministral uses Mistral's own template rather than Gemma's `<start_of_turn>` form, which is
exactly the sort of difference the model profile seam exists to hold: the prompt *intent* is shared
between candidates and the template is not.

## Generation settings

The bake-off runs both candidates at temperature 0 with a fixed seed, for the same reason
production does: the task is reading facts out of a transcript and citing them, and a comparison
between two models that each answer differently on re-run is not a comparison at all.

## Reasoning

Ministral 3 Instruct is not a reasoning-mode model in the way Gemma 4 is, so it needs no equivalent
of Gemma's `--reasoning off` pinning. The model profile records this explicitly rather than leaving
it implied by the absence of a flag — Gemma's default turned out to be the opposite of what its own
model card described, and "we did not need to set it" is worth being able to tell apart from "we
forgot to".
