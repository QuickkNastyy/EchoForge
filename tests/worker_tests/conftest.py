"""Shared fixtures for the worker tests.

The worker is driven two ways here on purpose. Most cases run it in process, which is fast
and lets a failure point at a line. The process tests in ``test_worker_process.py`` launch
a real ``python -m echoforge_worker``, because a worker that only ever runs in the test's
own interpreter proves nothing about stdio framing, encodings, exit codes, or what happens
when it dies.
"""

from __future__ import annotations

import io
import json
import math
import os
import sys
import wave
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable, Mapping

import pytest

WORKER_ROOT = Path(__file__).resolve().parents[2] / "worker"
REPO_ROOT = Path(__file__).resolve().parents[2]
if str(WORKER_ROOT) not in sys.path:
    sys.path.insert(0, str(WORKER_ROOT))

from echoforge_worker import protocol  # noqa: E402
from echoforge_worker.main import WorkerSession  # noqa: E402
from echoforge_worker.testmodes import ALLOW_VARIABLE  # noqa: E402


# --------------------------------------------------------------------------------------
# audio fixtures
# --------------------------------------------------------------------------------------


def write_wav(
    path: Path,
    seconds: float = 3.0,
    sample_rate: int = 8000,
    channels: int = 1,
    silent: bool = False,
    seed: int = 1,
) -> int:
    """Write a PCM16 chunk and return its frame count.

    The tone is generated arithmetically rather than randomly so the same arguments always
    produce the same bytes; the determinism tests depend on that being true of the input
    before they can say anything about the output.
    """
    frames = int(round(seconds * sample_rate))
    path.parent.mkdir(parents=True, exist_ok=True)

    payload = bytearray()
    for frame in range(frames):
        if silent:
            value = 0
        else:
            value = int(12000 * math.sin(2 * math.pi * (110 + (seed * 7)) * frame / sample_rate))
        for _ in range(channels):
            payload += int(value).to_bytes(2, "little", signed=True)

    with wave.open(str(path), "wb") as handle:
        handle.setnchannels(channels)
        handle.setsampwidth(2)
        handle.setframerate(sample_rate)
        handle.writeframes(bytes(payload))

    return frames


# --------------------------------------------------------------------------------------
# request fixtures
# --------------------------------------------------------------------------------------


def make_request(
    session_root: Path,
    output_path: Path,
    tracks: Mapping[str, Iterable[tuple[str, int, int, int, int]]],
    duration_seconds: float,
    epochs: list[dict[str, Any]] | None = None,
    options: dict[str, Any] | None = None,
) -> dict[str, Any]:
    """Build a transcription request.

    ``tracks`` maps a source track to tuples of
    ``(relative_path, epoch, frames, sample_rate, channels)``; chunk start times are laid
    out back to back from the epoch start, which is what the host does.
    """
    request_tracks = []
    for source_track, chunks in tracks.items():
        entries = []
        cursor = 0.0
        for index, (relative_path, epoch, frames, sample_rate, channels) in enumerate(chunks, 1):
            length = frames / sample_rate
            entries.append(
                {
                    "index": index,
                    "epoch": epoch,
                    "relative_path": relative_path,
                    "start_seconds": cursor,
                    "end_seconds": cursor + length,
                    "sample_rate": sample_rate,
                    "channels": channels,
                    "frames": frames,
                }
            )
            cursor += length
        request_tracks.append({"source_track": source_track, "chunks": entries})

    return {
        "session_id": "01JTESTSESSION",
        "transcript_revision": 1,
        "created_at_utc": "2026-08-06T12:00:00+00:00",
        "session_root": str(session_root),
        "output_path": str(output_path),
        "duration_seconds": duration_seconds,
        "epochs": epochs or [{"index": 1, "start_seconds": 0.0, "end_seconds": duration_seconds}],
        "tracks": request_tracks,
        "options": options or {"backend": "mock"},
    }


