"""Transcription: the backend seam, the deterministic placeholder backend, and the merge.

The backend interface takes a whole track rather than a chunk at a time. That is deliberate:
60-second source chunk boundaries are not speech boundaries, and the production backend will
form its own ten-minute windows with overlap across them. An interface shaped around single
chunks would have to be rewritten to allow that, and the protocol would move with it.

The placeholder backend registered here performs **no speech recognition of any kind**. It
reads the real audio, finds where energy exists, and emits deterministic filler text derived
from a hash of those bytes. It exists so the protocol, the supervisor, the transcript
contract, and the timing arithmetic can be proven correct before any model is downloaded.
Every transcript it writes carries ``recognizes_speech: false``, and every segment it emits
begins with a ``[mock]`` marker, so its output cannot be mistaken for a record of anything
that was said.
"""

from __future__ import annotations

import hashlib
from abc import ABC, abstractmethod
from typing import Callable, Final, Sequence

from .audio import (
    SILENCE_RMS_THRESHOLD,
    PcmAudio,
    check_matches_metadata,
    read_pcm16,
    resolve_inside,
    window_bytes,
    window_rms,
)
from .models import (
    MICROPHONE,
    SOURCE_TRACKS,
    SYSTEM,
    UNDETERMINED_LANGUAGE,
    RequestChunk,
    RequestOptions,
    Segment,
    Transcript,
    TranscriptionRequest,
    TranscriptModel,
    Word,
)
from .protocol import Cancelled, ErrorCode, Stage, WorkerFailure

#: Placeholder segmentation window. Not a speech-boundary decision; just a fixed grid.
DEFAULT_SEGMENT_SECONDS: Final[float] = 3.0

#: A trailing window shorter than this is folded away rather than emitted as a sliver.
MINIMUM_SEGMENT_SECONDS: Final[float] = 0.25

#: Times are rounded so that two runs on two machines produce identical bytes.
TIME_PRECISION: Final[int] = 6

MOCK_MARKER: Final[str] = "[mock]"

_LEXICON: Final[tuple[str, ...]] = (
    "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel",
    "india", "juliet", "kilo", "lima", "mike", "november", "oscar", "papa",
    "quebec", "romeo", "sierra", "tango", "uniform", "victor", "whiskey", "xray",
    "yankee", "zulu", "one", "two", "three", "four", "five", "six",
)


class BackendContext:
    """What a backend is allowed to do to the outside world.

    Loading audio, reporting progress, raising warnings, and checking for cancellation all
    go through here, so a backend never touches stdout and never decides on its own when a
    job should stop.
    """

    def __init__(
        self,
        session_root: str,
        cancelled: Callable[[], bool],
        on_chunk_completed: Callable[[str], None],
        on_warning: Callable[[str, str], None],
    ) -> None:
        self._session_root = session_root
        self._cancelled = cancelled
        self._on_chunk_completed = on_chunk_completed
        self._on_warning = on_warning

    def check_cancelled(self) -> None:
        """Raise at a safe boundary if the host has asked to stop."""
        if self._cancelled():
            raise Cancelled()

    def load(self, chunk: RequestChunk) -> PcmAudio:
        audio = read_pcm16(resolve_inside(self._session_root, chunk.relative_path))
        mismatch = check_matches_metadata(audio, chunk.sample_rate, chunk.channels, chunk.frames)
        if mismatch is not None:
            # The audio is the authority. Proceed on what was actually read and say so,
            # rather than trusting a description that the file itself contradicts.
            self._on_warning("chunk_metadata_mismatch", f"chunk {chunk.index}: {mismatch}")
        return audio

    def chunk_completed(self, source_track: str) -> None:
        self._on_chunk_completed(source_track)

    def warn(self, code: str, detail: str) -> None:
        self._on_warning(code, detail)


class TranscriptionBackend(ABC):
    """One way of turning a track's audio into segments.

    Implementations must be deterministic for a given input, must not write to any source
    file, and must not attribute speakers: attribution follows from the track and is applied
    by the merge, not by the recogniser.
    """

    name: str = ""
    recognizes_speech: bool = False

    @abstractmethod
    def describe(self, options: RequestOptions) -> TranscriptModel:
        """What to record in the transcript about what produced it."""

    @abstractmethod
    def transcribe_track(
        self,
        source_track: str,
        chunks: Sequence[RequestChunk],
        options: RequestOptions,
        context: BackendContext,
    ) -> list[Segment]:
        """Segments for one track, in any order. The merge sorts and identifies them."""


