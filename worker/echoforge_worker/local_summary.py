"""The production summary backend: a local model through llama.cpp, behind the Phase 3A seam.

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
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Final, Sequence

from .llama_server import LlamaServer, LlamaServerError
from .model_profiles import GEMMA_4_12B, SummaryModelProfile
from .protocol import Cancelled, ErrorCode, Stage, WorkerFailure
from .summarize import (
    ACTION_CLASSIFICATIONS,
    BRIEF_SECTIONS,
    EXPLICIT,
    INFERRED,
    UNKNOWN,
    Candidate,
    SummaryBackend,
    TranscriptSegment,
    _resolve_due_date,
    candidate_identities,
    deduplicate,
    fallback_brief,
)

PROMPT_DIRECTORY: Final[Path] = Path(__file__).resolve().parent.parent / "prompts"

#: Room reserved for one analysis reply.
#:
#: Raised from 2048 when the analysis stage grew from six kinds to nine. A dense slice of a real
#: work meeting - commitments with classifications, dependencies, ideas, context - genuinely fills
#: more than two thousand tokens, and the failure mode is not graceful: llama.cpp stops mid-object
#: and the whole slice contributes nothing. Found by running an actual meeting through it.
#:
#: This is subtracted from the context before the transcript is fitted, so a larger reply budget
#: buys smaller chunks rather than a risk of overflow.
REPLY_TOKENS: Final[int] = 4096

#: Slack for the chat template's own control tokens, which are added after the text this module
#: measures. Measured against the pinned template it is a couple of dozen tokens; the margin is
#: wide because being wrong in this direction costs one extra chunk and being wrong in the other
#: costs a failed generation.
TEMPLATE_OVERHEAD_TOKENS: Final[int] = 256

#: Room for the final brief. Larger than an extraction reply because this one answer contains the
#: whole document: prose, an ordered plan with reasons, and every section the meeting earned.
BRIEF_REPLY_TOKENS: Final[int] = 4096

_KINDS: Final[dict[str, str]] = {
    "key_points": "key_point",
    "decisions": "decision",
    "action_items": "action",
    "open_questions": "question",
    "risks": "risk",
    "blockers": "blocker",
    "dependencies": "dependency",
    "future_ideas": "idea",
    "important_context": "context",
}

_STATUSES: Final[frozenset[str]] = frozenset({EXPLICIT, INFERRED, UNKNOWN})

_PLAN_AUDIENCES: Final[frozenset[str]] = frozenset({"you", "others", "unassigned"})
_PLAN_TIMINGS: Final[frozenset[str]] = frozenset({"immediate", "next", "later"})
_PLAN_BASES: Final[frozenset[str]] = frozenset({"explicit", "grounded_inference", "recommendation"})


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
        "required": [
            "text", "certainty", "segment_ids", "classification",
            "owner", "owner_status", "due_date_text", "due_date_status",
        ],
        "properties": {
            "text": {"type": "string", "minLength": 1},
            "certainty": {"type": "string", "enum": [EXPLICIT, INFERRED, UNKNOWN]},
            "segment_ids": {"type": "array", "items": {"type": "string"}, "minItems": 1},
            # Required, not optional. A model allowed to omit this would omit it exactly when the
            # judgement is hard, which is the case the field exists for.
            "classification": {"type": "string", "enum": sorted(ACTION_CLASSIFICATIONS)},
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
            "dependencies": {"type": "array", "items": item},
            "future_ideas": {"type": "array", "items": item},
            "important_context": {"type": "array", "items": item},
        },
    }


def brief_schema() -> dict[str, Any]:
    """The shape of the final brief.

    Plan steps name their prerequisites by index rather than by an ID they would have to invent,
    because a model that mints its own identifiers mints them inconsistently, and a dependency
    graph is only useful if every edge resolves.
    """
    block = {
        "type": "object",
        "additionalProperties": False,
        "required": ["text", "fact_ids"],
        "properties": {
            # The pinned llama.cpp b10298 JSON-schema converter supports the structural
            # constraints used here but rejects maxLength/maxItems at request time. REPLY_TOKENS
            # bounds the raw answer; the allow-list bounds what is retained.
            "text": {"type": "string", "minLength": 1},
            "fact_ids": {"type": "array", "minItems": 1, "items": {"type": "string"}},
        },
    }

    step = {
        "type": "object",
        "additionalProperties": False,
        "required": ["title", "detail", "audience", "timing", "basis", "fact_ids"],
        "properties": {
            "title": {"type": "string", "minLength": 1},
            "detail": {"type": "string"},
            "audience": {"type": "string", "enum": sorted(_PLAN_AUDIENCES)},
            "timing": {"type": "string", "enum": sorted(_PLAN_TIMINGS)},
            "basis": {"type": "string", "enum": sorted(_PLAN_BASES)},
            "depends_on": {"type": ["string", "null"]},
            "depends_on_indexes": {"type": "array", "items": {"type": "integer", "minimum": 0}},
            # No owner or due date here on purpose. They are taken from the facts the step names,
            # where they were already held to the owner/date invariants, so the final pass has no
            # opportunity to improve one on the way past.
            "fact_ids": {"type": "array", "minItems": 1, "items": {"type": "string"}},
        },
    }

    properties: dict[str, Any] = {
        section: {"type": "array", "items": block} for section in BRIEF_SECTIONS
    }
    properties["action_plan"] = {"type": "array", "items": step}

    # The summary is the one section that always exists, so the grammar is not allowed to express
    # an absent one. Found on a real recording: a short test meeting assigned no work, the model
    # correctly returned an empty plan, and then generalised that permission to the summary and
    # returned nothing at all - which fell back to quoting facts and read like a list of
    # disconnected observations. minItems is honoured by the pinned llama.cpp schema converter;
    # maxItems and maxLength are not, which is why bounds elsewhere are enforced after the fact.
    properties["summary"] = {"type": "array", "minItems": 1, "items": block}

    return {
        "type": "object",
        "additionalProperties": False,
        "required": ["summary", "action_plan"],
        "properties": properties,
    }


def render_segments(segments: Sequence[TranscriptSegment]) -> str:
    """The transcript slice, in the one form the prompt describes.

    The segment ID stands alone at the start of the line, with nothing wrapped around it. It used
    to be shown as ``[segment-000002]``, and one of the two bake-off models copied the brackets
    into its citations - producing ``"[segment-000002]"``, which the allow-list correctly refused,
    which silently emptied every summary that model produced. The guardrail did its job; the
    prompt was ambiguous about whether the brackets were delimiters or part of the identifier, and
    an ambiguity two models resolve differently is a defect in the question, not in either answer.
    """
    return "\n".join(f"{segment.id}  {segment.speaker_name}: {segment.text}" for segment in segments)


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


class LocalSummaryBackend(SummaryBackend):
    """A local language model, served by llama.cpp, reading the transcript.

    One class for every candidate. What differs between Gemma and Ministral is their chat
    template and their server flags, and both of those live in the model profile - the extraction
    prompt, the schema, the allow-list, the owner and date invariants and the fold are identical,
    which is the only reason a comparison between them measures the models rather than the
    pipelines.

    ``produces_summaries`` is true: unlike the placeholder, this is a language model reading the
    transcript. That says nothing about whether any particular answer is *correct*, which is what
    the validator and the annotated corpus are for.
    """

    produces_summaries = True

    def __init__(
        self,
        server: LlamaServer,
        profile: SummaryModelProfile | None = None,
        extract_prompt: str | None = None,
        synthesize_prompt: str | None = None,
        repair_prompt: str | None = None,
        brief_prompt: str | None = None,
        prompt_version: str = "meeting-brief-v3",
        model_revision: str = "",
        worker_version: str | None = None,
    ) -> None:
        self._profile = profile or GEMMA_4_12B
        self.name = self._profile.backend
        self._server = server
        self._extract_prompt = extract_prompt if extract_prompt is not None else load_prompt("analyze-v2")
        self._synthesize_prompt = synthesize_prompt if synthesize_prompt is not None else load_prompt("synthesize-v2")
        self._repair_prompt = repair_prompt if repair_prompt is not None else load_prompt("repair-v1")
        self._brief_prompt = brief_prompt if brief_prompt is not None else load_prompt("brief-v2")
        self._prompt_version = prompt_version
        self._model_id = self._profile.model_id
        self._model_revision = model_revision
        self._worker_version = worker_version
        self._schema = extraction_schema()
        self._overhead: int | None = None
        #: Set by the job so llama.cpp's own accounting lands in one place. Optional, so a test
        #: can drive the backend without one.
        self.measurements = None

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
                on_response=self._record,
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
        """Owner, date and classification, held to the invariants the placeholder was held to."""
        classification = entry.get("classification")
        # An unrecognised classification is treated as a commitment rather than dropped: losing a
        # real task because a model spelled a label wrong is the worse of the two failures, and the
        # brief prompt is what decides whether it reaches the plan.
        candidate.classification = (
            classification if classification in ACTION_CLASSIFICATIONS else "post_meeting_commitment"
        )

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
                on_response=self._record,
            )
        except Cancelled:
            raise
        except LlamaServerError as error:
            # Deterministic merging is a correct answer, just a less clever one.
            self._note_fallback(
                "synthesis",
                "the model could not merge one fact group; deterministic evidence-preserving "
                f"deduplication ran ({error.detail})",
                error.out_of_memory,
            )
            return deduplicate(group)

        parsed = _parse(raw)
        if parsed is None:
            return deduplicate(group)

        return _apply_merges(group, parsed.get("merges") or [])

    def brief(
        self,
        candidates: Sequence[Candidate],
        segments: Sequence[TranscriptSegment],
        request: Any,
    ) -> dict[str, Any] | None:
        """Write the meeting brief in one pass over the whole meeting.

        This is the stage the previous pipeline got wrong. It used to be handed a bag of extracted
        one-line facts and asked to write prose about them, which is why it could describe a
        meeting but could not tell anybody what to do first: nothing in its input said which piece
        of work was waiting on which other piece, and it had no way to find out.

        So the input is the meeting. When the transcript fits the context with room for the answer,
        the model reads **all of it**, alongside the validated facts. When it does not, it reads a
        consolidated view of every part of the meeting in order - built from those same facts, so
        the beginning and the end both still reach this stage - and the fact text stands in for the
        words. Either way the answer is one call, because an ordered plan cannot be produced in
        batches: a step's position is a claim about every other step.

        Grounding is unchanged. Every block and step names validated facts, and citations are built
        from those facts' own segments, so reasoning further has not made anything easier to
        fabricate.
        """
        identified = candidate_identities(candidates)
        if not identified:
            return None

        by_segment = {segment.id: segment for segment in segments}
        facts = {identifier: candidate for identifier, candidate in identified}
        ordered = sorted(identified, key=lambda pair: (pair[1].first_time, pair[1].sort_key()))

        schema = brief_schema()
        try:
            payload, notes = self._brief_payload(ordered, segments, by_segment, schema)
        except Cancelled:
            raise
        except LlamaServerError as error:
            self._note_fallback(
                "brief",
                f"the runtime could not budget the meeting brief; validated fact text was retained ({error.detail})",
                error.out_of_memory,
            )
            return fallback_brief(candidates)

        for note in notes:
            self._note_fallback("brief", note, False)

        try:
            raw = self._server.generate_json(
                system=self._brief_prompt,
                user=json.dumps(payload, ensure_ascii=False),
                schema=schema,
                max_tokens=BRIEF_REPLY_TOKENS,
                on_response=self._record,
            )
        except Cancelled:
            raise
        except LlamaServerError as error:
            self._note_fallback(
                "brief",
                f"the meeting brief could not be generated; validated fact text was retained ({error.detail})",
                error.out_of_memory,
            )
            return fallback_brief(candidates)

        parsed = _parse(raw)
        brief = _supported_brief(parsed, facts, request) if parsed is not None else None

        if brief is None or not brief["summary"]:
            self._note_fallback(
                "brief",
                "the meeting brief had no supported summary; validated fact text was retained",
                False,
            )
            return fallback_brief(candidates)

        return brief

    def _brief_payload(
        self,
        ordered: Sequence[tuple[str, Candidate]],
        segments: Sequence[TranscriptSegment],
        by_segment: dict[str, TranscriptSegment],
        schema: dict[str, Any],
    ) -> tuple[dict[str, Any], list[str]]:
        """Build the largest honest input that fits, and say what it cost when it had to shrink.

        Four rungs, in order of how much they take away:

        1. every fact with its quoted evidence, plus the entire transcript;
        2. every fact with its quoted evidence, and the transcript replaced by an ordered digest;
        3. every fact, with evidence reduced to identity and time;
        4. the facts that carry work - commitments, decisions, blockers, dependencies, questions,
           ideas - with the descriptive ones dropped last and reported.

        Coverage of the meeting's *time* is preserved at every rung: the digest is built from the
        whole span, so a two-hour meeting's last twenty minutes cannot fall off the end. What
        degrades is detail, and each degradation is recorded on the run rather than hidden.
        """
        fixed = self._server.token_count(self._brief_prompt + json.dumps(schema))
        available = self._server.context_tokens - BRIEF_REPLY_TOKENS - fixed - TEMPLATE_OVERHEAD_TOKENS
        available = max(1024, available)

        speakers = sorted({segment.speaker_name for segment in segments if segment.speaker_name})
        notes: list[str] = []

        def fits(candidate_payload: dict[str, Any]) -> bool:
            return self._server.token_count(json.dumps(candidate_payload, ensure_ascii=False)) <= available

        rich = [_brief_fact(identifier, candidate, by_segment, quote=True) for identifier, candidate in ordered]
        transcript = render_segments(segments)

        whole = {"speakers": speakers, "transcript": transcript, "facts": rich}
        if fits(whole):
            return whole, notes

        digest = _thread_digest(ordered)
        threaded = {"speakers": speakers, "threads": digest, "facts": rich}
        if fits(threaded):
            notes.append(
                "the meeting was too long to give the final pass the whole transcript; it read an "
                "ordered digest covering the entire meeting instead"
            )
            return threaded, notes

        notes.append(
            "the meeting was too long to give the final pass the whole transcript; it read an "
            "ordered digest covering the entire meeting instead"
        )

        slim = [_brief_fact(identifier, candidate, by_segment, quote=False) for identifier, candidate in ordered]
        threaded = {"speakers": speakers, "threads": digest, "facts": slim}
        if fits(threaded):
            notes.append("quoted evidence was omitted from the final pass; every citation was kept")
            return threaded, notes

        notes.append("quoted evidence was omitted from the final pass; every citation was kept")

        # Last rung. Descriptive facts go before anything that could become work.
        essential = [
            _brief_fact(identifier, candidate, by_segment, quote=False)
            for identifier, candidate in ordered
            if candidate.kind != "key_point"
        ]
        dropped = len(slim) - len(essential)
        threaded = {"speakers": speakers, "threads": digest, "facts": essential}
        if fits(threaded) and essential:
            notes.append(
                f"{dropped} descriptive points were withheld from the final pass so that every "
                "commitment, decision and blocker would fit; they remain in the supporting details"
            )
            return threaded, notes

        # Nothing else to give up without losing work. The digest is thinned *within* each part
        # rather than truncated to the first few, because dropping the later parts would drop the
        # end of the meeting - and a brief that silently stops describing a two-hour call after the
        # first hour is worse than a shorter one that covers all of it.
        notes.append(
            "the meeting exceeded what the final pass could read in one call; each part of the "
            "ordered digest was shortened, and every part of the meeting is still represented"
        )
        return {
            "speakers": speakers,
            "threads": [{**part, "about": part["about"][:3]} for part in digest],
            "facts": essential or slim,
        }, notes

    def _note_fallback(self, stage: str, detail: str, out_of_memory: bool) -> None:
        if self.measurements is not None:
            self.measurements.note_fallback(stage, detail, out_of_memory)

    def _record(self, response: dict[str, Any]) -> None:
        if self.measurements is not None:
            self.measurements.record_response(response)

    @property
    def profile(self) -> SummaryModelProfile:
        return self._profile

    @property
    def repair_prompt(self) -> str:
        return self._repair_prompt


def _brief_fact(
    identifier: str,
    candidate: Candidate,
    by_segment: dict[str, TranscriptSegment],
    quote: bool,
) -> dict[str, Any]:
    """One validated fact as the final pass sees it.

    ``quote`` controls whether the cited words travel with the fact. Dropping them is the first
    thing given up on a long meeting, because the fact's own text already says what happened and
    the quotation is duplication - but the citation identities always survive, since those are what
    the reader clicks.
    """
    fact: dict[str, Any] = {
        "fact_id": identifier,
        "kind": candidate.kind,
        "text": candidate.text,
        "at": _display_time(candidate.first_time),
    }

    if candidate.kind == "action":
        fact["classification"] = candidate.classification or "post_meeting_commitment"
        if candidate.owner:
            fact["owner"] = candidate.owner
        if candidate.due_date_text:
            fact["due_date_text"] = candidate.due_date_text

    evidence = []
    for segment_id in candidate.segment_ids:
        segment = by_segment.get(segment_id)
        if segment is None:
            continue
        reference: dict[str, Any] = {"segment_id": segment_id, "speaker": segment.speaker_name}
        if quote:
            reference["text"] = segment.text
        evidence.append(reference)

    fact["evidence"] = evidence
    return fact


#: How many parts a long meeting is digested into. Enough that a two-hour call is described in
#: roughly ten-minute stretches, few enough that the digest itself stays readable.
DIGEST_PARTS: Final[int] = 12


def _thread_digest(ordered: Sequence[tuple[str, Candidate]]) -> list[dict[str, Any]]:
    """An ordered, whole-meeting view for the final pass when the transcript will not fit.

    Built by splitting the meeting's *time span* into equal parts and describing each from the
    facts that fall inside it. Splitting by time rather than by fact count is what guarantees
    complete coverage: a quiet stretch still gets a part, and the last twenty minutes of a long
    meeting cannot be squeezed out by a talkative first hour.
    """
    if not ordered:
        return []

    times = [candidate.first_time for _, candidate in ordered]
    start, end = min(times), max(times)
    span = max(end - start, 1e-6)

    parts: list[list[tuple[str, Candidate]]] = [[] for _ in range(DIGEST_PARTS)]
    for identifier, candidate in ordered:
        index = int((candidate.first_time - start) / span * DIGEST_PARTS)
        parts[min(index, DIGEST_PARTS - 1)].append((identifier, candidate))

    digest: list[dict[str, Any]] = []
    for index, part in enumerate(parts):
        if not part:
            continue
        digest.append(
            {
                "part": index + 1,
                "from": _display_time(start + span * index / DIGEST_PARTS),
                "to": _display_time(start + span * (index + 1) / DIGEST_PARTS),
                "fact_ids": [identifier for identifier, _ in part],
                "about": [candidate.text for _, candidate in part],
            }
        )
    return digest


def _display_time(seconds: float) -> str:
    total = int(max(0.0, seconds))
    return f"{total // 3600:02d}:{(total % 3600) // 60:02d}:{total % 60:02d}"


def _supported_brief(
    parsed: dict[str, Any],
    facts: dict[str, Candidate],
    request: Any,
) -> dict[str, Any] | None:
    """Keep only what the validated facts support, and fill owner and date from those facts.

    The model chose what to say and in what order. It did not choose who owns anything: owner and
    due date are lifted from the action facts a step names, where they already passed the owner and
    date invariants. That is why the plan can be useful about sequencing without being able to
    invent a commitment - the two decisions were never in the same hands.
    """
    result: dict[str, Any] = {section: [] for section in BRIEF_SECTIONS}
    result["action_plan"] = []

    # Every identifier in play, not just the ones a block admits to resting on. A block that
    # mentions a *neighbouring* fact's ID in its prose - "this follows from decision-004" while
    # naming only decision-002 in its own fact_ids - was never cleaned, because the sanitiser was
    # handed that block's own list. The reader saw the identifier. These names are internal and
    # generated, so no meeting can have uttered one, and matching the full set cannot cost a claim.
    known_fact_ids = list(facts)

    def resolve(raw_ids: Any) -> tuple[list[str], list[str]] | None:
        if not isinstance(raw_ids, list):
            return None
        ids = [value for value in raw_ids if isinstance(value, str)]
        # Reject the block rather than silently deleting the unsupported half of its basis.
        if not ids or len(set(ids)) != len(ids) or any(identifier not in facts for identifier in ids):
            return None
        segment_ids: list[str] = []
        for identifier in ids:
            for segment_id in facts[identifier].segment_ids:
                if segment_id not in segment_ids:
                    segment_ids.append(segment_id)
        return (ids, segment_ids) if segment_ids else None

    for section in BRIEF_SECTIONS:
        entries = parsed.get(section)
        if not isinstance(entries, list):
            continue
        seen: set[str] = set()
        for entry in entries[:12]:
            if not isinstance(entry, dict):
                continue
            text = entry.get("text")
            if not isinstance(text, str) or not text.strip():
                continue
            resolved = resolve(entry.get("fact_ids"))
            if resolved is None:
                continue
            cleaned = _clean_parenthetical_fact_ids(text.strip(), known_fact_ids)
            # The same sentence under two headings is the duplication this brief exists to avoid.
            if not cleaned or cleaned.casefold() in seen:
                continue
            seen.add(cleaned.casefold())
            result[section].append(
                {
                    "text": cleaned,
                    "supporting_item_ids": resolved[0],
                    "segment_ids": resolved[1],
                }
            )

    steps = parsed.get("action_plan")
    if isinstance(steps, list):
        for entry in steps[:20]:
            if not isinstance(entry, dict):
                continue
            title = entry.get("title")
            if not isinstance(title, str) or not title.strip():
                continue
            resolved = resolve(entry.get("fact_ids"))
            if resolved is None:
                continue

            named = [facts[identifier] for identifier in resolved[0]]
            owner, owner_status = _best_owner(named, request)
            due_text, due, due_status = _best_due_date(named, request)

            audience = entry.get("audience")
            if audience not in _PLAN_AUDIENCES:
                audience = "unassigned"
            # A step addressed to somebody else with nobody named tells the reader this is not
            # theirs and gives them no one to ask, which is worse than not splitting the list.
            if audience == "others" and not owner:
                audience = "unassigned"

            detail = entry.get("detail")
            result["action_plan"].append(
                {
                    "title": _clean_parenthetical_fact_ids(title.strip(), known_fact_ids),
                    "detail": _clean_parenthetical_fact_ids(detail.strip(), known_fact_ids)
                    if isinstance(detail, str) and detail.strip()
                    else "",
                    "audience": audience,
                    "timing": entry.get("timing") if entry.get("timing") in _PLAN_TIMINGS else "next",
                    "basis": entry.get("basis") if entry.get("basis") in _PLAN_BASES else "recommendation",
                    "depends_on": entry.get("depends_on")
                    if isinstance(entry.get("depends_on"), str) and entry.get("depends_on").strip()
                    else None,
                    "depends_on_indexes": [
                        value for value in (entry.get("depends_on_indexes") or []) if isinstance(value, int)
                    ],
                    "owner": owner,
                    "owner_status": owner_status,
                    "due_date": due,
                    "due_date_text": due_text,
                    "due_date_status": due_status,
                    "supporting_item_ids": resolved[0],
                    "segment_ids": resolved[1],
                }
            )

    return result


def _best_owner(named: Sequence[Candidate], request: Any) -> tuple[str | None, str]:
    """The best-supported owner among the facts a step names, or nobody."""
    best: tuple[str | None, str] = (None, UNKNOWN)
    for candidate in named:
        if candidate.owner_status == UNKNOWN or not candidate.owner:
            continue
        if candidate.owner_status == INFERRED and not getattr(request, "infer_owners", False):
            continue
        if _rank(candidate.owner_status) > _rank(best[1]):
            best = (candidate.owner, candidate.owner_status)
    return best


def _best_due_date(named: Sequence[Candidate], request: Any) -> tuple[str | None, str | None, str]:
    """The best-supported date among the facts a step names. EchoForge resolves it, never a model."""
    best: tuple[str | None, str | None, str] = (None, None, UNKNOWN)
    for candidate in named:
        if not candidate.due_date_text:
            continue
        if candidate.due_date_status == INFERRED and not getattr(request, "infer_due_dates", False):
            # Keep the wording, drop the claim, exactly as the extraction stage would have.
            if best[0] is None:
                best = (candidate.due_date_text, None, UNKNOWN)
            continue
        if _rank(candidate.due_date_status) >= _rank(best[2]):
            resolved = candidate.due_date
            if resolved is None:
                resolved = _resolve_due_date(candidate.due_date_text, getattr(request, "meeting_date", None))
            best = (
                candidate.due_date_text,
                resolved,
                candidate.due_date_status if resolved is not None else UNKNOWN,
            )
    return best


#: Ways a model introduces a citation before naming one. Removed along with the ID it introduces,
#: because deleting only the ID leaves prose trailing off into "as recorded in."
_CITATION_LEAD: Final[str] = (
    r"(?:as\s+(?:recorded|noted|stated|decided|described|captured|set\s+out)\s+in"
    r"|according\s+to|based\s+on|per|see(?:\s+also)?|cf\.?|from|ref(?:erence)?)"
)


def _clean_parenthetical_fact_ids(text: str, fact_ids: Sequence[str]) -> str:
    """Hide exact internal citation markers without rewriting narrative claims.

    Models copy an ID into otherwise good prose in two shapes: bracketed, as ``(decision-001)``,
    and bare, as ``the beta ships Monday, as recorded in decision-001``. Both are presentation
    noise - the evidence is already on the block, and the UI shows it as a chip a reader can click
    - and both were reaching the brief, because only the bracketed form was ever removed.

    What is matched is still exact: the block's own identifiers, and nothing else. These are
    internal names of the form ``decision-001``, so a meeting cannot have said one, and removing
    one cannot delete a claim. Any citation lead-in immediately before the ID goes with it, so the
    sentence closes cleanly rather than ending mid-phrase. Arbitrary prose is never searched.
    """
    if not fact_ids:
        return text

    identifiers = "|".join(re.escape(identifier) for identifier in fact_ids)
    one = rf"(?:{identifiers})"
    group = rf"{one}(?:\s*(?:,|;|and)\s*{one})*"

    # Bracketed first: the whole parenthetical goes, punctuation and all.
    cleaned = re.sub(rf"\s*[\(\[]\s*{group}\s*[\)\]]", "", text, flags=re.IGNORECASE)

    # Then bare, with whatever introduced it and any punctuation holding it to the sentence.
    cleaned = re.sub(
        rf"\s*(?:[,;:]|\s-{{1,2}}|—)?\s*(?:{_CITATION_LEAD}\s+)?{group}",
        "",
        cleaned,
        flags=re.IGNORECASE,
    )

    # Tidy what the removal left behind: space before punctuation, a comma pushed against a full
    # stop, doubled spaces, and a sentence that now opens on its own punctuation.
    cleaned = re.sub(r"\s+([,.;:!?])", r"\1", cleaned)
    cleaned = re.sub(r",\s*([.;:!?])", r"\1", cleaned)
    cleaned = re.sub(r"[ \t]{2,}", " ", cleaned)
    cleaned = re.sub(r"^\s*[,;:.\-—]\s*", "", cleaned).strip()

    # Re-open the sentence in upper case when the removal took its first words with it.
    return cleaned[:1].upper() + cleaned[1:] if cleaned else cleaned


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
