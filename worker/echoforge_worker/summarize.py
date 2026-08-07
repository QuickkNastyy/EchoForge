"""Summarisation: the backend seam, the deterministic placeholder, and the pipeline.

The placeholder here **writes nothing**. It reads the real transcript, picks real segments by a
hash of their own text, and assembles prose out of the words that were actually said. Every
citation it emits is a segment ID the host supplied, and every timestamp comes from the segment
rather than from the extractor. It exists so the whole pipeline — chunking, extraction, evidence
validation, deduplication, synthesis, revision activation — can be proven before a language model
is anywhere near it.

That is the point of the ordering. When Gemma arrives it replaces one class behind one interface,
and everything that decides whether a claim may be shown to a user is already written, already
tested, and already refusing things.

The validator, not the extractor, is the final authority. Nothing here is trusted to police
itself: an item that cites a segment the transcript does not contain is dropped by the pipeline,
not by the good manners of whatever produced it.
"""

from __future__ import annotations

import hashlib
import json
import re
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import date, timedelta
from pathlib import Path
from typing import Any, Callable, Final, Sequence

from .protocol import Cancelled, ErrorCode, Stage, WorkerFailure

EXPLICIT: Final[str] = "explicit"
INFERRED: Final[str] = "inferred"
UNKNOWN: Final[str] = "unknown"

#: Weekday names a relative due date might use. Resolved only against a known meeting date.
_WEEKDAYS: Final[dict[str, int]] = {
    "monday": 0, "tuesday": 1, "wednesday": 2, "thursday": 3,
    "friday": 4, "saturday": 5, "sunday": 6,
}

_DECISION_MARKERS: Final[tuple[str, ...]] = ("we will", "we'll", "decided", "agreed", "let's go with")
_ACTION_MARKERS: Final[tuple[str, ...]] = ("will ", "going to", "i'll", "take on", "action")
_QUESTION_MARKERS: Final[tuple[str, ...]] = ("?", "not sure", "unclear", "who owns")
_RISK_MARKERS: Final[tuple[str, ...]] = ("risk", "worried", "concern", "might fail")
_BLOCKER_MARKERS: Final[tuple[str, ...]] = ("blocked", "blocker", "can't proceed", "waiting on")


@dataclass(frozen=True, slots=True)
class SummaryChunk:
    """One deterministic slice of the transcript, named by segment ID rather than by text."""

    index: int
    first_segment_id: str
    last_segment_id: str
    overlap_before: int
    overlap_after: int
    input_fingerprint: str


@dataclass(frozen=True, slots=True)
class SummaryRequest:
    """Everything needed to summarise one transcript revision."""

    session_id: str
    summary_revision: int
    transcript_revision: int
    transcript_sha256: str
    transcript_path: str
    session_root: str
    output_path: str
    created_at_utc: str
    prompt_version: str
    backend: str
    meeting_date: str | None = None
    infer_owners: bool = False
    infer_due_dates: bool = False
    chunks: tuple[SummaryChunk, ...] = ()
    test_mode: str | None = None
    test_delay_seconds: float | None = None

    @staticmethod
    def from_json(obj: Any) -> SummaryRequest:
        def fail(detail: str) -> WorkerFailure:
            return WorkerFailure(ErrorCode.INVALID_REQUEST, Stage.ACCEPTING, detail)

        if not isinstance(obj, dict):
            raise fail("summary request must be an object")

        for field_name in (
            "session_id", "summary_revision", "transcript_revision", "transcript_sha256",
            "transcript_path", "session_root", "output_path", "created_at_utc",
            "prompt_version", "backend",
        ):
            if field_name not in obj:
                raise fail(f"summary request is missing required field {field_name!r}")

        chunks = []
        for raw in obj.get("chunks", []) or []:
            if not isinstance(raw, dict):
                raise fail("chunk must be an object")
            chunks.append(
                SummaryChunk(
                    index=int(raw["index"]),
                    first_segment_id=str(raw["first_segment_id"]),
                    last_segment_id=str(raw["last_segment_id"]),
                    overlap_before=int(raw.get("overlap_before", 0)),
                    overlap_after=int(raw.get("overlap_after", 0)),
                    input_fingerprint=str(raw.get("input_fingerprint", "")),
                )
            )

        return SummaryRequest(
            session_id=str(obj["session_id"]),
            summary_revision=int(obj["summary_revision"]),
            transcript_revision=int(obj["transcript_revision"]),
            transcript_sha256=str(obj["transcript_sha256"]),
            transcript_path=str(obj["transcript_path"]),
            session_root=str(obj["session_root"]),
            output_path=str(obj["output_path"]),
            created_at_utc=str(obj["created_at_utc"]),
            prompt_version=str(obj["prompt_version"]),
            backend=str(obj["backend"]),
            meeting_date=obj.get("meeting_date"),
            infer_owners=bool(obj.get("infer_owners", False)),
            infer_due_dates=bool(obj.get("infer_due_dates", False)),
            chunks=tuple(chunks),
            test_mode=obj.get("test_mode") if isinstance(obj.get("test_mode"), str) else None,
            test_delay_seconds=obj.get("test_delay_seconds"),
        )


