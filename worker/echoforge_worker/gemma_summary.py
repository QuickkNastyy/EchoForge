"""The production summary backend: Gemma 4 through llama.cpp, behind the Phase 3A seam.

Everything that decides whether a claim may be shown to a user already existed before this file
did, and none of it lives here. This module produces candidates; ``SummaryValidator`` on the host
decides whether they survive. That separation is the point of having built the guardrails first,
and it is why this file contains no evidence checking of its own beyond what it needs in order to
ask a sensible question.

Two things here are genuinely new rather than a swap of one generator for another:

**Token budgeting is real.** The placeholder counted characters because it had no tokenizer. This
backend asks the pinned GGUF's own tokenizer, through the server's ``/tokenize`` endpoint, and
counts the rendered prompt - system text, schema, segment block, and the reply's reserved room -
rather than only the transcript. A chunk the host planned by characters that turns out not to fit
is split further on segment boundaries, never truncated.

**The model can be wrong in ways the placeholder could not.** A grammar constrains shape, not
truth: schema-constrained decoding will produce a beautifully formed citation to a segment that
does not exist. So every candidate is filtered against the segment IDs actually handed to that
call before it leaves this module, and then validated again, independently, on the host.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Final, Sequence

from .llama_server import LlamaServer, LlamaServerError
from .protocol import Cancelled, ErrorCode, Stage, WorkerFailure
from .summarize import (
    EXPLICIT,
    INFERRED,
    UNKNOWN,
    Candidate,
    SummaryBackend,
    TranscriptSegment,
    _resolve_due_date,
    deduplicate,
)

PROMPT_DIRECTORY: Final[Path] = Path(__file__).resolve().parent.parent / "prompts"

#: Room reserved for the model's reply. Extraction output is small and highly structured; this is
#: generous enough that a dense chunk does not hit the ceiling and get reported as truncated.
REPLY_TOKENS: Final[int] = 2048

#: Slack for the chat template's own control tokens, which are added after the text this module
#: measures. Measured against the pinned template it is a couple of dozen tokens; the margin is
#: wide because being wrong in this direction costs one extra chunk and being wrong in the other
#: costs a failed generation.
TEMPLATE_OVERHEAD_TOKENS: Final[int] = 256

_KINDS: Final[dict[str, str]] = {
    "key_points": "key_point",
    "decisions": "decision",
    "action_items": "action",
    "open_questions": "question",
    "risks": "risk",
    "blockers": "blocker",
}

_STATUSES: Final[frozenset[str]] = frozenset({EXPLICIT, INFERRED, UNKNOWN})


def load_prompt(name: str) -> str:
    """Read one versioned prompt. Missing prompt files are a broken install, not a default."""
    path = PROMPT_DIRECTORY / f"{name}.txt"
    try:
        return path.read_text(encoding="utf-8")
    except OSError as error:
        raise WorkerFailure(
            ErrorCode.BACKEND_UNAVAILABLE,
            Stage.PREPARING,
            f"the prompt file {name}.txt could not be read: {type(error).__name__}",
        ) from error


def extraction_schema() -> dict[str, Any]:
    """The shape one extraction call may return.

    Deliberately smaller than ``schemas/summary.schema.json``. The model is asked for facts and
    citations; identity, timestamps, display strings and the final document are assembled by code
    that cannot get them wrong. A model that never sees a field cannot mis-state it.
    """
    item = {
        "type": "object",
        "additionalProperties": False,
        "required": ["text", "certainty", "segment_ids"],
        "properties": {
            "text": {"type": "string", "minLength": 1},
            "certainty": {"type": "string", "enum": [EXPLICIT, INFERRED, UNKNOWN]},
            "segment_ids": {"type": "array", "items": {"type": "string"}, "minItems": 1},
        },
    }

    action = {
        "type": "object",
        "additionalProperties": False,
        "required": ["text", "certainty", "segment_ids", "owner", "owner_status", "due_date_text", "due_date_status"],
        "properties": {
            "text": {"type": "string", "minLength": 1},
            "certainty": {"type": "string", "enum": [EXPLICIT, INFERRED, UNKNOWN]},
            "segment_ids": {"type": "array", "items": {"type": "string"}, "minItems": 1},
            "owner": {"type": ["string", "null"]},
            "owner_status": {"type": "string", "enum": [EXPLICIT, INFERRED, UNKNOWN]},
            # The model reports the words that were said. It never computes a calendar date:
            # it does not know when the meeting was, and EchoForge does.
            "due_date_text": {"type": ["string", "null"]},
            "due_date_status": {"type": "string", "enum": [EXPLICIT, INFERRED, UNKNOWN]},
        },
    }

    return {
        "type": "object",
        "additionalProperties": False,
        "required": list(_KINDS),
        "properties": {
            "key_points": {"type": "array", "items": item},
            "decisions": {"type": "array", "items": item},
            "action_items": {"type": "array", "items": action},
            "open_questions": {"type": "array", "items": item},
            "risks": {"type": "array", "items": item},
            "blockers": {"type": "array", "items": item},
        },
    }


def render_segments(segments: Sequence[TranscriptSegment]) -> str:
    """The transcript slice, in the one form the prompt describes."""
    return "\n".join(f"[{segment.id}] {segment.speaker_name}: {segment.text}" for segment in segments)


@dataclass(frozen=True, slots=True)
class TokenBudget:
    """What one call may spend, measured with the model's own tokenizer."""

    context_tokens: int
    reply_tokens: int
    overhead_tokens: int

    @property
    def available_for_transcript(self) -> int:
        room = self.context_tokens - self.reply_tokens - self.overhead_tokens
        return max(256, room)


