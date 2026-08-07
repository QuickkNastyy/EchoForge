"""The deterministic summariser, and the rules that decide what may be shown.

What is under test is not prose quality - the placeholder writes none. It is the part that will
still be doing the work when a real model arrives: which claims may be emitted, what they must
cite, and what happens to a date nobody actually gave.
"""

from __future__ import annotations

import json
from pathlib import Path

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