@dataclass(frozen=True, slots=True)
class TranscriptSegment:
    """One segment of the transcript, as the summariser sees it."""

    id: str
    source_track: str
    speaker_name: str
    text: str
    start_seconds: float
    end_seconds: float


@dataclass(slots=True)
class Candidate:
    """One extracted fact, before validation and before it has an identity."""

    kind: str
    text: str
    certainty: str
    segment_ids: list[str]
    owner: str | None = None
    owner_status: str = UNKNOWN
    due_date_text: str | None = None
    due_date: str | None = None
    due_date_status: str = UNKNOWN
    confidence: float | None = None
    chunk_index: int = 0
    ordinal: int = 0

    def sort_key(self) -> tuple[str, float, int, int]:
        return (self.kind, self.first_time, self.chunk_index, self.ordinal)

    first_time: float = 0.0


class SummaryBackend(ABC):
    """One way of turning transcript chunks into candidate facts."""

    name: str = ""
    produces_summaries: bool = False

    @abstractmethod
    def describe(self) -> dict[str, Any]:
        """What to record about what produced the summary."""

    @abstractmethod
    def extract(self, chunk: Any, segments: Sequence[TranscriptSegment], request: Any) -> list[Candidate]:
        """Candidates for one chunk. Identity and validation happen afterwards."""