class GemmaSummaryBackend(SummaryBackend):
    """Gemma 4 12B, quantization-aware Q4_0, served locally by llama.cpp.

    ``produces_summaries`` is true: unlike the placeholder, this one is a language model reading
    the transcript. That says nothing about whether any particular answer is *correct*, which is
    what the validator and the eventual real-meeting corpus are for.
    """

    name = "gemma-4-12b"
    produces_summaries = True

    def __init__(
        self,
        server: LlamaServer,
        extract_prompt: str | None = None,
        synthesize_prompt: str | None = None,
        repair_prompt: str | None = None,
        prompt_version: str = "meeting-summary-v1",
        model_id: str = "gemma-4-12b-it-qat-q4_0",
        model_revision: str = "",
        worker_version: str | None = None,
    ) -> None:
        self._server = server
        self._extract_prompt = extract_prompt if extract_prompt is not None else load_prompt("extract-v1")
        self._synthesize_prompt = synthesize_prompt if synthesize_prompt is not None else load_prompt("synthesize-v1")
        self._repair_prompt = repair_prompt if repair_prompt is not None else load_prompt("repair-v1")
        self._prompt_version = prompt_version
        self._model_id = model_id
        self._model_revision = model_revision
        self._worker_version = worker_version
        self._schema = extraction_schema()
        self._overhead: int | None = None

    # -- what produced the summary --------------------------------------------------------

    def describe(self) -> dict[str, Any]:
        from .protocol import WORKER_VERSION

        return {
            "runtime": f"llama.cpp ({self._server.profile.name})",
            "backend": self.name,
            "model_id": self._model_id,
            "revision": self._model_revision or self._model_id,
            "context_tokens": self._server.context_tokens,
            # Gemma 4 reasons only when the system prompt opens with <|think|>. EchoForge's
            # prompts do not contain it, so thinking is off by construction rather than by a
            # setting that could be missed.
            "thinking": False,
            "produces_summaries": True,
            "worker_version": self._worker_version or WORKER_VERSION,
        }

    # -- budgeting -------------------------------------------------------------------------

    def budget(self) -> TokenBudget:
        """What fits, measured rather than assumed."""
        if self._overhead is None:
            # The fixed cost of every extraction call: the instructions and the schema the
            # grammar is built from. Counted once, with the real tokenizer.
            fixed = self._server.token_count(self._extract_prompt + json.dumps(self._schema))
            self._overhead = fixed + TEMPLATE_OVERHEAD_TOKENS

        return TokenBudget(
            context_tokens=self._server.context_tokens,
            reply_tokens=REPLY_TOKENS,
            overhead_tokens=self._overhead,
        )

    def fit(self, segments: Sequence[TranscriptSegment]) -> list[list[TranscriptSegment]]:
        """Split a chunk the host planned until each piece actually fits the context.

        The host plans by characters, which is stable and cheap but only an estimate of tokens.
        A dense chunk - a language with a poor token ratio, a wall of numbers, an unusual name
        repeated - can exceed the budget the host believed it was under. The answer is more
        pieces, cut on segment boundaries, never fewer segments.
        """
        if not segments:
            return []

        room = self.budget().available_for_transcript

        if self._server.token_count(render_segments(segments)) <= room:
            return [list(segments)]

        if len(segments) == 1:
            # One segment larger than the whole context. Refusing it would silently drop it from
            # the summary, so it goes as it is and the runtime reports the overflow honestly.
            return [list(segments)]

        middle = len(segments) // 2
        return self.fit(segments[:middle]) + self.fit(segments[middle:])

    # -- the seam ---------------------------------------------------------------------------

    def extract(self, chunk: Any, segments: Sequence[TranscriptSegment], request: Any) -> list[Candidate]:
        candidates: list[Candidate] = []
        chunk_index = int(getattr(chunk, "index", 0))

        for ordinal, piece in enumerate(self.fit(segments)):
            candidates.extend(self._extract_one(piece, chunk_index, ordinal, request))

        return candidates

    def _extract_one(
        self,
        segments: Sequence[TranscriptSegment],
        chunk_index: int,
        piece: int,
        request: Any,
    ) -> list[Candidate]:
        allowed = {segment.id: segment for segment in segments}

        try:
            raw = self._server.generate_json(
                system=self._extract_prompt,
                user=render_segments(segments),
                schema=self._schema,
                max_tokens=REPLY_TOKENS,
            )
        except LlamaServerError as error:
            from .llama_server import failure_from

            raise failure_from(error, Stage.TRANSCRIBING_MICROPHONE) from error

        parsed = _parse(raw)
        if parsed is None:
            # One malformed extraction is not the end of the job. The host's single bounded
            # repair covers the *document*; a chunk that produced unreadable output contributes
            # nothing rather than aborting the meeting.
            return []

        return self._candidates_from(parsed, allowed, chunk_index, piece, request)

    def _candidates_from(
        self,
        parsed: dict[str, Any],
        allowed: dict[str, TranscriptSegment],
        chunk_index: int,
        piece: int,
        request: Any,
    ) -> list[Candidate]:
        candidates: list[Candidate] = []
        ordinal = piece * 1000

        for field, kind in _KINDS.items():
            for entry in parsed.get(field) or []:
                if not isinstance(entry, dict):
                    continue

                candidate = self._candidate(entry, kind, allowed, chunk_index, ordinal, request)
                if candidate is not None:
                    candidates.append(candidate)
                    ordinal += 1

        return candidates

    def _candidate(
        self,
        entry: dict[str, Any],
        kind: str,
        allowed: dict[str, TranscriptSegment],
        chunk_index: int,
        ordinal: int,
        request: Any,
    ) -> Candidate | None:
        text = entry.get("text")
        if not isinstance(text, str) or not text.strip():
            return None

        # The allow-list, applied where the answer is still fresh. A grammar guarantees the
        # citation is a string; it guarantees nothing about the string naming a real segment.
        cited = [
            segment_id
            for segment_id in (entry.get("segment_ids") or [])
            if isinstance(segment_id, str) and segment_id in allowed
        ]
        if not cited:
            return None

        certainty = entry.get("certainty")
        if certainty not in _STATUSES:
            return None

        first = min(allowed[segment_id].start_seconds for segment_id in cited)

        candidate = Candidate(
            kind=kind,
            text=text.strip(),
            certainty=certainty,
            segment_ids=list(dict.fromkeys(cited)),
            chunk_index=chunk_index,
            ordinal=ordinal,
            first_time=first,
        )

        if kind == "action":
            self._fill_action(candidate, entry, request)

        return candidate

    @staticmethod
    def _fill_action(candidate: Candidate, entry: dict[str, Any], request: Any) -> None:
        """Owner and date, held to the same invariants the placeholder was held to."""
        owner = entry.get("owner")
        owner_status = entry.get("owner_status")

        if owner_status in _STATUSES and owner_status != UNKNOWN and isinstance(owner, str) and owner.strip():
            # Inference stays off unless it was asked for, whatever the model decided to claim.
            if owner_status == INFERRED and not getattr(request, "infer_owners", False):
                candidate.owner, candidate.owner_status = None, UNKNOWN
            else:
                candidate.owner, candidate.owner_status = owner.strip(), owner_status
        else:
            candidate.owner, candidate.owner_status = None, UNKNOWN

        phrase = entry.get("due_date_text")
        due_status = entry.get("due_date_status")

        if not isinstance(phrase, str) or not phrase.strip() or due_status not in _STATUSES or due_status == UNKNOWN:
            candidate.due_date_text, candidate.due_date, candidate.due_date_status = None, None, UNKNOWN
            return

        if due_status == INFERRED and not getattr(request, "infer_due_dates", False):
            # Keep the wording, drop the claim. The reader still sees what was said.
            candidate.due_date_text, candidate.due_date, candidate.due_date_status = phrase.strip(), None, UNKNOWN
            return

        candidate.due_date_text = phrase.strip()

        # EchoForge resolves the date, not the model. It is the one that knows the meeting date,
        # and it refuses anything genuinely ambiguous rather than picking a day.
        resolved = _resolve_due_date(phrase, getattr(request, "meeting_date", None))
        if resolved is None:
            candidate.due_date, candidate.due_date_status = None, UNKNOWN
        else:
            candidate.due_date, candidate.due_date_status = resolved, due_status

    def synthesize(self, group: Sequence[Candidate], request: Any) -> list[Candidate]:
        """Merge one group of candidates.

        The model is asked to merge duplicates; the result is then held to the same rule the
        recursive fold enforces on every backend - it may only merge. A synthesis that invents a
        claim, cites something none of its inputs cited, or raises a certainty is rejected by
        ``synthesize`` in the caller, and this method falls back to deterministic deduplication
        rather than failing the job.
        """
        if len(group) < 2:
            return list(group)

        payload = [
            {
                "index": index,
                "kind": candidate.kind,
                "text": candidate.text,
                "certainty": candidate.certainty,
                "segment_ids": candidate.segment_ids,
            }
            for index, candidate in enumerate(group)
        ]

        merge_schema = {
            "type": "object",
            "additionalProperties": False,
            "required": ["merges"],
            "properties": {
                "merges": {
                    "type": "array",
                    "items": {
                        "type": "array",
                        "items": {"type": "integer", "minimum": 0},
                        "minItems": 2,
                    },
                }
            },
        }

        try:
            raw = self._server.generate_json(
                system=self._synthesize_prompt,
                user=json.dumps(payload, ensure_ascii=False),
                schema=merge_schema,
                max_tokens=REPLY_TOKENS,
            )
        except (LlamaServerError, Cancelled):
            # Deterministic merging is a correct answer, just a less clever one.
            return deduplicate(group)

        parsed = _parse(raw)
        if parsed is None:
            return deduplicate(group)

        return _apply_merges(group, parsed.get("merges") or [])

    @property
    def repair_prompt(self) -> str:
        return self._repair_prompt