class MockBackend(TranscriptionBackend):
    """Deterministic placeholder. Reads real audio; recognises nothing.

    Windows the audio on a fixed grid, keeps the windows that carry energy, and derives
    filler words from a hash of those exact bytes. Identical audio therefore produces an
    identical transcript, and silence produces no segments at all — which is what makes the
    empty-input and determinism cases testable without a model.
    """

    name = "mock"
    recognizes_speech = False

    def describe(self, options: RequestOptions) -> TranscriptModel:
        from .protocol import WORKER_VERSION

        return TranscriptModel(
            runtime="echoforge-mock",
            backend=self.name,
            model_id=options.profile or "mock-v1",
            revision="mock-v1",
            compute_type="none",
            recognizes_speech=False,
            worker_version=WORKER_VERSION,
        )

    def transcribe_track(
        self,
        source_track: str,
        chunks: Sequence[RequestChunk],
        options: RequestOptions,
        context: BackendContext,
    ) -> list[Segment]:
        window_seconds = options.segment_seconds or DEFAULT_SEGMENT_SECONDS
        segments: list[Segment] = []

        for chunk in chunks:
            context.check_cancelled()
            audio = context.load(chunk)
            segments.extend(self._chunk_segments(source_track, chunk, audio, window_seconds))
            context.chunk_completed(source_track)

        return segments

    def _chunk_segments(
        self,
        source_track: str,
        chunk: RequestChunk,
        audio: PcmAudio,
        window_seconds: float,
    ) -> list[Segment]:
        if audio.frames <= 0:
            return []

        frames_per_window = max(1, int(round(window_seconds * audio.sample_rate)))
        minimum_frames = max(1, int(round(MINIMUM_SEGMENT_SECONDS * audio.sample_rate)))

        # Chunk timing comes from the request, which already placed this chunk on the
        # session timeline. The audio decides how long it is; the file is the authority.
        chunk_end = chunk.start_seconds + (audio.frames / audio.sample_rate)

        segments: list[Segment] = []
        ordinal = 0
        first_frame = 0
        while first_frame < audio.frames:
            frame_count = min(frames_per_window, audio.frames - first_frame)
            if frame_count < minimum_frames and segments:
                break

            if window_rms(audio, first_frame, frame_count) >= SILENCE_RMS_THRESHOLD:
                start = chunk.start_seconds + (first_frame / audio.sample_rate)
                end = min(chunk_end, start + (frame_count / audio.sample_rate))
                segments.append(
                    self._segment(
                        source_track=source_track,
                        chunk=chunk,
                        payload=window_bytes(audio, first_frame, frame_count),
                        start=_round(start),
                        end=_round(end),
                        ordinal=ordinal,
                    )
                )
            ordinal += 1
            first_frame += frames_per_window

        return segments

    def _segment(
        self,
        source_track: str,
        chunk: RequestChunk,
        payload: bytes,
        start: float,
        end: float,
        ordinal: int,
    ) -> Segment:
        digest = hashlib.sha256(source_track.encode("utf-8") + b"|" + payload).digest()
        word_count = 2 + (digest[0] % 5)
        texts = [MOCK_MARKER] + [_LEXICON[digest[1 + i] % len(_LEXICON)] for i in range(word_count)]

        words = _spread(texts, start, end)
        return Segment(
            source_track=source_track,
            epoch=chunk.epoch,
            start_seconds=start,
            end_seconds=end,
            text=" ".join(texts),
            words=tuple(words),
            language=UNDETERMINED_LANGUAGE,
            # No calibrated score exists here, and inventing one would be worse than none.
            confidence=None,
            chunk_index=chunk.index,
            ordinal=ordinal,
        )


_BACKENDS: Final[dict[str, type[TranscriptionBackend]]] = {MockBackend.name: MockBackend}


def available_backends() -> list[str]:
    return sorted(_BACKENDS)


def resolve_backend(name: str) -> TranscriptionBackend:
    factory = _BACKENDS.get(name)
    if factory is None:
        raise WorkerFailure(
            ErrorCode.BACKEND_UNAVAILABLE,
            Stage.PREPARING,
            f"backend {name!r} is not available in this build; have {available_backends()}",
        )
    return factory()


def _round(value: float) -> float:
    return round(value, TIME_PRECISION)


def _spread(texts: Sequence[str], start: float, end: float) -> list[Word]:
    """Lay words out evenly across a segment, staying inside it.

    The ends are pinned rather than computed so that rounding can never push a word past
    the segment that contains it — a containment failure would be a validation error, and
    an arithmetic artefact is a poor reason to fail one.
    """
    count = len(texts)
    if count == 0:
        return []

    span = max(0.0, end - start)
    step = span / count
    words: list[Word] = []
    for index, text in enumerate(texts):
        word_start = start if index == 0 else _round(start + (index * step))
        word_end = end if index == count - 1 else _round(start + ((index + 1) * step))
        if word_end < word_start:
            word_end = word_start
        words.append(Word(text=text, start_seconds=word_start, end_seconds=word_end, probability=None))
    return words