class MockSummaryBackend(SummaryBackend):
    """Deterministic placeholder. Cites real segments; composes nothing.

    It classifies segments by looking for a handful of marker phrases and quotes what was said.
    That is not comprehension and is not presented as any: ``produces_summaries`` is false, the
    overview says so, and no item is ever marked explicit unless it is quoting a segment it also
    cites.
    """

    name = "mock-summary"
    produces_summaries = False

    def describe(self) -> dict[str, Any]:
        from .protocol import WORKER_VERSION

        return {
            "runtime": "echoforge-mock",
            "backend": self.name,
            "model_id": "mock-summary-v1",
            "revision": "mock-summary-v1",
            "context_tokens": 0,
            "thinking": False,
            "produces_summaries": False,
            "worker_version": WORKER_VERSION,
        }

    def extract(self, chunk: Any, segments: Sequence[TranscriptSegment], request: Any) -> list[Candidate]:
        candidates: list[Candidate] = []

        for ordinal, segment in enumerate(segments):
            lowered = segment.text.casefold()
            kind = self._classify(lowered)
            if kind is None:
                continue

            candidate = Candidate(
                kind=kind,
                text=segment.text.strip(),
                # Quoting a segment it also cites is the only thing this backend can honestly
                # call explicit. It never infers.
                certainty=EXPLICIT,
                segment_ids=[segment.id],
                confidence=None,
                chunk_index=chunk.index,
                ordinal=ordinal,
                first_time=segment.start_seconds,
            )

            if kind == "action":
                self._fill_action(candidate, segment, request)

            candidates.append(candidate)

        return candidates

    @staticmethod
    def _classify(lowered: str) -> str | None:
        # Order matters: a blocker that mentions a decision is still a blocker.
        if any(marker in lowered for marker in _BLOCKER_MARKERS):
            return "blocker"
        if any(marker in lowered for marker in _RISK_MARKERS):
            return "risk"
        if any(marker in lowered for marker in _QUESTION_MARKERS):
            return "question"
        if any(marker in lowered for marker in _DECISION_MARKERS):
            return "decision"
        if any(marker in lowered for marker in _ACTION_MARKERS):
            return "action"
        return None

    @staticmethod
    def _fill_action(candidate: Candidate, segment: TranscriptSegment, request: Any) -> None:
        """Owner and date, only where the transcript actually gives them."""
        owner = _explicit_owner(segment)
        if owner is not None:
            candidate.owner = owner
            candidate.owner_status = EXPLICIT
        elif getattr(request, "infer_owners", False):
            # Inference is off by default and, when on, is marked as inference and never
            # promoted. Guessing an owner is the failure that turns a useful draft misleading.
            candidate.owner = segment.speaker_name
            candidate.owner_status = INFERRED

        phrase = _due_phrase(segment.text)
        if phrase is not None:
            candidate.due_date_text = phrase
            resolved = _resolve_due_date(phrase, getattr(request, "meeting_date", None))
            if resolved is not None:
                candidate.due_date = resolved
                candidate.due_date_status = EXPLICIT
            # Unresolvable stays unknown with the phrase kept, so the reader sees "by Friday"
            # and is not handed a date nobody said.


#: Capitalised words that occupy the same grammatical slot as a name and are not one. Without
#: this list, "Someone will write it up" yields an *explicit* owner called Someone - precisely
#: the unsupported-owner failure the whole certainty model exists to prevent, and the more
#: dangerous for looking like a real extraction.
_NOT_A_NAME: Final[frozenset[str]] = frozenset(
    {
        "someone", "somebody", "anyone", "anybody", "everyone", "everybody", "nobody", "none",
        "we", "they", "you", "he", "she", "it", "i", "who", "whoever", "one", "people",
        "this", "that", "there", "then", "these", "those", "the", "our", "their", "his", "her",
        "maybe", "perhaps", "probably", "hopefully", "next", "first", "last", "another", "both",
    }
)


def _explicit_owner(segment: TranscriptSegment) -> str | None:
    """A name the transcript gives outright, as in 'Alex will prepare the deck'.

    Refuses anything on the not-a-name list. An indefinite pronoun in a name's position is the
    transcript saying nobody was assigned, and recording that as an explicit owner would invent
    a commitment from a sentence that deliberately avoided making one.
    """
    match = re.match(r"^\s*([A-Z][a-z]{1,20})\s+(?:will|is going to|to)\b", segment.text)
    if match is None:
        return None

    candidate = match.group(1)
    return None if candidate.casefold() in _NOT_A_NAME else candidate


def _due_phrase(text: str) -> str | None:
    match = re.search(
        r"\b(by|before|on)\s+((?:next\s+|this\s+)?(?:monday|tuesday|wednesday|thursday|friday|saturday|sunday|tomorrow|"
        r"end of (?:the )?(?:week|month|quarter)|\d{4}-\d{2}-\d{2}))",
        text,
        re.IGNORECASE,
    )
    return match.group(0).strip() if match else None


