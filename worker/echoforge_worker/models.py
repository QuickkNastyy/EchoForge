"""Typed request and transcript models, and the validation that turns host JSON into them.

Two rules shape this module.

The first is that a malformed request is refused precisely, with a code and a reason,
rather than tolerated. A worker that guesses at a missing field produces a transcript whose
timestamps mean something slightly different from what the host believes, and nothing
downstream can detect that.

The second is that speaker attribution is not data. It is derived from ``source_track`` and
cannot be supplied, overridden, or inferred: microphone content is You, system content is
Remote. There is no code path here that could produce anything else.
"""

from __future__ import annotations

import math
from dataclasses import dataclass, field
from typing import Any, Final

from .protocol import ErrorCode, Stage, WorkerFailure

MICROPHONE: Final[str] = "microphone"
SYSTEM: Final[str] = "system"
SOURCE_TRACKS: Final[tuple[str, ...]] = (MICROPHONE, SYSTEM)

SPEAKER_YOU_ID: Final[str] = "speaker-you"
SPEAKER_YOU_NAME: Final[str] = "You"
SPEAKER_REMOTE_ID: Final[str] = "speaker-remote"
SPEAKER_REMOTE_NAME: Final[str] = "Remote"

UNDETERMINED_LANGUAGE: Final[str] = "und"

TRANSCRIPT_SCHEMA_VERSION: Final[int] = 1


def speaker_for(source_track: str) -> tuple[str, str]:
    """The speaker a track is attributed to. The only place that decision is made."""
    if source_track == MICROPHONE:
        return SPEAKER_YOU_ID, SPEAKER_YOU_NAME
    if source_track == SYSTEM:
        return SPEAKER_REMOTE_ID, SPEAKER_REMOTE_NAME
    raise WorkerFailure(
        ErrorCode.INVALID_REQUEST, Stage.ACCEPTING, f"unknown source track {source_track!r}"
    )


def _invalid(detail: str) -> WorkerFailure:
    return WorkerFailure(ErrorCode.INVALID_REQUEST, Stage.ACCEPTING, detail)


def _require(obj: dict[str, Any], key: str, where: str) -> Any:
    if key not in obj:
        raise _invalid(f"{where} is missing required field {key!r}")
    return obj[key]


def _as_int(value: Any, where: str, minimum: int | None = None) -> int:
    if isinstance(value, bool) or not isinstance(value, int):
        raise _invalid(f"{where} must be an integer")
    if minimum is not None and value < minimum:
        raise _invalid(f"{where} must be at least {minimum}")
    return value


def _as_number(value: Any, where: str, minimum: float | None = None) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        raise _invalid(f"{where} must be a number")
    number = float(value)
    if not math.isfinite(number):
        raise _invalid(f"{where} must be finite")
    if minimum is not None and number < minimum:
        raise _invalid(f"{where} must be at least {minimum}")
    return number


def _as_str(value: Any, where: str, allow_empty: bool = False) -> str:
    if not isinstance(value, str):
        raise _invalid(f"{where} must be a string")
    if not allow_empty and not value.strip():
        raise _invalid(f"{where} must not be empty")
    return value


# --------------------------------------------------------------------------------------
# request
# --------------------------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class RequestEpoch:
    index: int
    start_seconds: float
    end_seconds: float

    @staticmethod
    def from_json(obj: Any) -> RequestEpoch:
        if not isinstance(obj, dict):
            raise _invalid("epoch must be an object")
        epoch = RequestEpoch(
            index=_as_int(_require(obj, "index", "epoch"), "epoch.index", minimum=1),
            start_seconds=_as_number(
                _require(obj, "start_seconds", "epoch"), "epoch.start_seconds", minimum=0.0
            ),
            end_seconds=_as_number(
                _require(obj, "end_seconds", "epoch"), "epoch.end_seconds", minimum=0.0
            ),
        )
        if epoch.end_seconds < epoch.start_seconds:
            raise _invalid(f"epoch {epoch.index} ends before it starts")
        return epoch