def source_manifest_sha256(request: TranscriptionRequest) -> str:
    """The identity of the audio this transcript was produced from.

    The host computes the same digest from the same request, byte for byte. The canonical
    form is one line per chunk in request order:
    ``track|epoch|index|relative_path|frames|sha256``, UTF-8, newline-terminated.
    """
    lines = []
    for track in request.tracks:
        for chunk in track.chunks:
            lines.append(
                f"{track.source_track}|{chunk.epoch}|{chunk.index}|"
                f"{chunk.relative_path}|{chunk.frames}|{chunk.sha256 or ''}\n"
            )
    return hashlib.sha256("".join(lines).encode("utf-8")).hexdigest()


def build_transcript(
    request: TranscriptionRequest,
    backend: TranscriptionBackend,
    context: BackendContext,
    on_stage: Callable[[str], None],
) -> Transcript:
    """Run the backend over both tracks and merge the result onto one timeline."""
    collected: list[Segment] = []

    for track in request.tracks:
        context.check_cancelled()
        on_stage(
            Stage.TRANSCRIBING_MICROPHONE
            if track.source_track == MICROPHONE
            else Stage.TRANSCRIBING_SYSTEM
        )
        collected.extend(
            backend.transcribe_track(track.source_track, track.chunks, request.options, context)
        )

    context.check_cancelled()
    on_stage(Stage.MERGING)

    segments = _merge(request, collected)

    languages = tuple(
        (track.source_track, request.options.language or UNDETERMINED_LANGUAGE, None)
        for track in sorted(request.tracks, key=lambda t: SOURCE_TRACKS.index(t.source_track))
    )

    return Transcript(
        session_id=request.session_id,
        transcript_revision=request.transcript_revision,
        created_at_utc=request.created_at_utc,
        source_manifest_sha256=source_manifest_sha256(request),
        duration_seconds=request.duration_seconds,
        model=backend.describe(request.options),
        epochs=request.epochs,
        languages=languages,
        segments=segments,
    )


def _merge(request: TranscriptionRequest, segments: list[Segment]) -> tuple[Segment, ...]:
    """Order, identify, clamp, and cross-link.

    Ordering is a total order, so identical input yields an identical file rather than a
    file that merely happens to sort the same way today. IDs are assigned only after that
    order is fixed, because an ID that depended on backend iteration order would not be
    stable across a change of backend.
    """
    ordered = sorted(segments, key=lambda s: s.sort_key())

    for position, segment in enumerate(ordered, start=1):
        segment.id = f"segment-{position:06d}"
        _clamp_to_epoch(request, segment)

    _link_overlaps(ordered)
    return tuple(ordered)


def _clamp_to_epoch(request: TranscriptionRequest, segment: Segment) -> None:
    """Hold a segment and its words inside the epoch that produced them.

    A backend that ran slightly past the end of the audio would otherwise place a segment
    where nothing was captured. Nothing could seek to it, and nothing could cite it.
    """
    epoch = request.epoch(segment.epoch)
    start = _round(min(max(segment.start_seconds, epoch.start_seconds), epoch.end_seconds))
    end = _round(min(max(segment.end_seconds, start), epoch.end_seconds))

    if start == segment.start_seconds and end == segment.end_seconds:
        return

    segment.start_seconds = start
    segment.end_seconds = end
    segment.words = tuple(
        Word(
            text=word.text,
            start_seconds=min(max(word.start_seconds, start), end),
            end_seconds=min(max(word.end_seconds, start), end),
            probability=word.probability,
        )
        for word in segment.words
    )


def _link_overlaps(ordered: Sequence[Segment]) -> None:
    """Record cross-track time overlaps.

    Overlap is information, never a de-duplication decision. Headset sidetone putting You
    on the system track and a genuine interruption look exactly the same from here, so the
    transcript records that they coincide and leaves the judgement to a human.
    """
    by_track: dict[str, list[Segment]] = {MICROPHONE: [], SYSTEM: []}
    for segment in ordered:
        by_track[segment.source_track].append(segment)

    for segment in ordered:
        other_track = SYSTEM if segment.source_track == MICROPHONE else MICROPHONE
        matches = [
            other.id
            for other in by_track[other_track]
            if other.start_seconds < segment.end_seconds and segment.start_seconds < other.end_seconds
        ]
        segment.overlaps_segment_ids = sorted(matches)
