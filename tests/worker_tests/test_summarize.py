"""The deterministic summariser, and the rules that decide what may be shown.

What is under test is not prose quality - the placeholder writes none. It is the part that will
still be doing the work when a real model arrives: which claims may be emitted, what they must
cite, and what happens to a date nobody actually gave.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import Callable, Sequence

import pytest
from conftest import hello_line, run_worker

from echoforge_worker.summarize import (
    EXPLICIT,
    INFERRED,
    UNKNOWN,
    Candidate,
    MockSummaryBackend,
    SummaryChunk,
    SummaryRequest,
    _resolve_due_date,
    build_summary,
    deduplicate,
    read_transcript,
    resolve_summary_backend,
    slice_chunk,
    synthesize,
)


def segment(index: int, text: str, track: str = "microphone", start: float = 0.0) -> dict:
    return {
        "id": f"segment-{index:06d}",
        "epoch": 1,
        "start_seconds": start,
        "end_seconds": start + 3.0,
        "speaker_id": "speaker-you" if track == "microphone" else "speaker-remote",
        "speaker_name": "You" if track == "microphone" else "Remote",
        "source_track": track,
        "text": text,
        "confidence": None,
        "language": "en",
        "words": [],
        "overlaps_segment_ids": [],
    }


def write_transcript(root: Path, segments: list[dict], revision: int = 1) -> Path:
    path = root / "transcript" / f"transcript.v{revision}.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(
            {
                "schema_version": 1,
                "session_id": "01JSUM",
                "transcript_revision": revision,
                "created_at_utc": "2026-08-06T12:00:00+00:00",
                "source_manifest_sha256": "a" * 64,
                "duration_seconds": 600.0,
                "model": {
                    "runtime": "echoforge-mock", "backend": "mock", "model_id": "mock-v1",
                    "revision": "mock-v1", "compute_type": "none",
                    "recognizes_speech": False, "worker_version": "0.1.0",
                },
                "epochs": [{"index": 1, "start_seconds": 0.0, "end_seconds": 600.0}],
                "speakers": [{"id": "speaker-you", "name": "You", "source_track": "microphone"}],
                "languages": [{"source_track": "microphone", "code": "en", "probability": None}],
                "segments": segments,
            }
        ),
        encoding="utf-8",
    )
    return path


def request_for(root: Path, path: Path, chunks: list[SummaryChunk], meeting_date: str | None = None, **kwargs) -> SummaryRequest:
    return SummaryRequest(
        session_id="01JSUM",
        summary_revision=1,
        transcript_revision=1,
        transcript_sha256="b" * 64,
        transcript_path=str(path),
        session_root=str(root),
        output_path=str(root / "summary" / "summary.v1.json"),
        created_at_utc="2026-08-06T12:00:00+00:00",
        prompt_version="meeting-summary-v1",
        backend="mock-summary",
        meeting_date=meeting_date,
        chunks=tuple(chunks),
        **kwargs,
    )


def chunk(index: int, first: int, last: int) -> SummaryChunk:
    return SummaryChunk(
        index=index,
        first_segment_id=f"segment-{first:06d}",
        last_segment_id=f"segment-{last:06d}",
        overlap_before=0,
        overlap_after=0,
        input_fingerprint=f"fp-{index}",
    )


def summarise(root: Path, segments: list[dict], chunks: list[SummaryChunk], **kwargs) -> dict:
    path = write_transcript(root, segments)
    request = request_for(root, path, chunks, **kwargs)
    backend = MockSummaryBackend()

    document, parsed = read_transcript(path)
    candidates = []
    for c in request.chunks:
        candidates.extend(backend.extract(c, slice_chunk(parsed, c), request))

    return build_summary(request, document, parsed, deduplicate(candidates), backend)


# -- honesty ------------------------------------------------------------------------------


def test_the_placeholder_says_it_does_not_summarise(tmp_path) -> None:
    summary = summarise(tmp_path, [segment(1, "We will ship on Friday")], [chunk(0, 1, 1)])

    assert summary["model"]["produces_summaries"] is False
    assert "does not understand" in summary["overview"]
    assert "placeholder" in summary["title"].casefold()


def test_the_backend_registry_offers_only_what_exists() -> None:
    assert resolve_summary_backend("mock-summary").name == "mock-summary"

    with pytest.raises(Exception):
        resolve_summary_backend("gemma-4-12b")


# -- evidence -----------------------------------------------------------------------------


def test_every_emitted_item_cites_a_segment_that_exists(tmp_path) -> None:
    segments = [
        segment(1, "We will ship on Friday", start=10.0),
        segment(2, "Alex will prepare the deck", start=20.0),
        segment(3, "I am worried about the database risk", start=30.0),
    ]

    summary = summarise(tmp_path, segments, [chunk(0, 1, 3)])
    ids = {s["id"] for s in segments}

    emitted = summary["decisions"] + summary["action_items"] + summary["risks"] + summary["key_points"]
    assert emitted

    for item in emitted:
        assert item["evidence"]
        for reference in item["evidence"]:
            assert reference["segment_id"] in ids
            assert reference["transcript_revision"] == 1


def test_citation_times_come_from_the_segment_not_the_extractor(tmp_path) -> None:
    summary = summarise(tmp_path, [segment(1, "We will ship on Friday", start=125.0)], [chunk(0, 1, 1)])

    reference = summary["decisions"][0]["evidence"][0]

    assert reference["start_seconds"] == 125.0
    assert reference["end_seconds"] == 128.0
    assert reference["display_timestamp"] == "00:02:05"


def test_an_item_whose_segments_cannot_be_resolved_is_dropped(tmp_path) -> None:
    path = write_transcript(tmp_path, [segment(1, "We will ship on Friday")])
    document, parsed = read_transcript(path)

    orphan = Candidate(
        kind="decision", text="nobody said this", certainty=EXPLICIT,
        segment_ids=["segment-999999"],
    )

    summary = build_summary(request_for(tmp_path, path, []), document, parsed, [orphan], MockSummaryBackend())

    # There is no partial credit: an item nothing supports is not shown at all.
    assert summary["decisions"] == []


def test_a_chunk_naming_segments_the_transcript_lacks_is_refused(tmp_path) -> None:
    path = write_transcript(tmp_path, [segment(1, "We will ship")])
    _, parsed = read_transcript(path)

    with pytest.raises(Exception) as failure:
        slice_chunk(parsed, chunk(0, 5, 9))

    assert "does not contain" in str(failure.value)


# -- owners -------------------------------------------------------------------------------


def test_an_owner_the_transcript_names_is_explicit(tmp_path) -> None:
    summary = summarise(tmp_path, [segment(1, "Alex will prepare the deck")], [chunk(0, 1, 1)])

    action = summary["action_items"][0]
    assert action["owner"] == "Alex"
    assert action["owner_status"] == EXPLICIT


def test_an_unnamed_owner_stays_unknown_and_null_by_default(tmp_path) -> None:
    summary = summarise(tmp_path, [segment(1, "Someone will have to write it up")], [chunk(0, 1, 1)])

    action = summary["action_items"][0]
    assert action["owner"] is None
    assert action["owner_status"] == UNKNOWN


def test_owner_inference_is_off_unless_asked_for_and_is_marked_when_on(tmp_path) -> None:
    segments = [segment(1, "Someone will have to write it up")]

    default = summarise(tmp_path / "off", segments, [chunk(0, 1, 1)])
    assert default["action_items"][0]["owner_status"] == UNKNOWN

    inferred = summarise(tmp_path / "on", segments, [chunk(0, 1, 1)], infer_owners=True)
    action = inferred["action_items"][0]

    assert action["owner_status"] == INFERRED
    assert action["owner"] == "You"


# -- dates --------------------------------------------------------------------------------


def test_a_relative_date_resolves_only_against_a_known_meeting_date(tmp_path) -> None:
    segments = [segment(1, "Alex will prepare the deck by Friday")]

    without = summarise(tmp_path / "no-date", segments, [chunk(0, 1, 1)])
    action = without["action_items"][0]

    # "Friday" names no particular day on its own.
    assert action["due_date"] is None
    assert action["due_date_status"] == UNKNOWN
    assert action["due_date_text"] == "by Friday"

    # 2026-08-05 is a Wednesday; the Friday after it is the 7th.
    with_date = summarise(tmp_path / "dated", segments, [chunk(0, 1, 1)], meeting_date="2026-08-05")
    resolved = with_date["action_items"][0]

    assert resolved["due_date"] == "2026-08-07"
    assert resolved["due_date_status"] == EXPLICIT


def test_an_ambiguous_relative_date_stays_unknown_even_with_a_meeting_date(tmp_path) -> None:
    summary = summarise(
        tmp_path,
        [segment(1, "Alex will prepare the deck by end of the month")],
        [chunk(0, 1, 1)],
        meeting_date="2026-08-05",
    )

    action = summary["action_items"][0]

    # Which day "end of the month" means is genuinely ambiguous, and picking one is not
    # EchoForge's decision to make.
    assert action["due_date"] is None
    assert action["due_date_status"] == UNKNOWN
    assert "end of the month" in action["due_date_text"]


def test_an_absolute_date_resolves_without_a_meeting_date() -> None:
    assert _resolve_due_date("by 2026-09-01", None) == "2026-09-01"


def test_the_same_weekday_as_the_meeting_means_next_week() -> None:
    # 2026-08-05 is a Wednesday. "by Wednesday" said on a Wednesday is not today.
    assert _resolve_due_date("by Wednesday", "2026-08-05") == "2026-08-12"
    assert _resolve_due_date("by next Friday", "2026-08-05") == "2026-08-14"


def test_a_malformed_meeting_date_resolves_nothing() -> None:
    assert _resolve_due_date("by Friday", "not-a-date") is None


# -- deduplication -------------------------------------------------------------------------


def test_a_decision_seen_in_two_overlapping_chunks_is_merged(tmp_path) -> None:
    segments = [
        segment(1, "Morning everyone", start=0.0),
        segment(2, "We will ship on Friday", start=10.0),
        segment(3, "Anything else", start=20.0),
    ]

    # Two chunks that both contain segment 2.
    summary = summarise(tmp_path, segments, [chunk(0, 1, 2), chunk(1, 2, 3)])

    assert len(summary["decisions"]) == 1


def test_the_same_sentence_said_twice_at_different_points_is_kept_twice(tmp_path) -> None:
    segments = [
        segment(1, "We will ship on Friday", start=10.0),
        segment(2, "Something else entirely", start=200.0),
        segment(3, "We will ship on Friday", start=400.0),
    ]

    summary = summarise(tmp_path, segments, [chunk(0, 1, 3)])

    # Two commitments, not one heard twice: they cite different segments.
    assert len(summary["decisions"]) == 2


def test_contradictory_statements_are_both_preserved(tmp_path) -> None:
    segments = [
        segment(1, "We will ship on Friday", start=10.0),
        segment(2, "We will ship on Monday instead", start=300.0),
    ]

    summary = summarise(tmp_path, segments, [chunk(0, 1, 2)])

    # Silently choosing one would hide that the meeting changed its mind.
    assert len(summary["decisions"]) == 2
    texts = {d["text"] for d in summary["decisions"]}
    assert any("Friday" in t for t in texts) and any("Monday" in t for t in texts)


def test_merging_keeps_the_better_supported_owner() -> None:
    weak = Candidate(kind="action", text="prepare the deck", certainty=EXPLICIT, segment_ids=["segment-000002"])
    strong = Candidate(
        kind="action", text="prepare the deck", certainty=EXPLICIT, segment_ids=["segment-000002"],
        owner="Alex", owner_status=EXPLICIT, chunk_index=1,
    )

    merged = deduplicate([weak, strong])

    assert len(merged) == 1
    assert merged[0].owner == "Alex"
    assert merged[0].owner_status == EXPLICIT


# -- scale and determinism -------------------------------------------------------------------


def test_a_very_long_transcript_is_summarised_without_silent_truncation(tmp_path) -> None:
    segments = [
        segment(i, f"We will ship item {i} on Friday" if i % 25 == 0 else f"Discussion point {i}", start=i * 3.0)
        for i in range(1, 601)
    ]

    chunks = [chunk(index, start, min(start + 49, 600)) for index, start in enumerate(range(1, 601, 50))]
    summary = summarise(tmp_path, segments, chunks)

    # Every marked decision survives: 25, 50, ... 600 is twenty-four of them.
    assert len(summary["decisions"]) == 24


def test_the_same_transcript_always_produces_the_same_summary(tmp_path) -> None:
    segments = [
        segment(1, "We will ship on Friday"),
        segment(2, "Alex will prepare the deck"),
        segment(3, "Blocked on the vendor"),
    ]

    first = summarise(tmp_path / "a", segments, [chunk(0, 1, 3)])
    second = summarise(tmp_path / "b", segments, [chunk(0, 1, 3)])

    assert json.dumps(first, sort_keys=True) == json.dumps(second, sort_keys=True)


def test_a_transcript_with_nothing_notable_produces_an_empty_but_valid_summary(tmp_path) -> None:
    summary = summarise(tmp_path, [segment(1, "Good morning"), segment(2, "Hello")], [chunk(0, 1, 2)])

    assert summary["decisions"] == []
    assert summary["action_items"] == []
    assert summary["schema_version"] == 1
    assert summary["model"]["produces_summaries"] is False


# -- recursive synthesis ----------------------------------------------------------------------


def fact(kind: str, text: str, segment: int, at: float, chunk_index: int = 0, certainty: str = EXPLICIT) -> Candidate:
    return Candidate(
        kind=kind,
        text=text,
        certainty=certainty,
        segment_ids=[f"segment-{segment:06d}"],
        chunk_index=chunk_index,
        first_time=at,
    )


def test_a_meeting_small_enough_folds_in_a_single_pass() -> None:
    facts = [fact("decision", "ship on Friday", 1, 10.0), fact("action", "prepare the deck", 2, 20.0)]

    outcome = synthesize(facts, MockSummaryBackend(), group_size=200)

    assert outcome.levels == 1
    assert outcome.groups == 1
    assert len(outcome.candidates) == 2


def test_one_pass_over_a_small_meeting_is_exactly_the_old_deduplication() -> None:
    facts = [
        fact("decision", "ship on Friday", 2, 10.0, chunk_index=0),
        fact("decision", "ship on Friday", 2, 10.0, chunk_index=1),
        fact("action", "prepare the deck", 3, 20.0),
    ]

    folded = synthesize(list(facts), MockSummaryBackend(), group_size=200).candidates
    deduped = deduplicate(list(facts))

    # The fold is a generalisation of deduplication, not a replacement for it: when everything
    # fits in one group the two must not be able to disagree.
    assert [(c.kind, c.text, c.segment_ids) for c in folded] == [
        (c.kind, c.text, c.segment_ids) for c in deduped
    ]


def test_more_facts_than_one_group_holds_are_folded_over_several_passes() -> None:
    facts = [
        fact("decision", "ship on Friday", 2, 10.0, chunk_index=0),
        fact("decision", "ship on Friday", 2, 10.0, chunk_index=1),
        fact("decision", "cut the scope", 5, 50.0, chunk_index=0),
        fact("decision", "cut the scope", 5, 50.0, chunk_index=1),
    ]

    outcome = synthesize(facts, MockSummaryBackend(), group_size=2)

    assert outcome.levels > 1
    assert len(outcome.candidates) == 2
    assert outcome.merged == 2


def test_two_statements_split_across_groups_still_meet_at_a_later_level() -> None:
    facts = [
        fact("decision", "ship on Friday", 2, 10.0, chunk_index=0),
        fact("decision", "ship on Friday", 2, 10.0, chunk_index=1),
        fact("decision", "cut the scope", 5, 50.0, chunk_index=0),
        fact("decision", "cut the scope", 5, 50.0, chunk_index=1),
    ]

    # Groups of three put the two halves of "cut the scope" on opposite sides of a boundary,
    # so one pass cannot merge them.
    one_pass = sum(len(MockSummaryBackend().synthesize(facts[at : at + 3], None)) for at in (0, 3))
    assert one_pass == 3

    # Folding again re-cuts the groups, and what the boundary separated meets.
    assert len(synthesize(facts, MockSummaryBackend(), group_size=3).candidates) == 2


def test_the_fold_never_drops_a_fact() -> None:
    facts = [fact("decision", f"decision number {i}", i, float(i)) for i in range(1, 41)]

    outcome = synthesize(facts, MockSummaryBackend(), group_size=3)

    # Forty distinct decisions, none of them duplicates of each other. A fold that made them
    # fit by discarding some would be the worst possible way for this to work.
    assert len(outcome.candidates) == 40
    assert {c.text for c in outcome.candidates} == {f"decision number {i}" for i in range(1, 41)}


def test_the_level_cap_returns_everything_rather_than_truncating() -> None:
    facts = [fact("decision", "ship on Friday", 1, 10.0, chunk_index=i) for i in range(8)]

    outcome = synthesize(facts, MockSummaryBackend(), group_size=2, level_cap=2)

    assert outcome.reached_level_cap is True
    # Stopped early, but stopped holding everything it had rather than dropping the remainder.
    assert len(outcome.candidates) == 2


def test_the_same_facts_always_fold_the_same_way() -> None:
    facts = [
        fact("decision", "ship on Friday", 2, 10.0, chunk_index=0),
        fact("action", "prepare the deck", 3, 20.0),
        fact("risk", "the vendor might slip", 4, 30.0),
        fact("decision", "ship on Friday", 2, 10.0, chunk_index=1),
    ]

    first = synthesize(list(facts), MockSummaryBackend(), group_size=2)
    second = synthesize(list(facts), MockSummaryBackend(), group_size=2)

    assert [(c.kind, c.text, c.segment_ids) for c in first.candidates] == [
        (c.kind, c.text, c.segment_ids) for c in second.candidates
    ]
    assert (first.levels, first.groups) == (second.levels, second.groups)


class _MisbehavingBackend(MockSummaryBackend):
    """A backend whose fold does something other than merge. Every kind is refused."""

    def __init__(self, produce: Callable[[Sequence[Candidate]], list[Candidate]]) -> None:
        self._produce = produce

    def synthesize(self, group, request):  # noqa: ANN001, ANN201 - matches the seam
        return self._produce(group)


def test_a_fold_that_cites_a_segment_none_of_its_inputs_cited_is_refused() -> None:
    facts = [fact("decision", "ship on Friday", 1, 10.0)]

    def invent(group):
        return [Candidate(kind="decision", text="ship on Friday", certainty=EXPLICIT, segment_ids=["segment-999999"])]

    with pytest.raises(Exception) as failure:
        synthesize(facts, _MisbehavingBackend(invent), group_size=2)

    assert "none of its inputs cited" in str(failure.value)


def test_a_fold_that_makes_a_claim_none_of_its_inputs_made_is_refused() -> None:
    facts = [fact("decision", "ship on Friday", 1, 10.0)]

    def embellish(group):
        return [Candidate(
            kind="decision", text="ship on Friday, definitely", certainty=EXPLICIT,
            segment_ids=["segment-000001"],
        )]

    with pytest.raises(Exception) as failure:
        synthesize(facts, _MisbehavingBackend(embellish), group_size=2)

    assert "none of its inputs made" in str(failure.value)


def test_a_fold_that_raises_a_certainty_is_refused() -> None:
    facts = [fact("decision", "ship on Friday", 1, 10.0, certainty=INFERRED)]

    def promote(group):
        return [Candidate(
            kind="decision", text="ship on Friday", certainty=EXPLICIT, segment_ids=["segment-000001"],
        )]

    with pytest.raises(Exception) as failure:
        synthesize(facts, _MisbehavingBackend(promote), group_size=2)

    # Explicit, inferred and unknown are not a scale a synthesis pass may climb.
    assert "raised a certainty" in str(failure.value)


def test_a_fold_that_returns_more_than_it_was_given_is_refused() -> None:
    facts = [fact("decision", "ship on Friday", 1, 10.0)]

    with pytest.raises(Exception) as failure:
        synthesize(facts, _MisbehavingBackend(lambda group: list(group) * 2), group_size=2)

    assert "may only merge" in str(failure.value)


def test_the_document_records_how_it_was_folded(tmp_path) -> None:
    path = write_transcript(tmp_path, [segment(1, "We will ship on Friday")])
    document, parsed = read_transcript(path)
    request = request_for(tmp_path, path, [])

    outcome = synthesize(
        [fact("decision", "We will ship on Friday", 1, 0.0)], MockSummaryBackend(), group_size=200
    )
    summary = build_summary(request, document, parsed, outcome.candidates, MockSummaryBackend(), outcome)

    assert summary["synthesis"]["levels"] == 1
    assert summary["synthesis"]["reached_level_cap"] is False
    assert summary["repair_attempt"] == 0


# -- the bounded re-ask -----------------------------------------------------------------------


def test_a_repair_attempt_is_recorded_in_the_document_it_produced(tmp_path) -> None:
    path = write_transcript(tmp_path, [segment(1, "We will ship on Friday")])
    document, parsed = read_transcript(path)

    request = request_for(tmp_path, path, [], repair_attempt=1, rejection_reasons=("cited a segment that does not exist",))
    summary = build_summary(request, document, parsed, [], MockSummaryBackend())

    # A reader can tell that the first answer was refused rather than never having existed.
    assert summary["repair_attempt"] == 1


def test_the_once_modes_damage_only_the_first_generation() -> None:
    from echoforge_worker.testmodes import FaultInjector

    intact = {"schema_version": 1, "decisions": [], "session_id": "s", "summary_revision": 1}

    first = FaultInjector("malformed_summary_once", None, allowed=True, repair_attempt=0)
    assert first.corrupt_summary(intact)["decisions"] != []

    # The re-ask is what the host's one repair attempt is for, so it has to be able to succeed.
    again = FaultInjector("malformed_summary_once", None, allowed=True, repair_attempt=1)
    assert again.corrupt_summary(intact) == intact


def test_the_permanent_modes_damage_every_generation() -> None:
    from echoforge_worker.testmodes import FaultInjector

    intact = {"schema_version": 1, "decisions": [], "session_id": "s", "summary_revision": 1}

    for attempt in (0, 1):
        injector = FaultInjector("malformed_summary", None, allowed=True, repair_attempt=attempt)
        assert injector.corrupt_summary(intact)["decisions"] != []

        truncating = FaultInjector("truncated_summary", None, allowed=True, repair_attempt=attempt)
        assert "decisions" not in truncating.corrupt_summary(intact)


# -- the job over the wire ---------------------------------------------------------------------


def test_a_summarize_job_runs_end_to_end_through_the_worker(tmp_path) -> None:
    path = write_transcript(tmp_path, [
        segment(1, "We will ship on Friday", start=10.0),
        segment(2, "Alex will prepare the deck", start=20.0),
    ])

    output = tmp_path / "summary" / "summary.v1.json"
    start = json.dumps({
        "protocol_version": 1,
        "type": "start_job",
        "job_id": "job-summary",
        "job_kind": "summarize",
        "summary_request": {
            "session_id": "01JSUM",
            "summary_revision": 1,
            "transcript_revision": 1,
            "transcript_sha256": "b" * 64,
            "transcript_path": str(path),
            "session_root": str(tmp_path),
            "output_path": str(output),
            "created_at_utc": "2026-08-06T12:00:00+00:00",
            "prompt_version": "meeting-summary-v1",
            "backend": "mock-summary",
            "chunks": [
                {"index": 0, "first_segment_id": "segment-000001", "last_segment_id": "segment-000002",
                 "overlap_before": 0, "overlap_after": 0, "input_fingerprint": "fp-0"}
            ],
        },
    })

    run = run_worker([hello_line(), start])
    result = run.terminal()

    assert result["type"] == "result", result
    assert output.is_file()

    summary = json.loads(output.read_text(encoding="utf-8"))
    assert len(summary["decisions"]) == 1
    assert summary["action_items"][0]["owner"] == "Alex"

    # Progress was reported and the job ended with exactly one terminal message.
    assert run.of_type("progress")
    assert len([m for m in run.messages if m.get("type") in {"result", "error", "cancelled"}]) == 1


def test_a_summarize_job_with_no_request_is_refused(tmp_path) -> None:
    start = json.dumps({
        "protocol_version": 1, "type": "start_job", "job_id": "j", "job_kind": "summarize",
    })

    run = run_worker([hello_line(), start])

    assert run.terminal()["code"] == "invalid_request"


def test_transcription_jobs_still_work_unchanged(tmp_path) -> None:
    from conftest import simple_session, start_job_line

    run = run_worker([hello_line(), start_job_line(simple_session(tmp_path))])

    assert run.terminal()["type"] == "result"