@dataclass(frozen=True, slots=True)
class RequestChunk:
    index: int
    epoch: int
    relative_path: str
    start_seconds: float
    end_seconds: float
    sample_rate: int
    channels: int
    frames: int
    sha256: str | None

    @staticmethod
    def from_json(obj: Any) -> RequestChunk:
        if not isinstance(obj, dict):
            raise _invalid("chunk must be an object")
        chunk = RequestChunk(
            index=_as_int(_require(obj, "index", "chunk"), "chunk.index", minimum=1),
            epoch=_as_int(_require(obj, "epoch", "chunk"), "chunk.epoch", minimum=1),
            relative_path=_as_str(_require(obj, "relative_path", "chunk"), "chunk.relative_path"),
            start_seconds=_as_number(
                _require(obj, "start_seconds", "chunk"), "chunk.start_seconds", minimum=0.0
            ),
            end_seconds=_as_number(
                _require(obj, "end_seconds", "chunk"), "chunk.end_seconds", minimum=0.0
            ),
            sample_rate=_as_int(_require(obj, "sample_rate", "chunk"), "chunk.sample_rate", minimum=1),
            channels=_as_int(_require(obj, "channels", "chunk"), "chunk.channels", minimum=1),
            frames=_as_int(_require(obj, "frames", "chunk"), "chunk.frames", minimum=0),
            sha256=obj.get("sha256") if isinstance(obj.get("sha256"), str) else None,
        )
        if chunk.end_seconds < chunk.start_seconds:
            raise _invalid(f"chunk {chunk.index} ends before it starts")
        return chunk


@dataclass(frozen=True, slots=True)
class RequestTrack:
    source_track: str
    chunks: tuple[RequestChunk, ...]

    @staticmethod
    def from_json(obj: Any) -> RequestTrack:
        if not isinstance(obj, dict):
            raise _invalid("track must be an object")
        source_track = _as_str(_require(obj, "source_track", "track"), "track.source_track")
        if source_track not in SOURCE_TRACKS:
            raise _invalid(f"unknown source track {source_track!r}")
        raw_chunks = _require(obj, "chunks", "track")
        if not isinstance(raw_chunks, list):
            raise _invalid("track.chunks must be an array")
        return RequestTrack(
            source_track=source_track,
            chunks=tuple(RequestChunk.from_json(c) for c in raw_chunks),
        )


@dataclass(frozen=True, slots=True)
class RequestDerivative:
    """The 16 kHz mono audio the host prepared for one track, and its timing map."""

    source_track: str
    relative_path: str
    timing_map_relative_path: str
    sample_rate: int
    channels: int
    total_frames: int
    sha256: str

    @staticmethod
    def from_json(obj: Any) -> RequestDerivative:
        if not isinstance(obj, dict):
            raise _invalid("derivative must be an object")
        source_track = _as_str(_require(obj, "source_track", "derivative"), "derivative.source_track")
        if source_track not in SOURCE_TRACKS:
            raise _invalid(f"unknown source track {source_track!r}")
        return RequestDerivative(
            source_track=source_track,
            relative_path=_as_str(_require(obj, "relative_path", "derivative"), "derivative.relative_path"),
            timing_map_relative_path=_as_str(
                _require(obj, "timing_map_relative_path", "derivative"), "derivative.timing_map_relative_path"
            ),
            sample_rate=_as_int(_require(obj, "sample_rate", "derivative"), "derivative.sample_rate", minimum=1),
            channels=_as_int(_require(obj, "channels", "derivative"), "derivative.channels", minimum=1),
            total_frames=_as_int(_require(obj, "total_frames", "derivative"), "derivative.total_frames", minimum=0),
            sha256=_as_str(_require(obj, "sha256", "derivative"), "derivative.sha256"),
        )


