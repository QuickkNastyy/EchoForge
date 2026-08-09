"""The two meetings the brief has to get right, and what "right" means for each.

These are deterministic. A language model's output is not, so what is checked here is everything
around it that decides whether its answer can be believed: what reaches the final pass, how a
commitment is classified, what a classification does to the plan, and what the assembled document
says when a meeting genuinely produced no work.

The transcripts are the synthetic corpus meetings — written, never recorded — so the same two
scenarios can also be run against a real model by ``scripts/evaluate-meeting-briefs.py``, which
scores exactly the expectations recorded beside them.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from echoforge_worker.local_summary import LocalSummaryBackend, brief_schema
from echoforge_worker.summarize import (
    ACTION_CLASSIFICATIONS,
    EPHEMERAL_INSTRUCTION,
    EXPLICIT,
    FUTURE_IDEA,
    OUTSTANDING_WORK,
    POST_MEETING_COMMITMENT,
    UNKNOWN,
    Candidate,
    SummaryBackend,
    TranscriptSegment,
    build_summary,
    fallback_brief,
    read_transcript,
)

CORPUS = Path(__file__).resolve().parent.parent / "fixtures" / "summary-benchmark" / "synthetic"


def corpus() -> dict:
    return json.loads((CORPUS / "corpus.json").read_text(encoding="utf-8"))


def meeting(meeting_id: str) -> dict:
    for entry in corpus()["meetings"]:
        if entry["meeting_id"] == meeting_id:
            return entry
    raise AssertionError(f"the synthetic corpus has no meeting {meeting_id!r}")


def transcript_of(meeting_id: str) -> list[TranscriptSegment]:
    _, segments = read_transcript(CORPUS / meeting(meeting_id)["transcript_path"])
    return segments


class Request:
    session_id = "01JSYNTHETIC"
    summary_revision = 1
    transcript_revision = 1
    transcript_sha256 = "b" * 64
    created_at_utc = "2026-08-07T12:00:00+00:00"
    prompt_version = "meeting-brief-v3"
    meeting_date = "2026-08-07"
    infer_owners = False
    infer_due_dates = False
    repair_attempt = 0


class Summarising(SummaryBackend):
    """A backend that claims to summarise, so the document assembly under test is the real one."""

    name = "test-model"
    produces_summaries = True

    def describe(self) -> dict:
        return {
            "runtime": "test", "backend": self.name, "model_id": "test", "revision": "test",
            "context_tokens": 8192, "thinking": False, "produces_summaries": True,
            "worker_version": "0.1.0",
        }

    def extract(self, chunk, segments, request):  # pragma: no cover - not exercised here
        return []


# -- the short recorder test --------------------------------------------------------------------


def test_the_short_test_meeting_is_in_the_corpus_and_expects_no_work() -> None:
    entry = meeting("synthetic-002-short-test")

    assert entry["gold"]["brief"]["action_plan"] == []
    assert "stop the recording" in entry["gold"]["brief"]["must_not_appear_in_plan"]

    # The words are actually in the transcript, so a pipeline that produced a task from them would
    # be grounded and still wrong. That is the whole point of the scenario.
    said = " ".join(segment.text for segment in transcript_of("synthetic-002-short-test")).casefold()
    assert "stop the recording" in said
    assert "getting smaller" in said


def test_an_in_meeting_instruction_never_becomes_work() -> None:
    """The failure this pass exists to fix, at the layer that decides it.

    "Stop the recording" is a real sentence somebody really said, and the extraction records it.
    What it must not do is survive into the plan, because it is not work that outlives the call.
    """
    ephemeral = Candidate(
        kind="action",
        text="Stop the recording.",
        certainty=EXPLICIT,
        segment_ids=["segment-000007"],
        classification=EPHEMERAL_INSTRUCTION,
    )
    observation = Candidate(
        kind="context",
        text="The panel compacts itself while the recording runs.",
        certainty=EXPLICIT,
        segment_ids=["segment-000005"],
    )

    brief = fallback_brief([ephemeral, observation])

    assert brief["action_plan"] == []
    assert EPHEMERAL_INSTRUCTION not in OUTSTANDING_WORK


def test_a_meeting_that_assigned_nothing_produces_a_document_that_says_so() -> None:
    segments = transcript_of("synthetic-002-short-test")
    candidates = [
        Candidate(
            kind="action",
            text="Stop the recording.",
            certainty=EXPLICIT,
            segment_ids=["segment-000007"],
            classification=EPHEMERAL_INSTRUCTION,
            first_time=72.0,
        ),
        Candidate(
            kind="context",
            text="Both speakers noticed the interface becoming more compact as the recording ran.",
            certainty=EXPLICIT,
            segment_ids=["segment-000004"],
            first_time=36.0,
        ),
    ]

    document = build_summary(
        Request(), {"transcript_revision": 1}, segments, candidates, Summarising(),
        brief=fallback_brief(candidates),
    )

    assert document["schema_version"] == 3
    assert document["brief"]["action_plan"] == []

    # The instruction is kept and labelled rather than deleted: somebody looking for it should find
    # it, in a place where it cannot be mistaken for something to do.
    assert len(document["action_items"]) == 1
    assert document["action_items"][0]["classification"] == EPHEMERAL_INSTRUCTION
    assert document["important_context"][0]["text"].startswith("Both speakers noticed")

    # And nothing invented an owner or a date for any of it.
    assert document["action_items"][0]["owner"] is None
    assert document["action_items"][0]["owner_status"] == UNKNOWN
    assert document["action_items"][0]["due_date"] is None


# -- the long work meeting ----------------------------------------------------------------------


def test_the_work_meeting_gold_describes_a_blocker_first_plan() -> None:
    gold = meeting("synthetic-003-work-meeting")["gold"]

    assert gold["brief"]["must_be_first"] == "a1"
    assert gold["brief"]["action_plan"][0] == "a1"

    # The blocker gates the work that follows it, and the meeting says so in words.
    said = " ".join(segment.text for segment in transcript_of("synthetic-003-work-meeting")).casefold()
    assert "rebuild admin console" in said
    assert "waiting on exactly that" in said
    assert "demo" in said

    # Backlog, deferral and the instruction not to touch the ads are all present to be classified.
    assert "put it on the backlog" in said
    assert "do not touch the existing google ads" in said


def test_future_ideas_leave_the_action_plan_and_land_in_the_backlog() -> None:
    idea = Candidate(
        kind="action",
        text="Build a customer portal",
        certainty=EXPLICIT,
        segment_ids=["segment-000018"],
        classification=FUTURE_IDEA,
    )
    commitment = Candidate(
        kind="action",
        text="Finish JR's website integration",
        certainty=EXPLICIT,
        segment_ids=["segment-000009"],
        classification=POST_MEETING_COMMITMENT,
    )

    brief = fallback_brief([idea, commitment])

    titles = [step["title"] for step in brief["action_plan"]]
    assert titles == ["Finish JR's website integration"]


def test_ideas_and_context_reach_the_document_in_their_own_sections() -> None:
    segments = transcript_of("synthetic-003-work-meeting")
    candidates = [
        Candidate(kind="idea", text="Build a customer portal eventually", certainty=EXPLICIT,
                  segment_ids=["segment-000018"], first_time=204.0),
        Candidate(kind="context", text="The JR contract is worth eleven thousand", certainty=EXPLICIT,
                  segment_ids=["segment-000022"], first_time=252.0),
        Candidate(kind="dependency", text="The JR integration cannot be finished without Rebuild access",
                  certainty=EXPLICIT, segment_ids=["segment-000003"], first_time=24.0),
        Candidate(kind="blocker", text="No access to the Rebuild admin console", certainty=EXPLICIT,
                  segment_ids=["segment-000001"], first_time=0.0),
    ]

    document = build_summary(
        Request(), {"transcript_revision": 1}, segments, candidates, Summarising(),
        brief=fallback_brief(candidates),
    )

    assert [item["text"] for item in document["future_ideas"]] == ["Build a customer portal eventually"]
    assert [item["text"] for item in document["important_context"]] == ["The JR contract is worth eleven thousand"]
    assert len(document["dependencies"]) == 1
    assert len(document["blockers"]) == 1


# -- what the pipeline is allowed to ask for ----------------------------------------------------


def test_every_classification_the_prompt_teaches_is_one_the_schema_accepts() -> None:
    """A label the schema rejects is a judgement the model cannot record, however well it made it."""
    from echoforge_worker.local_summary import extraction_schema

    schema = extraction_schema()
    allowed = set(schema["properties"]["action_items"]["items"]["properties"]["classification"]["enum"])

    assert allowed == set(ACTION_CLASSIFICATIONS)
    assert OUTSTANDING_WORK <= allowed


def test_the_schema_cannot_express_a_brief_with_no_summary() -> None:
    """Structural, because a prompt asking for one was not enough.

    On a real short recording the model was told - correctly - that an empty plan is the right
    answer when nothing was assigned, and generalised that permission to the summary: it returned
    every section empty. The brief then fell back to quoting facts, and read like a list of
    disconnected observations rather than a description of the meeting.

    Every other section may be empty. This one may not, and the grammar is where that is settled.
    """
    schema = brief_schema()

    assert schema["properties"]["summary"]["minItems"] == 1
    assert "summary" in schema["required"]

    # And the permission genuinely is per-section: a meeting with no decisions says so by omission.
    for section in ("decisions", "blockers", "backlog", "risks"):
        assert "minItems" not in schema["properties"][section]


def test_the_prompt_protects_the_summary_from_the_empty_plan_rule() -> None:
    from echoforge_worker.local_summary import load_prompt

    brief = load_prompt("brief-v2").casefold()

    assert "always write one" in brief
    assert "never return an empty summary" in brief


def test_the_plan_schema_cannot_express_an_owner_or_a_date() -> None:
    """Structural, not a rule the model is asked to follow.

    Owner and date come from the facts a step names. Leaving the fields out of the schema means a
    final pass cannot introduce one even if it wanted to, which is a stronger guarantee than any
    sentence in a prompt.
    """
    step = brief_schema()["properties"]["action_plan"]["items"]["properties"]

    assert "owner" not in step
    assert "due_date" not in step
    assert "due_date_text" not in step
    assert set(step["audience"]["enum"]) == {"you", "others", "unassigned"}
    assert set(step["timing"]["enum"]) == {"immediate", "next", "later"}
    assert set(step["basis"]["enum"]) == {"explicit", "grounded_inference", "recommendation"}


@pytest.mark.parametrize("meeting_id", ["synthetic-002-short-test", "synthetic-003-work-meeting"])
def test_each_scenario_transcript_matches_the_digest_recorded_beside_it(meeting_id: str) -> None:
    import hashlib

    entry = meeting(meeting_id)
    payload = (CORPUS / entry["transcript_path"]).read_bytes()

    assert hashlib.sha256(payload).hexdigest() == entry["transcript_sha256"]


# -- internal identifiers never reach the reader ------------------------------------------------


@pytest.mark.parametrize(
    "written",
    [
        "The beta ships Monday (decision-001).",
        "The beta ships Monday [decision-001, action-002].",
        "The beta ships Monday, as recorded in decision-001.",
        "According to decision-001, the beta ships Monday.",
        "Per decision-001 and action-002, the beta ships Monday.",
        "The beta ships Monday decision-001",
    ],
)
def test_no_shape_of_a_fact_id_survives_into_prose(written: str) -> None:
    """A brief is read; fact IDs are plumbing.

    Evidence already reaches the reader as a chip they can click, so an identifier in the prose is
    noise at best. The bracketed form was handled from the start and the bare form was not, which
    is how ``as recorded in decision-001`` reached a real brief. Both are covered here because the
    model chooses the shape, not us.
    """
    from echoforge_worker.local_summary import _clean_parenthetical_fact_ids

    cleaned = _clean_parenthetical_fact_ids(written, ["decision-001", "action-002"])

    assert "decision-001" not in cleaned
    assert "action-002" not in cleaned
    assert cleaned.startswith("The beta ships Monday")


def test_cleaning_leaves_prose_that_cites_nothing_exactly_as_written() -> None:
    """The sanitiser removes identifiers, and is not licensed to edit anything else."""
    from echoforge_worker.local_summary import _clean_parenthetical_fact_ids

    written = "Alex will prepare the release notes by Friday, which the team confirmed."

    assert _clean_parenthetical_fact_ids(written, ["decision-001"]) == written