def _resolve_due_date(phrase: str, meeting_date: str | None) -> str | None:
    """Turn a relative phrase into a calendar date, or refuse.

    Nothing resolves without a known meeting date: 'Friday' names no particular day on its own,
    and emitting one would be a guess wearing the clothes of a fact. 'End of the month' and its
    relatives stay unknown even with a meeting date, because which day they mean is genuinely
    ambiguous and picking one is not EchoForge's decision to make.
    """
    lowered = phrase.casefold()

    absolute = re.search(r"\d{4}-\d{2}-\d{2}", lowered)
    if absolute:
        try:
            return date.fromisoformat(absolute.group(0)).isoformat()
        except ValueError:
            return None

    if meeting_date is None:
        return None

    try:
        anchor = date.fromisoformat(meeting_date[:10])
    except (ValueError, TypeError):
        return None

    if "tomorrow" in lowered:
        return (anchor + timedelta(days=1)).isoformat()

    for name, index in _WEEKDAYS.items():
        if name not in lowered:
            continue

        ahead = (index - anchor.weekday()) % 7
        if ahead == 0 or "next" in lowered:
            ahead += 7
        return (anchor + timedelta(days=ahead)).isoformat()

    # "end of the month" and similar: a real range, not a date.
    return None


# --------------------------------------------------------------------------------------
# the pipeline
# --------------------------------------------------------------------------------------


def read_transcript(path: Path) -> tuple[dict[str, Any], list[TranscriptSegment]]:
    """Read the activated transcript revision. Opened read-only; never rewritten."""
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise WorkerFailure(
            ErrorCode.INPUT_INVALID,
            Stage.READING_AUDIO,
            f"the transcript could not be read: {type(error).__name__}",
        ) from error

    segments = [
        TranscriptSegment(
            id=str(raw["id"]),
            source_track=str(raw["source_track"]),
            speaker_name=str(raw["speaker_name"]),
            text=str(raw["text"]),
            start_seconds=float(raw["start_seconds"]),
            end_seconds=float(raw["end_seconds"]),
        )
        for raw in document.get("segments", [])
    ]

    return document, segments


def slice_chunk(segments: Sequence[TranscriptSegment], chunk: Any) -> list[TranscriptSegment]:
    """The segments one chunk covers, resolved out of the transcript by ID."""
    index = {segment.id: position for position, segment in enumerate(segments)}

    first = index.get(chunk.first_segment_id)
    last = index.get(chunk.last_segment_id)

    if first is None or last is None or last < first:
        raise WorkerFailure(
            ErrorCode.INVALID_REQUEST,
            Stage.PREPARING,
            f"chunk {chunk.index} names segments the transcript does not contain",
        )

    return list(segments[first : last + 1])


def deduplicate(candidates: Sequence[Candidate]) -> list[Candidate]:
    """Merge candidates the chunk overlap produced twice.

    Two chunks sharing segments will extract the same sentence twice. Merging requires the
    normalised text to match **and** the citations to intersect - the same words about the same
    moment. Two people saying "we'll ship on Friday" at different points in a meeting cite
    different segments and both survive, because they are two commitments and not one.
    """
    kept: list[Candidate] = []

    for candidate in sorted(candidates, key=lambda c: c.sort_key()):
        needle = _normalise(candidate.text)
        merged = False

        for existing in kept:
            if existing.kind != candidate.kind or _normalise(existing.text) != needle:
                continue
            if not set(existing.segment_ids) & set(candidate.segment_ids):
                continue

            for segment_id in candidate.segment_ids:
                if segment_id not in existing.segment_ids:
                    existing.segment_ids.append(segment_id)

            # Keep the better-supported owner and date rather than whichever came first.
            if existing.owner_status == UNKNOWN and candidate.owner_status != UNKNOWN:
                existing.owner, existing.owner_status = candidate.owner, candidate.owner_status
            if existing.due_date_status == UNKNOWN and candidate.due_date_status != UNKNOWN:
                existing.due_date = candidate.due_date
                existing.due_date_text = candidate.due_date_text
                existing.due_date_status = candidate.due_date_status

            merged = True
            break

        if not merged:
            kept.append(candidate)

    return kept


def _normalise(text: str) -> str:
    folded = re.sub(r"[^\w\s]", " ", text.casefold())
    return re.sub(r"\s+", " ", folded).strip()