@dataclass(frozen=True, slots=True)
class RequestWindow:
    """One unit of transcription work, already placed on the session timeline by the host."""

    id: str
    source_track: str
    epoch: int
    ordinal: int
    start_frame: int
    end_frame: int
    session_start_seconds: float
    session_end_seconds: float
    overlap_before_seconds: float = 0.0
    overlap_after_seconds: float = 0.0
    input_fingerprint: str = ""

    @property
    def frames(self) -> int:
        return max(0, self.end_frame - self.start_frame)

    @staticmethod
    def from_json(obj: Any, ordinal: int) -> RequestWindow:
        if not isinstance(obj, dict):
            raise _invalid("window must be an object")
        source_track = _as_str(_require(obj, "source_track", "window"), "window.source_track")
        if source_track not in SOURCE_TRACKS:
            raise _invalid(f"unknown source track {source_track!r}")

        window = RequestWindow(
            id=_as_str(_require(obj, "id", "window"), "window.id"),
            source_track=source_track,
            epoch=_as_int(_require(obj, "epoch", "window"), "window.epoch", minimum=1),
            ordinal=ordinal,
            start_frame=_as_int(_require(obj, "start_frame", "window"), "window.start_frame", minimum=0),
            end_frame=_as_int(_require(obj, "end_frame", "window"), "window.end_frame", minimum=0),
            session_start_seconds=_as_number(
                _require(obj, "session_start_seconds", "window"), "window.session_start_seconds", minimum=0.0
            ),
            session_end_seconds=_as_number(
                _require(obj, "session_end_seconds", "window"), "window.session_end_seconds", minimum=0.0
            ),
            overlap_before_seconds=_as_number(obj.get("overlap_before_seconds", 0.0), "window.overlap_before_seconds", minimum=0.0),
            overlap_after_seconds=_as_number(obj.get("overlap_after_seconds", 0.0), "window.overlap_after_seconds", minimum=0.0),
            input_fingerprint=str(obj.get("input_fingerprint", "")),
        )

        if window.session_end_seconds < window.session_start_seconds:
            raise _invalid(f"window {window.id} ends before it starts")
        if window.end_frame < window.start_frame:
            raise _invalid(f"window {window.id} has a negative frame range")

        return window


@dataclass(frozen=True, slots=True)
class TimingSpan:
    """One stretch of a derivative: either real audio or an explicit gap."""

    kind: str
    derivative_frame: int
    frames: int
    epoch: int
    session_start_seconds: float
    session_end_seconds: float


@dataclass(frozen=True, slots=True)
class TimingMap:
    """Where each part of a derivative came from. Gaps are what a transcript may not enter."""

    sample_rate: int
    total_frames: int
    spans: tuple[TimingSpan, ...]


@dataclass(frozen=True, slots=True)
class RequestOptions:
    backend: str
    profile: str | None = None
    language: str | None = None
    segment_seconds: float | None = None
    test_mode: str | None = None
    test_delay_seconds: float | None = None

    #: Absolute directory of the verified CTranslate2 model. Never an alias, never a repo id:
    #: the registry resolved and verified this path, and the worker must not go looking.
    model_path: str | None = None

    compute_profile: str | None = None
    beam_size: int | None = None

    #: Conservative voice-activity filtering, using the Silero model inside the pinned wheel.
    vad_filter: bool = True

    word_timestamps: bool = True

    #: Seeded into the recogniser as an initial prompt: names, jargon, acronyms.
    initial_prompt: str | None = None
    glossary: tuple[str, ...] = ()

    @staticmethod
    def from_json(obj: Any) -> RequestOptions:
        if not isinstance(obj, dict):
            raise _invalid("options must be an object")
        segment_seconds = obj.get("segment_seconds")
        if segment_seconds is not None:
            segment_seconds = _as_number(segment_seconds, "options.segment_seconds")
            if segment_seconds <= 0:
                raise _invalid("options.segment_seconds must be greater than zero")
        delay = obj.get("test_delay_seconds")
        if delay is not None:
            delay = _as_number(delay, "options.test_delay_seconds", minimum=0.0)
        glossary = obj.get("glossary")
        if glossary is not None and not isinstance(glossary, list):
            raise _invalid("options.glossary must be an array of terms")

        beam = obj.get("beam_size")
        if beam is not None:
            beam = _as_int(beam, "options.beam_size", minimum=1)

        return RequestOptions(
            backend=_as_str(_require(obj, "backend", "options"), "options.backend"),
            profile=obj.get("profile") if isinstance(obj.get("profile"), str) else None,
            language=obj.get("language") if isinstance(obj.get("language"), str) else None,
            segment_seconds=segment_seconds,
            test_mode=obj.get("test_mode") if isinstance(obj.get("test_mode"), str) else None,
            test_delay_seconds=delay,
            model_path=obj.get("model_path") if isinstance(obj.get("model_path"), str) else None,
            compute_profile=obj.get("compute_profile") if isinstance(obj.get("compute_profile"), str) else None,
            beam_size=beam,
            vad_filter=bool(obj.get("vad_filter", True)),
            word_timestamps=bool(obj.get("word_timestamps", True)),
            initial_prompt=obj.get("initial_prompt") if isinstance(obj.get("initial_prompt"), str) else None,
            glossary=tuple(str(term) for term in (glossary or []) if str(term).strip()),
        )


