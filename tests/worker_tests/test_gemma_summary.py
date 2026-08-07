"""The production summary backend, without needing a GPU or a seven-gigabyte model.

What is under test is everything that decides what a language model is allowed to have said:
the allow-list applied to its citations, the owner and date invariants, the merge that may only
merge, and the token budget that decides how much transcript it sees at once. The model itself is
replaced by a fake that answers whatever the test wants it to, including badly - which is the
only way to test the handling of a bad answer, since a real model cannot be asked for one.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from echoforge_worker.gemma_summary import (
    REPLY_TOKENS,
    TEMPLATE_OVERHEAD_TOKENS,
    GemmaSummaryBackend,
    TokenBudget,
    _apply_merges,
    _parse,
    extraction_schema,
    load_prompt,
    render_segments,
)
from echoforge_worker.llama_server import (
    CPU_ONLY_LADDER,
    DEFAULT_LADDER,
    LlamaProfile,
    LlamaServerError,
    _free_port,
    _looks_like_oom,
    failure_from,
)
from echoforge_worker.summarize import EXPLICIT, INFERRED, UNKNOWN, Candidate, TranscriptSegment


class FakeServer:
    """A llama.cpp server that says exactly what a test tells it to."""

    def __init__(self, replies=None, context_tokens: int = 32768, tokens_per_char: float = 0.25) -> None:
        self.profile = LlamaProfile("fake", context_tokens, 99, "q8_0", "fake")
        self.context_tokens = context_tokens
        self._replies = list(replies or [])
        self._tokens_per_char = tokens_per_char
        self.prompts: list[str] = []

    def token_count(self, text: str) -> int:
        return max(1, int(len(text) * self._tokens_per_char))

    def generate_json(self, system, user, schema, max_tokens):  # noqa: ANN001, ANN201
        self.prompts.append(user)
        if not self._replies:
            return json.dumps(_empty())
        reply = self._replies.pop(0)
        if isinstance(reply, Exception):
            raise reply
        return reply if isinstance(reply, str) else json.dumps(reply)


def _empty() -> dict:
    return {
        "key_points": [], "decisions": [], "action_items": [],
        "open_questions": [], "risks": [], "blockers": [],
    }


def segment(index: int, text: str, start: float = 0.0) -> TranscriptSegment:
    return TranscriptSegment(f"segment-{index:06d}", "microphone", "You", text, start, start + 5.0)


class Request:
    def __init__(self, infer_owners=False, infer_due_dates=False, meeting_date=None) -> None:
        self.infer_owners = infer_owners
        self.infer_due_dates = infer_due_dates
        self.meeting_date = meeting_date


class Chunk:
    def __init__(self, index: int = 0) -> None:
        self.index = index


def backend_with(replies=None, **kwargs) -> tuple[GemmaSummaryBackend, FakeServer]:
    server = FakeServer(replies, **kwargs)
    return GemmaSummaryBackend(server, model_revision="rev-under-test"), server


# -- prompts -----------------------------------------------------------------------------------


def test_all_three_production_prompts_exist_and_are_versioned() -> None:
    for name in ("extract-v1", "synthesize-v1", "repair-v1"):
        assert len(load_prompt(name)) > 400, name


def test_a_missing_prompt_is_a_broken_install_not_a_default() -> None:
    with pytest.raises(Exception) as failure:
        load_prompt("no-such-prompt-v9")

    assert "could not be read" in str(failure.value)


def test_the_prompts_carry_the_rules_the_validator_enforces() -> None:
    extract = load_prompt("extract-v1").casefold()

    # The prompt is not the enforcement, but a prompt that contradicted the validator would
    # produce a model that fails constantly for reasons nobody told it about.
    assert "explicit" in extract and "inferred" in extract and "unknown" in extract
    assert "null" in extract
    assert "both" in extract  # contradictions survive
    assert "never compute a calendar date" in extract or "you never compute" in extract

    synthesize = load_plain("synthesize-v1")
    assert "never invent a fact" in synthesize
    assert "never raise a certainty" in synthesize

    repair = load_plain("repair-v1")
    assert "do not add facts" in repair
    assert "has not been relaxed" in repair


def load_plain(name: str) -> str:
    return load_prompt(name).casefold()


def test_no_prompt_asks_the_model_to_think() -> None:
    # Gemma 4 enters reasoning mode when the system prompt opens with this token. Thinking is
    # excluded by the plan, and a reasoning block would break the JSON grammar as well.
    for name in ("extract-v1", "synthesize-v1", "repair-v1"):
        assert "<|think|>" not in load_prompt(name), name


# -- the generation schema ---------------------------------------------------------------------


def test_the_schema_offers_only_the_three_support_states() -> None:
    schema = extraction_schema()
    item = schema["properties"]["decisions"]["items"]

    assert item["properties"]["certainty"]["enum"] == [EXPLICIT, INFERRED, UNKNOWN]
    assert item["additionalProperties"] is False
    assert "segment_ids" in item["required"]


def test_the_schema_never_lets_the_model_emit_a_calendar_date() -> None:
    action = extraction_schema()["properties"]["action_items"]["items"]

    # It reports the words that were said; EchoForge resolves them, because it knows the meeting
    # date and the model does not. A field the model never sees is one it cannot mis-state.
    assert "due_date_text" in action["properties"]
    assert "due_date" not in action["properties"]


def test_segments_are_rendered_with_the_ids_the_prompt_asks_for() -> None:
    rendered = render_segments([segment(1, "We will ship on Friday")])

    assert rendered == "[segment-000001] You: We will ship on Friday"


# -- the allow-list ----------------------------------------------------------------------------


def test_a_citation_to_a_segment_outside_the_slice_is_dropped() -> None:
    reply = _empty()
    reply["decisions"] = [
        {"text": "something nobody said", "certainty": EXPLICIT, "segment_ids": ["segment-999999"]},
        {"text": "we will ship on Friday", "certainty": EXPLICIT, "segment_ids": ["segment-000001"]},
    ]

    backend, _ = backend_with([reply])
    candidates = backend.extract(Chunk(), [segment(1, "We will ship on Friday")], Request())

    # A grammar guarantees the citation is a string. It guarantees nothing about the string
    # naming a segment that exists.
    assert len(candidates) == 1
    assert candidates[0].segment_ids == ["segment-000001"]


def test_an_item_citing_nothing_real_at_all_is_dropped() -> None:
    reply = _empty()
    reply["decisions"] = [{"text": "invented", "certainty": EXPLICIT, "segment_ids": ["segment-424242"]}]

    backend, _ = backend_with([reply])

    assert backend.extract(Chunk(), [segment(1, "hello")], Request()) == []


def test_an_unknown_certainty_is_refused_rather_than_defaulted() -> None:
    reply = _empty()
    reply["decisions"] = [{"text": "x", "certainty": "very-sure", "segment_ids": ["segment-000001"]}]

    backend, _ = backend_with([reply])

    assert backend.extract(Chunk(), [segment(1, "hello")], Request()) == []


def test_a_malformed_reply_costs_one_chunk_not_the_meeting() -> None:
    backend, _ = backend_with(["this is not JSON at all"])

    assert backend.extract(Chunk(), [segment(1, "hello")], Request()) == []


# -- owner and date ----------------------------------------------------------------------------


def action_reply(**overrides) -> dict:
    action = {
        "text": "prepare the deck", "certainty": EXPLICIT, "segment_ids": ["segment-000001"],
        "owner": None, "owner_status": UNKNOWN,
        "due_date_text": None, "due_date_status": UNKNOWN,
    }
    action.update(overrides)
    reply = _empty()
    reply["action_items"] = [action]
    return reply


def test_a_named_owner_survives_as_explicit() -> None:
    backend, _ = backend_with([action_reply(owner="Alex", owner_status=EXPLICIT)])
    candidate = backend.extract(Chunk(), [segment(1, "Alex will prepare the deck")], Request())[0]

    assert (candidate.owner, candidate.owner_status) == ("Alex", EXPLICIT)


def test_an_inferred_owner_is_discarded_unless_inference_was_asked_for() -> None:
    reply = action_reply(owner="Alex", owner_status=INFERRED)

    backend, _ = backend_with([reply])
    off = backend.extract(Chunk(), [segment(1, "someone will prepare the deck")], Request())[0]

    # Inference is off by default, whatever the model decided to claim.
    assert (off.owner, off.owner_status) == (None, UNKNOWN)

    backend, _ = backend_with([action_reply(owner="Alex", owner_status=INFERRED)])
    on = backend.extract(Chunk(), [segment(1, "someone will prepare the deck")], Request(infer_owners=True))[0]

    assert (on.owner, on.owner_status) == ("Alex", INFERRED)


def test_an_unknown_owner_never_keeps_a_name() -> None:
    backend, _ = backend_with([action_reply(owner="Someone", owner_status=UNKNOWN)])
    candidate = backend.extract(Chunk(), [segment(1, "someone will do it")], Request())[0]

    assert candidate.owner is None


def test_the_host_resolves_the_date_and_the_model_only_reports_the_words() -> None:
    backend, _ = backend_with([action_reply(due_date_text="by Friday", due_date_status=EXPLICIT)])

    # 2026-08-05 is a Wednesday, so "by Friday" is the 7th.
    candidate = backend.extract(
        Chunk(), [segment(1, "prepare the deck by Friday")], Request(meeting_date="2026-08-05"))[0]

    assert candidate.due_date == "2026-08-07"
    assert candidate.due_date_text == "by Friday"


def test_a_relative_date_with_no_meeting_date_stays_unknown() -> None:
    backend, _ = backend_with([action_reply(due_date_text="by Friday", due_date_status=EXPLICIT)])
    candidate = backend.extract(Chunk(), [segment(1, "prepare the deck by Friday")], Request())[0]

    assert candidate.due_date is None
    assert candidate.due_date_status == UNKNOWN
    # The wording is kept, so a reader still sees what was actually said.
    assert candidate.due_date_text == "by Friday"


def test_an_ambiguous_date_stays_unknown_even_with_a_meeting_date() -> None:
    backend, _ = backend_with([action_reply(due_date_text="by end of the month", due_date_status=EXPLICIT)])
    candidate = backend.extract(
        Chunk(), [segment(1, "prepare the deck by end of the month")], Request(meeting_date="2026-08-05"))[0]

    assert candidate.due_date is None
    assert candidate.due_date_status == UNKNOWN


# -- contradictions ----------------------------------------------------------------------------


def test_two_opposing_decisions_both_survive_extraction() -> None:
    reply = _empty()
    reply["decisions"] = [
        {"text": "ship on Friday", "certainty": EXPLICIT, "segment_ids": ["segment-000001"]},
        {"text": "ship on Monday instead", "certainty": EXPLICIT, "segment_ids": ["segment-000002"]},
    ]

    backend, _ = backend_with([reply])
    candidates = backend.extract(
        Chunk(), [segment(1, "ship on Friday", 0), segment(2, "ship on Monday instead", 60)], Request())

    assert len(candidates) == 2


# -- the merge that may only merge ---------------------------------------------------------------


def fact(kind: str, text: str, seg: str, certainty: str = EXPLICIT, **kwargs) -> Candidate:
    return Candidate(kind=kind, text=text, certainty=certainty, segment_ids=[seg], **kwargs)


def test_a_merge_unions_the_evidence() -> None:
    group = [fact("decision", "ship on Friday", "segment-000001"),
             fact("decision", "we ship Friday", "segment-000002")]

    merged = _apply_merges(group, [[0, 1]])

    assert len(merged) == 1
    assert set(merged[0].segment_ids) == {"segment-000001", "segment-000002"}


def test_a_merge_keeps_the_lower_certainty() -> None:
    group = [fact("decision", "ship on Friday", "segment-000001", certainty=EXPLICIT),
             fact("decision", "ship Friday", "segment-000002", certainty=INFERRED)]

    merged = _apply_merges(group, [[0, 1]])

    # Merging is not new evidence, so it cannot promote a reading into a statement.
    assert merged[0].certainty == INFERRED


def test_a_merge_keeps_the_better_supported_owner() -> None:
    weak = fact("action", "prepare the deck", "segment-000001")
    strong = fact("action", "prepare deck", "segment-000002", owner="Alex", owner_status=EXPLICIT)

    merged = _apply_merges([weak, strong], [[0, 1]])

    assert (merged[0].owner, merged[0].owner_status) == ("Alex", EXPLICIT)


def test_items_of_different_kinds_are_never_merged() -> None:
    group = [fact("decision", "ship on Friday", "segment-000001"),
             fact("action", "ship on Friday", "segment-000002")]

    merged = _apply_merges(group, [[0, 1]])

    # A decision and the action that follows from it are not one fact.
    assert len(merged) == 2


def test_a_merge_naming_an_item_that_does_not_exist_is_ignored() -> None:
    group = [fact("decision", "ship on Friday", "segment-000001")]

    assert len(_apply_merges(group, [[0, 99]])) == 1


def test_nothing_is_dropped_by_a_merge_nobody_asked_for() -> None:
    group = [fact("decision", f"decision {i}", f"segment-{i:06d}") for i in range(1, 8)]

    assert len(_apply_merges(group, [])) == 7


def test_a_synthesis_the_model_could_not_answer_falls_back_to_deduplication() -> None:
    group = [fact("decision", "ship on Friday", "segment-000001"),
             fact("decision", "ship on Friday", "segment-000001", chunk_index=1)]

    backend, _ = backend_with([LlamaServerError("the runtime stopped")])
    merged = backend.synthesize(group, Request())

    # Deterministic merging is a correct answer, just a less clever one.
    assert len(merged) == 1


# -- token budgeting ----------------------------------------------------------------------------


def test_the_budget_counts_the_prompt_and_the_schema_not_only_the_transcript() -> None:
    backend, server = backend_with()
    budget = backend.budget()

    assert budget.context_tokens == server.context_tokens
    assert budget.reply_tokens == REPLY_TOKENS
    # The instructions and the schema are real tokens the transcript does not get to use.
    assert budget.overhead_tokens > TEMPLATE_OVERHEAD_TOKENS
    assert budget.available_for_transcript < server.context_tokens


def test_available_room_never_goes_negative() -> None:
    budget = TokenBudget(context_tokens=100, reply_tokens=2048, overhead_tokens=4096)

    assert budget.available_for_transcript > 0


def test_a_chunk_that_fits_is_sent_whole() -> None:
    backend, _ = backend_with()
    segments = [segment(i, "short line") for i in range(1, 6)]

    assert len(backend.fit(segments)) == 1


def test_a_chunk_the_host_planned_too_large_is_split_on_segment_boundaries() -> None:
    # A tiny context, so a handful of ordinary segments cannot fit.
    backend, _ = backend_with(context_tokens=1200, tokens_per_char=1.0)
    segments = [segment(i, "a fairly ordinary sentence from a meeting") for i in range(1, 17)]

    pieces = backend.fit(segments)

    assert len(pieces) > 1
    # Nothing is lost and nothing is duplicated: the pieces are exactly the chunk, in order.
    assert [s.id for piece in pieces for s in piece] == [s.id for s in segments]


def test_one_segment_larger_than_the_whole_context_is_still_sent() -> None:
    backend, _ = backend_with(context_tokens=600, tokens_per_char=1.0)
    huge = [segment(1, "x" * 5000)]

    # Refusing it would silently drop it from the summary, which is worse than handing the model
    # something bigger than it asked for and letting the overflow be visible.
    assert len(backend.fit(huge)) == 1


def test_a_split_chunk_is_extracted_once_per_piece() -> None:
    backend, server = backend_with(context_tokens=1200, tokens_per_char=1.0)
    segments = [segment(i, "a fairly ordinary sentence from a meeting") for i in range(1, 17)]

    backend.extract(Chunk(), segments, Request())

    assert len(server.prompts) == len(backend.fit(segments))


# -- what produced it ----------------------------------------------------------------------------


def test_the_backend_records_the_runtime_it_actually_ran_on() -> None:
    backend, server = backend_with()
    described = backend.describe()

    assert described["backend"] == "gemma-4-12b"
    assert described["produces_summaries"] is True
    assert described["thinking"] is False
    assert described["context_tokens"] == server.context_tokens
    assert described["revision"] == "rev-under-test"


# -- reading what the model said -----------------------------------------------------------------


def test_json_wrapped_in_a_code_fence_is_still_read() -> None:
    assert _parse('```json\n{"decisions": []}\n```') == {"decisions": []}


def test_json_with_a_sentence_in_front_of_it_is_still_read() -> None:
    assert _parse('Here you go:\n{"decisions": []}') == {"decisions": []}


def test_something_that_is_not_an_object_is_refused() -> None:
    assert _parse("[1, 2, 3]") is None
    assert _parse("") is None
    assert _parse("completely unparseable {{{") is None


# -- the runtime ladder ---------------------------------------------------------------------------


def test_the_ladder_gives_up_context_before_it_gives_up_the_gpu() -> None:
    names = [profile.name for profile in DEFAULT_LADDER]

    assert names[0] == "cuda-32k"
    assert names.index("cuda-16k") < names.index("cuda-8k-partial")
    assert names.index("cuda-8k-partial") < names.index("cpu-8k")
    assert DEFAULT_LADDER[0].context_tokens == 32768
    assert DEFAULT_LADDER[0].cache_type == "q8_0"


def test_every_rung_of_the_ladder_can_explain_itself() -> None:
    for profile in DEFAULT_LADDER:
        assert profile.description
        assert profile.context_tokens >= 8192


def test_the_cpu_profile_does_not_start_by_trying_the_gpu() -> None:
    assert all(not profile.uses_gpu for profile in CPU_ONLY_LADDER)


def test_running_out_of_memory_is_recognised_however_it_is_worded() -> None:
    assert _looks_like_oom("ggml_backend_cuda_buffer_type_alloc_buffer: failed to allocate")
    assert _looks_like_oom("CUDA error: out of memory")
    assert not _looks_like_oom("model loaded, listening on http://127.0.0.1:9999")


def test_a_runtime_failure_reaches_the_host_as_backend_unavailable() -> None:
    failure = failure_from(LlamaServerError("would not load", out_of_memory=True))

    # There is no separate out-of-memory outcome: by the time anything is reported, every smaller
    # profile has already been tried, so what the host learns is "not here, at any size".
    assert failure.code == "backend_unavailable"
    assert "would not load" in failure.detail


def test_ports_are_taken_rather_than_guessed() -> None:
    first, second = _free_port(), _free_port()

    assert 1024 < first < 65536
    assert 1024 < second < 65536


def test_a_server_pointed_at_a_missing_binary_says_so_without_launching_anything() -> None:
    from echoforge_worker.llama_server import LlamaServer

    server = LlamaServer(
        binary_path=Path("no-such-llama-server.exe"),
        model_path=Path("no-such-model.gguf"),
        profile=DEFAULT_LADDER[0],
    )

    with pytest.raises(LlamaServerError) as failure:
        server.start()

    assert "not where the host said it was" in failure.value.detail


def test_a_runtime_error_never_carries_meeting_content() -> None:
    # Diagnostics reach the log. A transcript line reaching a log is a privacy failure, and the
    # error path is the one place where it would be easy to include "helpful" context.
    error = LlamaServerError("the summary runtime stopped responding")

    assert "segment-" not in error.detail
    assert "transcript" not in error.detail.casefold() or "the transcript could not" in error.detail
