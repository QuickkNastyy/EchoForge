"""Per-window results on disk, so an interrupted job resumes instead of starting again.

A ten-minute window of a three-hour meeting is minutes of GPU time. Losing all of it because
window seventeen failed would make every transient fault cost the whole job, so each window's
result is written the moment it succeeds and read back on the next run.

A checkpoint is only reused when the window's **full input fingerprint** matches. The
fingerprint covers the audio, the derivative, the profile, the boundaries, and the planning
rules, so a checkpoint cannot survive a change to any of them. Matching on the window ID alone
would eventually hand a caller a result produced from different audio.
"""

from __future__ import annotations

import json
import os
import tempfile
from pathlib import Path
from typing import Sequence

from .models import RequestWindow, Word
from .windows import WindowSegment

SCHEMA_VERSION = 1


def directory_for(session_root: str, planning_version: str) -> Path:
    return Path(session_root) / "derived" / "windows" / planning_version / "results"


def load(directory: Path, window: RequestWindow) -> tuple[WindowSegment, ...] | None:
    """A previous run's result for this exact window, or None."""
    path = directory / f"{window.id}.json"
    if not path.is_file():
        return None

    try:
        payload = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        # An unreadable checkpoint costs a re-run of one window, never correctness.
        return None

    if payload.get("schema_version") != SCHEMA_VERSION:
        return None

    # The fingerprint is the whole point. A window ID that matched while the audio behind it
    # had changed would return a result describing something else entirely.
    if payload.get("input_fingerprint") != window.input_fingerprint:
        return None

    segments = []
    for raw in payload.get("segments", []):
        segments.append(
            WindowSegment(
                start_seconds=float(raw["start_seconds"]),
                end_seconds=float(raw["end_seconds"]),
                text=str(raw["text"]),
                words=tuple(
                    Word(
                        text=str(w["text"]),
                        start_seconds=float(w["start_seconds"]),
                        end_seconds=float(w["end_seconds"]),
                        probability=w.get("probability"),
                    )
                    for w in raw.get("words", [])
                ),
                language=raw.get("language"),
                confidence=raw.get("confidence"),
            )
        )

    return tuple(segments)


def save(
    directory: Path,
    window: RequestWindow,
    segments: Sequence[WindowSegment],
    language: str | None,
) -> None:
    """Record a finished window, atomically.

    Written to a temporary neighbour and moved into place, so a crash halfway through cannot
    leave a truncated file that the next run would read as a complete result.
    """
    directory.mkdir(parents=True, exist_ok=True)

    payload = {
        "schema_version": SCHEMA_VERSION,
        "window_id": window.id,
        "input_fingerprint": window.input_fingerprint,
        "source_track": window.source_track,
        "epoch": window.epoch,
        "language": language,
        "segments": [
            {
                "start_seconds": segment.start_seconds,
                "end_seconds": segment.end_seconds,
                "text": segment.text,
                "language": segment.language,
                "confidence": segment.confidence,
                "words": [
                    {
                        "text": word.text,
                        "start_seconds": word.start_seconds,
                        "end_seconds": word.end_seconds,
                        "probability": word.probability,
                    }
                    for word in segment.words
                ],
            }
            for segment in segments
        ],
    }

    destination = directory / f"{window.id}.json"
    handle, staging = tempfile.mkstemp(dir=str(directory), suffix=".partial")

    try:
        with os.fdopen(handle, "w", encoding="utf-8") as stream:
            json.dump(payload, stream, ensure_ascii=False)
            stream.flush()
            os.fsync(stream.fileno())

        os.replace(staging, destination)
    except BaseException:
        try:
            os.unlink(staging)
        except OSError:
            pass
        raise