@dataclass(frozen=True, slots=True)
class TranscriptionRequest:
    session_id: str
    transcript_revision: int
    created_at_utc: str
    session_root: str
    output_path: str
    duration_seconds: float
    epochs: tuple[RequestEpoch, ...]
    tracks: tuple[RequestTrack, ...]
    options: RequestOptions

    #: Present only for a production run. The placeholder backend ignores both and works from
    #: the source chunks, which is what keeps it usable before any audio has been prepared.
    derivatives: tuple[RequestDerivative, ...] = ()
    windows: tuple[RequestWindow, ...] = ()

    def derivative_for(self, source_track: str) -> RequestDerivative | None:
        for derivative in self.derivatives:
            if derivative.source_track == source_track:
                return derivative
        return None

    def windows_for(self, source_track: str) -> tuple[RequestWindow, ...]:
        return tuple(w for w in self.windows if w.source_track == source_track)

    @staticmethod
    def from_json(obj: Any) -> TranscriptionRequest:
        if not isinstance(obj, dict):
            raise _invalid("request must be an object")

        raw_epochs = _require(obj, "epochs", "request")
        if not isinstance(raw_epochs, list):
            raise _invalid("request.epochs must be an array")
        epochs = tuple(RequestEpoch.from_json(e) for e in raw_epochs)

        # Epochs are the bounds every transcript time is checked against, so an unordered
        # or overlapping set would make those checks meaningless rather than merely untidy.
        previous_end = 0.0
        previous_index = 0
        for epoch in epochs:
            if epoch.index <= previous_index:
                raise _invalid(f"epoch {epoch.index} is out of order")
            if epoch.start_seconds < previous_end - 1e-9:
                raise _invalid(f"epoch {epoch.index} starts before the previous epoch ends")
            previous_index = epoch.index
            previous_end = epoch.end_seconds

        raw_tracks = _require(obj, "tracks", "request")
        if not isinstance(raw_tracks, list):
            raise _invalid("request.tracks must be an array")
        tracks = tuple(RequestTrack.from_json(t) for t in raw_tracks)

        seen: set[str] = set()
        known_epochs = {e.index for e in epochs}
        for track in tracks:
            if track.source_track in seen:
                raise _invalid(f"track {track.source_track!r} appears more than once")
            seen.add(track.source_track)
            for chunk in track.chunks:
                if chunk.epoch not in known_epochs:
                    raise _invalid(
                        f"chunk {chunk.index} on {track.source_track} names epoch "
                        f"{chunk.epoch}, which the request does not describe"
                    )

        request = TranscriptionRequest(
            session_id=_as_str(_require(obj, "session_id", "request"), "request.session_id"),
            transcript_revision=_as_int(
                _require(obj, "transcript_revision", "request"),
                "request.transcript_revision",
                minimum=1,
            ),
            created_at_utc=_as_str(
                _require(obj, "created_at_utc", "request"), "request.created_at_utc"
            ),
            session_root=_as_str(_require(obj, "session_root", "request"), "request.session_root"),
            output_path=_as_str(_require(obj, "output_path", "request"), "request.output_path"),
            duration_seconds=_as_number(
                _require(obj, "duration_seconds", "request"),
                "request.duration_seconds",
                minimum=0.0,
            ),
            epochs=epochs,
            tracks=tracks,
            options=RequestOptions.from_json(_require(obj, "options", "request")),
            derivatives=tuple(
                RequestDerivative.from_json(d) for d in (obj.get("derivatives") or [])
            ),
            windows=tuple(
                RequestWindow.from_json(w, ordinal)
                for ordinal, w in enumerate(obj.get("windows") or [])
            ),
        )

        if epochs and request.duration_seconds < epochs[-1].end_seconds - 1e-9:
            raise _invalid("request.duration_seconds is shorter than the last epoch")

        return request

    def total_chunks(self) -> int:
        return sum(len(track.chunks) for track in self.tracks)

    def epoch(self, index: int) -> RequestEpoch:
        for epoch in self.epochs:
            if epoch.index == index:
                return epoch
        raise _invalid(f"epoch {index} is not described by this request")


