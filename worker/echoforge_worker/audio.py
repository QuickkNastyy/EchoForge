"""Read-only access to EchoForge's immutable source chunks.

Everything here opens files in binary read mode and nothing here writes. Source WAVs and
their sidecar metadata are canonical: a processing stage that repaired one in place would
destroy the only authoritative copy of what was actually captured.

The energy estimate below decimates rather than reading every sample. A 60-second 48 kHz
stereo chunk is 5.76 million samples, and summing those in pure Python for every window
would make the placeholder backend slower than the real one it stands in for. The stride is
derived from the window length, so it is deterministic: the same audio always yields the
same estimate.
"""

from __future__ import annotations

import wave
from dataclasses import dataclass
from pathlib import Path
from typing import Final

from .protocol import ErrorCode, Stage, WorkerFailure

#: Samples examined per energy estimate. Enough to separate silence from content without
#: touching every frame of a long chunk.
ENERGY_SAMPLE_BUDGET: Final[int] = 4096

#: Below this RMS, in PCM16 units, a window is treated as carrying nothing. Digital silence
#: is zero; this leaves room for a dithered or very quiet floor.
SILENCE_RMS_THRESHOLD: Final[float] = 64.0


@dataclass(frozen=True, slots=True)
class PcmAudio:
    """Interleaved little-endian PCM16, exactly as it sits in the file."""

    sample_rate: int
    channels: int
    frames: int
    data: bytes

    @property
    def bytes_per_frame(self) -> int:
        return self.channels * 2

    @property
    def duration_seconds(self) -> float:
        return self.frames / self.sample_rate if self.sample_rate else 0.0


def resolve_inside(session_root: str, relative_path: str) -> Path:
    """Resolve a chunk path and prove it stays inside the session directory.

    A relative path that escapes its session is refused rather than followed. The host
    builds these paths, but a worker that trusts them completely would turn a bug in the
    host into a read of an arbitrary file.
    """
    root = Path(session_root).resolve()
    candidate = (root / relative_path.replace("\\", "/")).resolve()
    try:
        candidate.relative_to(root)
    except ValueError as error:
        raise WorkerFailure(
            ErrorCode.INPUT_INVALID,
            Stage.READING_AUDIO,
            f"chunk path escapes the session root: {relative_path!r}",
        ) from error
    return candidate


def read_pcm16(path: Path) -> PcmAudio:
    """Read a finalized PCM16 chunk. Never opens the file for writing."""
    if not path.is_file():
        raise WorkerFailure(
            ErrorCode.INPUT_MISSING,
            Stage.READING_AUDIO,
            f"source chunk is missing: {path.name}",
        )

    try:
        with wave.open(str(path), "rb") as handle:
            channels = handle.getnchannels()
            sample_width = handle.getsampwidth()
            sample_rate = handle.getframerate()
            frames = handle.getnframes()
            data = handle.readframes(frames)
    except (wave.Error, EOFError, OSError) as error:
        raise WorkerFailure(
            ErrorCode.AUDIO_UNREADABLE,
            Stage.READING_AUDIO,
            f"{path.name} could not be read as RIFF/WAVE: {error}",
        ) from error

    if sample_width != 2:
        raise WorkerFailure(
            ErrorCode.INPUT_INVALID,
            Stage.READING_AUDIO,
            f"{path.name} is {sample_width * 8}-bit; EchoForge source chunks are PCM16",
        )
    if channels < 1 or sample_rate < 1:
        raise WorkerFailure(
            ErrorCode.INPUT_INVALID,
            Stage.READING_AUDIO,
            f"{path.name} declares an unusable format",
        )

    return PcmAudio(sample_rate=sample_rate, channels=channels, frames=frames, data=data)


def check_matches_metadata(audio: PcmAudio, sample_rate: int, channels: int, frames: int) -> str | None:
    """Compare a chunk against what the host said it was.

    Returns a description of the disagreement, or ``None``. It is not fatal here: the audio
    is the authority, so the worker proceeds on what it actually read and reports the
    discrepancy as a warning rather than substituting the metadata's version of events.
    """
    problems = []
    if audio.sample_rate != sample_rate:
        problems.append(f"sample rate {audio.sample_rate} but metadata says {sample_rate}")
    if audio.channels != channels:
        problems.append(f"{audio.channels} channels but metadata says {channels}")
    if audio.frames != frames:
        problems.append(f"{audio.frames} frames but metadata says {frames}")
    return "; ".join(problems) if problems else None


def window_rms(audio: PcmAudio, first_frame: int, frame_count: int) -> float:
    """Root-mean-square amplitude over a frame range, estimated by decimation."""
    if frame_count <= 0 or audio.channels <= 0:
        return 0.0

    samples = memoryview(audio.data).cast("h")
    first_sample = first_frame * audio.channels
    last_sample = min(len(samples), (first_frame + frame_count) * audio.channels)
    if last_sample <= first_sample:
        return 0.0

    span = last_sample - first_sample
    stride = max(1, span // ENERGY_SAMPLE_BUDGET)

    total = 0
    counted = 0
    for index in range(first_sample, last_sample, stride):
        value = samples[index]
        total += value * value
        counted += 1

    if counted == 0:
        return 0.0
    return (total / counted) ** 0.5


def window_bytes(audio: PcmAudio, first_frame: int, frame_count: int) -> bytes:
    """The raw bytes of a frame range, for hashing. Never mutated."""
    start = first_frame * audio.bytes_per_frame
    end = min(len(audio.data), (first_frame + frame_count) * audio.bytes_per_frame)
    if end <= start:
        return b""
    return audio.data[start:end]