def build_summary(
    request: Any,
    document: dict[str, Any],
    segments: Sequence[TranscriptSegment],
    candidates: Sequence[Candidate],
    backend: SummaryBackend,
) -> dict[str, Any]:
    """Assemble the canonical summary, citing only segments that exist.

    Every citation is built from the segment itself, so a timestamp cannot disagree with the
    transcript it points at. A candidate whose segments cannot all be resolved is dropped rather
    than emitted with a broken link.
    """
    by_id = {segment.id: segment for segment in segments}
    revision = int(document.get("transcript_revision", request.transcript_revision))

    def cite(segment_ids: Sequence[str]) -> list[dict[str, Any]]:
        references = []
        for segment_id in segment_ids:
            segment = by_id.get(segment_id)
            if segment is None:
                continue
            references.append(
                {
                    "transcript_revision": revision,
                    "segment_id": segment.id,
                    "source_track": segment.source_track,
                    "start_seconds": segment.start_seconds,
                    "end_seconds": segment.end_seconds,
                    "display_timestamp": _display(segment.start_seconds),
                }
            )
        return references

    buckets: dict[str, list[dict[str, Any]]] = {
        "key_points": [], "decisions": [], "open_questions": [], "risks": [], "blockers": [],
    }
    actions: list[dict[str, Any]] = []
    counters: dict[str, int] = {}

    for candidate in candidates:
        evidence = cite(candidate.segment_ids)
        if not evidence:
            # An item nothing supports is not shown. There is no partial credit here.
            continue

        counters[candidate.kind] = counters.get(candidate.kind, 0) + 1
        identifier = f"{candidate.kind}-{counters[candidate.kind]:03d}"

        if candidate.kind == "action":
            actions.append(
                {
                    "id": identifier,
                    "task": candidate.text,
                    "certainty": candidate.certainty,
                    "confidence": candidate.confidence,
                    "evidence": evidence,
                    "owner": candidate.owner,
                    "owner_status": candidate.owner_status,
                    "due_date": candidate.due_date,
                    "due_date_text": candidate.due_date_text,
                    "due_date_status": candidate.due_date_status,
                }
            )
            continue

        bucket = {
            "decision": "decisions",
            "question": "open_questions",
            "risk": "risks",
            "blocker": "blockers",
        }.get(candidate.kind, "key_points")

        buckets[bucket].append(
            {
                "id": identifier,
                "text": candidate.text,
                "certainty": candidate.certainty,
                "confidence": candidate.confidence,
                "evidence": evidence,
            }
        )

    overview = (
        "This overview was assembled by EchoForge's deterministic placeholder summariser. "
        "It quotes and groups what was said; it does not understand it, and it is not a summary. "
        f"{len(buckets['decisions'])} decisions and {len(actions)} action items were extracted "
        f"from {len(segments)} transcript segments."
    )

    return {
        "schema_version": 1,
        "session_id": request.session_id,
        "summary_revision": request.summary_revision,
        "created_at_utc": request.created_at_utc,
        "transcript_revision": revision,
        "transcript_sha256": request.transcript_sha256,
        "meeting_date": request.meeting_date,
        "prompt_version": request.prompt_version,
        "model": backend.describe(),
        "title": "Meeting summary (placeholder)",
        "overview": overview,
        "key_points": buckets["key_points"],
        "decisions": buckets["decisions"],
        "action_items": actions,
        "open_questions": buckets["open_questions"],
        "risks": buckets["risks"],
        "blockers": buckets["blockers"],
    }


def _display(seconds: float) -> str:
    total = int(max(0.0, seconds))
    return f"{total // 3600:02d}:{(total % 3600) // 60:02d}:{total % 60:02d}"


_BACKENDS: Final[dict[str, type[SummaryBackend]]] = {MockSummaryBackend.name: MockSummaryBackend}


def available_summary_backends() -> list[str]:
    return sorted(_BACKENDS)


def resolve_summary_backend(name: str) -> SummaryBackend:
    factory = _BACKENDS.get(name)
    if factory is None:
        raise WorkerFailure(
            ErrorCode.BACKEND_UNAVAILABLE,
            Stage.PREPARING,
            f"summary backend {name!r} is not available; have {available_summary_backends()}",
        )
    return factory()