# --------------------------------------------------------------------------------------
# transcript
# --------------------------------------------------------------------------------------


@dataclass(frozen=True, slots=True)
class Word:
    text: str
    start_seconds: float
    end_seconds: float
    probability: float | None = None

    def to_json(self) -> dict[str, Any]:
        return {
            "text": self.text,
            "start_seconds": self.start_seconds,
            "end_seconds": self.end_seconds,
            "probability": self.probability,
        }


@dataclass(slots=True)
class Segment:
    """One stretch of one speaker on one track.

    ``id`` is empty until the merge assigns it: IDs are positional within a revision, so a
    backend must not invent one, and ``sort_key`` exists to make that assignment a total
    order rather than a stable-sort accident.
    """

    source_track: str
    epoch: int
    start_seconds: float
    end_seconds: float
    text: str
    words: tuple[Word, ...] = ()
    language: str = UNDETERMINED_LANGUAGE
    confidence: float | None = None
    chunk_index: int = 0
    ordinal: int = 0
    id: str = ""
    overlaps_segment_ids: list[str] = field(default_factory=list)

    def sort_key(self) -> tuple[float, float, int, int, int]:
        track_rank = 0 if self.source_track == MICROPHONE else 1
        return (self.start_seconds, self.end_seconds, track_rank, self.chunk_index, self.ordinal)

    def to_json(self) -> dict[str, Any]:
        speaker_id, speaker_name = speaker_for(self.source_track)
        return {
            "id": self.id,
            "epoch": self.epoch,
            "start_seconds": self.start_seconds,
            "end_seconds": self.end_seconds,
            "speaker_id": speaker_id,
            "speaker_name": speaker_name,
            "source_track": self.source_track,
            "text": self.text,
            "confidence": self.confidence,
            "language": self.language,
            "words": [w.to_json() for w in self.words],
            "overlaps_segment_ids": list(self.overlaps_segment_ids),
        }


@dataclass(frozen=True, slots=True)
class TranscriptModel:
    runtime: str
    backend: str
    model_id: str
    revision: str
    compute_type: str
    recognizes_speech: bool
    worker_version: str

    def to_json(self) -> dict[str, Any]:
        return {
            "runtime": self.runtime,
            "backend": self.backend,
            "model_id": self.model_id,
            "revision": self.revision,
            "compute_type": self.compute_type,
            "recognizes_speech": self.recognizes_speech,
            "worker_version": self.worker_version,
        }


@dataclass(frozen=True, slots=True)
class Transcript:
    session_id: str
    transcript_revision: int
    created_at_utc: str
    source_manifest_sha256: str | None
    duration_seconds: float
    model: TranscriptModel
    epochs: tuple[RequestEpoch, ...]
    languages: tuple[tuple[str, str, float | None], ...]
    segments: tuple[Segment, ...]

    def to_json(self) -> dict[str, Any]:
        tracks_present = [language[0] for language in self.languages]
        speakers = []
        for track in SOURCE_TRACKS:
            if track in tracks_present:
                speaker_id, speaker_name = speaker_for(track)
                speakers.append({"id": speaker_id, "name": speaker_name, "source_track": track})

        return {
            "schema_version": TRANSCRIPT_SCHEMA_VERSION,
            "session_id": self.session_id,
            "transcript_revision": self.transcript_revision,
            "created_at_utc": self.created_at_utc,
            "source_manifest_sha256": self.source_manifest_sha256,
            "duration_seconds": self.duration_seconds,
            "model": self.model.to_json(),
            "epochs": [
                {
                    "index": e.index,
                    "start_seconds": e.start_seconds,
                    "end_seconds": e.end_seconds,
                }
                for e in self.epochs
            ],
            "speakers": speakers,
            "languages": [
                {"source_track": track, "code": code, "probability": probability}
                for track, code, probability in self.languages
            ],
            "segments": [s.to_json() for s in self.segments],
        }
