"""What differs between one local summary model and another, and nothing else.

Phase 3 compares two models on the same work. Almost none of that work is model-specific: the
canonical transcript, the summary schema, the evidence rules, the owner and date invariants, the
prompt *intent*, the token budget policy, the recursive fold, the bounded repair and the revision
storage are all shared, and sharing them is the entire reason a comparison means anything. If
Gemma and Ministral were scored through two pipelines, the pipelines would be part of the result.

So this module holds the short list of things that genuinely cannot be shared - the chat template
lives inside each GGUF, and the server flags each model needs are not the same - and everything
else stays where it was.

The rule that matters: a profile may change *how a model is asked*. It may never change what an
answer has to satisfy. There is deliberately no field here for relaxing validation, adjusting the
evidence rules, or lowering a threshold, because a model that needs those to pass has not passed.
"""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Final


@dataclass(frozen=True, slots=True)
class SummaryModelProfile:
    """One local model's identity and the arguments llama.cpp needs for it."""

    #: The backend name the host asks for, and what gets recorded against the revision.
    backend: str

    #: Stable model identity, independent of where the file happens to live on this machine.
    model_id: str

    #: What to call it in a report a person reads.
    display_name: str

    #: The publisher's own quantization of this file.
    quantization: str

    #: Extra llama-server arguments this model needs. Everything common - context, offload, KV
    #: cache type, one slot, offline - is set by the runtime profile, not here.
    server_args: tuple[str, ...] = ()

    #: Why the reasoning settings are what they are. Recorded rather than left implied by the
    #: absence of a flag: Gemma's default turned out to be the opposite of what its model card
    #: described, and "this model does not need it" has to be distinguishable from "we forgot".
    reasoning_note: str = ""

    #: True when this model may be EchoForge's summariser. A bake-off candidate is something
    #: measured, never something the application quietly starts using because it is installed.
    is_default_candidate: bool = False

    def with_args(self, *extra: str) -> tuple[str, ...]:
        return (*self.server_args, *extra)


#: Gemma 4 12B, Google's quantization-aware-training Q4_0. EchoForge's default.
GEMMA_4_12B: Final[SummaryModelProfile] = SummaryModelProfile(
    backend="gemma-4-12b",
    model_id="gemma-4-12b-it-qat-q4_0",
    display_name="Gemma 4 12B Instruct (QAT Q4_0)",
    quantization="Q4_0",
    # Thinking off, twice over. Gemma 4's model card says reasoning is *triggered* by a <|think|>
    # token at the start of the system prompt; the chat template inside the GGUF turns it on
    # regardless, and left at its default the model spent an entire reply budget inside
    # reasoning_content and returned an empty message. Both the switch and the budget are pinned
    # so a future template change cannot quietly re-enable it.
    server_args=("--reasoning", "off", "--reasoning-budget", "0"),
    reasoning_note=(
        "Reasoning explicitly disabled. Measured, not assumed: this model's template enables "
        "thinking by default despite its card describing it as opt-in."
    ),
    is_default_candidate=True,
)

#: Ministral 3 14B Instruct, Mistral's own Q4_K_M. The bake-off candidate, never the default.
MINISTRAL_3_14B: Final[SummaryModelProfile] = SummaryModelProfile(
    backend="ministral-3-14b",
    model_id="ministral-3-14b-instruct-2512-q4_k_m",
    display_name="Ministral 3 14B Instruct 2512 (Q4_K_M)",
    quantization="Q4_K_M",
    # No reasoning flags. This is an instruct model without a thinking mode to disable, so
    # passing Gemma's flags would either be ignored or refused depending on the build.
    server_args=(),
    reasoning_note=(
        "No reasoning controls needed: this model has no thinking mode to disable. Recorded so "
        "that 'not required' stays distinguishable from 'not set'."
    ),
    is_default_candidate=False,
)

#: OpenAI gpt-oss-20b, native MXFP4 converted by ggml-org. Optional comparison model.
GPT_OSS_20B: Final[SummaryModelProfile] = SummaryModelProfile(
    backend="gpt-oss-20b",
    model_id="gpt-oss-20b-mxfp4",
    display_name="gpt-oss-20b (MXFP4)",
    quantization="MXFP4",
    # The GGUF carries the Harmony/Jinja template. Reasoning is kept low and returned in
    # reasoning_content by llama.cpp; EchoForge reads only final message.content.
    server_args=(
        "--jinja",
        "--reasoning",
        "on",
        "--reasoning-format",
        "auto",
        "--reasoning-budget",
        "1024",
        "--chat-template-kwargs",
        '{"reasoning_effort":"low"}',
    ),
    reasoning_note=(
        "Harmony template enabled with low reasoning effort and a bounded private reasoning "
        "budget. Only the final content channel is persisted or shown."
    ),
    is_default_candidate=False,
)

_PROFILES: Final[dict[str, SummaryModelProfile]] = {
    profile.backend: profile for profile in (GEMMA_4_12B, MINISTRAL_3_14B, GPT_OSS_20B)
}


def available_model_profiles() -> list[str]:
    return sorted(_PROFILES)


def resolve_model_profile(backend: str) -> SummaryModelProfile | None:
    """The profile for a backend name, or None if it is not one of the local models."""
    return _PROFILES.get(backend)


def is_local_model(backend: str) -> bool:
    return backend in _PROFILES
