"""Window execution: rebasing window-local times onto the session, and removing the overlap.

A recogniser is handed one window of audio at a time and answers in times relative to that
window. Three things then have to be true before those times can go in a transcript, and each
of them is a way the transcript would otherwise be quietly wrong.

They have to be **rebased** onto the session timeline, using the window's own session start
rather than a running total, so nothing drifts.

They have to be **held inside real audio**. A window sits on a derivative that contains
explicit silence wherever the recording was not running. A recogniser given ten minutes that
include a five-second pause will occasionally emit something across it; a segment that lands
in a gap names a moment when nothing was captured, so it is dropped rather than kept.

And the **overlap has to be removed**. Adjacent windows deliberately share five seconds so a
sentence spoken across the boundary is heard whole by at least one of them. Both of them
usually hear it, and without de-duplication every seam would repeat a line.
"""

from __future__ import annotations

import re
import unicodedata
from dataclasses import dataclass
from typing import Any, Final, Iterable, Sequence

from .models import RequestWindow, Segment, TimingMap, TimingSpan, Word

#: Times are rounded so two runs on two machines produce identical bytes.
TIME_PRECISION: Final[int] = 6

#: How far apart two candidates may be and still be the same speech heard twice. Generous
#: enough for two windows to disagree slightly about where a word began, far tighter than the
#: gap between two genuine repetitions of a phrase in conversation.
DUPLICATE_TOLERANCE_SECONDS: Final[float] = 1.5

_PUNCTUATION: Final[re.Pattern[str]] = re.compile(r"[^\w\s]", re.UNICODE)
_WHITESPACE: Final[re.Pattern[str]] = re.compile(r"\s+")


def normalize_text(text: str) -> str:
    """Fold a phrase to what it would sound like, for comparison only.

    Two windows hearing the same sentence routinely disagree about a comma, a capital, or a
    trailing full stop. Comparing raw text would leave those duplicates in; comparing this
    catches them. It is never written to the transcript - the original text is preserved.
    """
    folded = unicodedata.normalize("NFKC", text).casefold()
    folded = _PUNCTUATION.sub(" ", folded)
    return _WHITESPACE.sub(" ", folded).strip()


def _round(value: float) -> float:
    return round(value, TIME_PRECISION)


@dataclass(frozen=True, slots=True)
class WindowSegment:
    """A recogniser's answer for one window, before it has been placed on the session."""

    start_seconds: float
    end_seconds: float
    text: str
    words: tuple[Word, ...] = ()
    language: str | None = None
    confidence: float | None = None


def rebase(
    window: RequestWindow,
    results: Sequence[WindowSegment],
    timing: TimingMap | None,
    session_duration: float,
) -> list[Segment]:
    """Place one window's results on the session timeline.

    Everything is clamped to the window that produced it. A recogniser that ran slightly past
    the audio it was given would otherwise claim time belonging to the next window, and the
    de-duplicator would have no way to tell that apart from genuine overlap.
    """
    placed: list[Segment] = []

    for ordinal, result in enumerate(results):
        start = window.session_start_seconds + max(0.0, result.start_seconds)
        end = window.session_start_seconds + max(0.0, result.end_seconds)

        start = min(max(start, window.session_start_seconds), window.session_end_seconds)
        end = min(max(end, start), window.session_end_seconds)
        end = min(end, session_duration)
        start = min(start, end)

        if end <= start:
            continue

        # Nothing was recorded in a gap, so nothing can be transcribed there.
        if timing is not None:
            trimmed = _trim_to_source(timing, start, end)
            if trimmed is None:
                continue
            start, end = trimmed

        text = result.text.strip()
        if not text:
            continue

        placed.append(
            Segment(
                source_track=window.source_track,
                epoch=window.epoch,
                start_seconds=_round(start),
                end_seconds=_round(end),
                text=text,
                words=tuple(_rebase_words(window, result, start, end)),
                language=result.language or "und",
                confidence=result.confidence,
                chunk_index=window.ordinal,
                ordinal=ordinal,
            )
        )

    return placed


def _rebase_words(
    window: RequestWindow,
    result: WindowSegment,
    segment_start: float,
    segment_end: float,
) -> Iterable[Word]:
    """Move word times with their segment, and keep every one inside it.

    A word outside its segment fails validation, and a rounding artefact is a poor reason to
    fail a whole transcript - so the ends are pinned rather than trusted.
    """
    for word in result.words:
        start = min(max(window.session_start_seconds + word.start_seconds, segment_start), segment_end)
        end = min(max(window.session_start_seconds + word.end_seconds, start), segment_end)

        text = word.text.strip()
        if not text:
            continue

        yield Word(
            text=text,
            start_seconds=_round(start),
            end_seconds=_round(end),
            probability=word.probability,
        )