def _apply_merges(group: Sequence[Candidate], merges: Any) -> list[Candidate]:
    """Fold the groups the model proposed, and only those.

    Every merge is applied by code: the union of the citations, the better-supported owner and
    date, the lower certainty. The model chooses *what* to merge and nothing else, so it has no
    opportunity to rewrite a fact on the way through.
    """
    if not isinstance(merges, list):
        return deduplicate(group)

    absorbed: set[int] = set()
    merged: list[Candidate] = []
    order: dict[int, int] = {}

    for raw_group in merges:
        if not isinstance(raw_group, list):
            continue

        indexes = [i for i in raw_group if isinstance(i, int) and 0 <= i < len(group) and i not in absorbed]
        if len(indexes) < 2:
            continue

        members = [group[i] for i in indexes]
        # Only merge things of the same kind. A decision and the action that follows from it are
        # not one fact, whatever the model thought.
        if len({member.kind for member in members}) != 1:
            continue

        head = members[0]
        combined = Candidate(
            kind=head.kind,
            text=head.text,
            certainty=min((member.certainty for member in members), key=_rank),
            segment_ids=list(dict.fromkeys(sid for member in members for sid in member.segment_ids)),
            chunk_index=head.chunk_index,
            ordinal=head.ordinal,
            first_time=min(member.first_time for member in members),
        )

        for member in members:
            if combined.owner_status == UNKNOWN and member.owner_status != UNKNOWN:
                combined.owner, combined.owner_status = member.owner, member.owner_status
            if combined.due_date_status == UNKNOWN and member.due_date_status != UNKNOWN:
                combined.due_date = member.due_date
                combined.due_date_text = member.due_date_text
                combined.due_date_status = member.due_date_status

        absorbed.update(indexes)
        order[len(merged)] = min(indexes)
        merged.append(combined)

    kept = [candidate for index, candidate in enumerate(group) if index not in absorbed]
    kept.extend(merged)

    return sorted(kept, key=lambda candidate: candidate.sort_key())


def _rank(status: str) -> int:
    return {UNKNOWN: 0, INFERRED: 1, EXPLICIT: 2}.get(status, 0)


def _parse(raw: str) -> dict[str, Any] | None:
    """Read the model's JSON, tolerating the wrappers models add and nothing more.

    A grammar makes this almost always unnecessary, which is the point of using one. It is here
    because "almost always" is not a property to write a pipeline against.
    """
    text = raw.strip()
    if not text:
        return None

    if text.startswith("```"):
        text = text.split("\n", 1)[-1]
        if text.endswith("```"):
            text = text[: -3]
        text = text.strip()
        if text.startswith("json"):
            text = text[4:].strip()

    try:
        parsed = json.loads(text)
    except json.JSONDecodeError:
        start, end = text.find("{"), text.rfind("}")
        if start < 0 or end <= start:
            return None
        try:
            parsed = json.loads(text[start : end + 1])
        except json.JSONDecodeError:
            return None

    return parsed if isinstance(parsed, dict) else None