def simple_session(root: Path, silent: bool = False, seconds: float = 3.0) -> dict[str, Any]:
    """A one-epoch session with one chunk on each track, and the request that describes it."""
    session_root = root / "session"
    mic = "tracks/microphone/chunks/000001.wav"
    sys_track = "tracks/system/chunks/000001.wav"
    frames = write_wav(session_root / mic, seconds=seconds, silent=silent, seed=1)
    write_wav(session_root / sys_track, seconds=seconds, silent=silent, seed=2)

    return make_request(
        session_root=session_root,
        output_path=root / "transcript" / "transcript.v1.json",
        tracks={
            "microphone": [(mic, 1, frames, 8000, 1)],
            "system": [(sys_track, 1, frames, 8000, 1)],
        },
        duration_seconds=seconds,
    )


# --------------------------------------------------------------------------------------
# in-process driver
# --------------------------------------------------------------------------------------


@dataclass(slots=True)
class WorkerRun:
    exit_code: int
    raw_lines: list[str]
    messages: list[dict[str, Any]]
    stderr: str

    def of_type(self, kind: str) -> list[dict[str, Any]]:
        return [m for m in self.messages if m.get("type") == kind]

    def first(self, kind: str) -> dict[str, Any]:
        found = self.of_type(kind)
        assert found, f"no {kind!r} message in {[m.get('type') for m in self.messages]}"
        return found[0]

    def terminal(self) -> dict[str, Any]:
        for message in self.messages:
            if message.get("type") in {"result", "error", "cancelled"}:
                return message
        raise AssertionError(f"no terminal message in {[m.get('type') for m in self.messages]}")


def hello_line(versions: list[int] | None = None) -> str:
    return json.dumps(
        {
            "protocol_version": protocol.PROTOCOL_VERSION,
            "type": "hello",
            "host_version": "test",
            "supported_protocol_versions": versions or list(protocol.SUPPORTED_PROTOCOL_VERSIONS),
        }
    )


def start_job_line(request: dict[str, Any], job_id: str = "job-1") -> str:
    return json.dumps(
        {
            "protocol_version": protocol.PROTOCOL_VERSION,
            "type": "start_job",
            "job_id": job_id,
            "job_kind": "transcribe",
            "request": request,
        }
    )


def run_worker(
    lines: Iterable[str],
    env: Mapping[str, str] | None = None,
    watch_stdin: bool = False,
) -> WorkerRun:
    """Drive a worker in this process with a scripted stdin."""
    stdin = io.StringIO("".join(line + "\n" for line in lines))
    stdout = io.StringIO()
    stderr = io.StringIO()

    session = WorkerSession(stdin, stdout, stderr, env or {}, watch_stdin=watch_stdin)
    exit_code = session.run()

    raw = [line for line in stdout.getvalue().split("\n") if line != ""]
    messages = []
    for line in raw:
        try:
            messages.append(json.loads(line))
        except json.JSONDecodeError:
            messages.append({"type": "<unparseable>", "raw": line})

    return WorkerRun(exit_code=exit_code, raw_lines=raw, messages=messages, stderr=stderr.getvalue())


@pytest.fixture
def allow_test_modes() -> dict[str, str]:
    return {ALLOW_VARIABLE: "1"}


@pytest.fixture(scope="session")
def transcript_schema() -> dict[str, Any]:
    path = REPO_ROOT / "schemas" / "transcript.schema.json"
    return json.loads(path.read_text(encoding="utf-8"))


@pytest.fixture(scope="session")
def protocol_schema() -> dict[str, Any]:
    path = REPO_ROOT / "schemas" / "worker-protocol.schema.json"
    return json.loads(path.read_text(encoding="utf-8"))


@pytest.fixture(scope="session")
def worker_environment() -> dict[str, str]:
    """Environment for launching a real worker process."""
    env = dict(os.environ)
    env["PYTHONPATH"] = str(WORKER_ROOT)
    env["PYTHONIOENCODING"] = "utf-8"
    env["PYTHONUTF8"] = "1"
    return env