def _trim_to_source(timing: TimingMap, start: float, end: float) -> tuple[float, float] | None:
    """Clip a span to the parts of the timeline that hold real audio.

    Returns None when it lies wholly inside a gap. A segment straddling the edge of one is
    trimmed back rather than dropped: the recogniser did hear something, just not for as long
    as it claimed.
    """
    spans = [s for s in timing.spans if s.kind == "source" and s.session_end_seconds > start and s.session_start_seconds < end]
    if not spans:
        return None

    clipped_start = max(start, min(s.session_start_seconds for s in spans))
    clipped_end = min(end, max(s.session_end_seconds for s in spans))

    if clipped_end <= clipped_start:
        return None

    return clipped_start, clipped_end


def deduplicate(segments: Sequence[Segment], windows: Sequence[RequestWindow]) -> list[Segment]:
    """Remove speech heard twice because two windows shared it.

    Conservative by construction, in three ways that matter.

    It only ever looks **inside a known overlap**. A phrase genuinely repeated later in a
    meeting - "yes", "agreed", a name said twice - is outside every overlap region and is
    never touched.

    It requires the **normalised text to match exactly**. Two windows disagreeing about
    punctuation are the same sentence; two windows producing different words are not, and
    keeping both is the safer error.

    And it keeps the **better-supported result** when duplicates differ: the one with word
    timestamps, then the longer one, then the earlier. A window that heard the whole sentence
    is worth more than one that caught its tail.
    """
    if not segments:
        return []

    overlaps = _overlap_regions(windows)
    if not overlaps:
        return sorted(segments, key=lambda s: s.sort_key())

    ordered = sorted(segments, key=lambda s: s.sort_key())
    kept: list[Segment] = []

    for candidate in ordered:
        region = _region_for(overlaps, candidate)
        if region is None:
            kept.append(candidate)
            continue

        rival = _find_duplicate(kept, candidate)
        if rival is None:
            kept.append(candidate)
            continue

        if _score(candidate) > _score(rival):
            kept[kept.index(rival)] = candidate

    return sorted(kept, key=lambda s: s.sort_key())


def _overlap_regions(windows: Sequence[RequestWindow]) -> dict[tuple[str, int], list[tuple[float, float]]]:
    """The stretches of the timeline two windows both heard, per track and epoch."""
    regions: dict[tuple[str, int], list[tuple[float, float]]] = {}

    by_track: dict[tuple[str, int], list[RequestWindow]] = {}
    for window in windows:
        by_track.setdefault((window.source_track, window.epoch), []).append(window)

    for key, group in by_track.items():
        group.sort(key=lambda w: w.session_start_seconds)
        spans: list[tuple[float, float]] = []

        for previous, current in zip(group, group[1:]):
            start = current.session_start_seconds
            end = min(previous.session_end_seconds, current.session_end_seconds)
            if end > start:
                spans.append((start, end))

        if spans:
            regions[key] = spans

    return regions


def _region_for(
    regions: dict[tuple[str, int], list[tuple[float, float]]], segment: Segment
) -> tuple[float, float] | None:
    for start, end in regions.get((segment.source_track, segment.epoch), []):
        # Any part of the segment inside the shared audio makes it a candidate.
        if segment.start_seconds < end and segment.end_seconds > start:
            return start, end

    return None


def _find_duplicate(kept: Sequence[Segment], candidate: Segment) -> Segment | None:
    needle = normalize_text(candidate.text)
    if not needle:
        return None

    for existing in kept:
        if existing.source_track != candidate.source_track or existing.epoch != candidate.epoch:
            continue

        if abs(existing.start_seconds - candidate.start_seconds) > DUPLICATE_TOLERANCE_SECONDS:
            continue

        if normalize_text(existing.text) == needle:
            return existing

    return None


def _score(segment: Segment) -> tuple[int, float, float]:
    """How well supported a result is. Word timings first, then length, then earliness."""
    return (1 if segment.words else 0, segment.end_seconds - segment.start_seconds, -segment.start_seconds)


def load_timing_map(payload: Any) -> TimingMap | None:
    """Read a timing map written by the host, tolerating one that is simply absent."""
    if not isinstance(payload, dict):
        return None

    spans = []
    for raw in payload.get("spans", []):
        if not isinstance(raw, dict):
            continue
        spans.append(
            TimingSpan(
                kind=str(raw.get("kind", "source")),
                derivative_frame=int(raw.get("derivative_frame", 0)),
                frames=int(raw.get("frames", 0)),
                epoch=int(raw.get("epoch", 1)),
                session_start_seconds=float(raw.get("session_start_seconds", 0.0)),
                session_end_seconds=float(raw.get("session_end_seconds", 0.0)),
            )
        )

    return TimingMap(
        sample_rate=int(payload.get("sample_rate", 16000)),
        total_frames=int(payload.get("total_frames", 0)),
        spans=tuple(spans),
    )
