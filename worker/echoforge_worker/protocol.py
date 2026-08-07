"""The NDJSON worker protocol: constants, framing, and message construction.

One JSON object per line. A line is never split across writes and two objects never share
a line. Blank and whitespace-only lines carry no meaning and are skipped by both sides.

Every message names its protocol version. A version this build does not speak is refused
before its body is looked at, because parsing the fields of an unknown version is exactly
how a build mismatch becomes a silent misinterpretation rather than a clear failure.

``schemas/worker-protocol.schema.json`` is authoritative for the shapes; this module is
the implementation of that file, not a second opinion about it.
"""

from __future__ import annotations

import json
import threading
from typing import Any, Final, TextIO

PROTOCOL_VERSION: Final[int] = 1
SUPPORTED_PROTOCOL_VERSIONS: Final[tuple[int, ...]] = (1,)
WORKER_VERSION: Final[str] = "0.1.0"

TRANSCRIBE_JOB_KIND: Final[str] = "transcribe"
SUMMARIZE_JOB_KIND: Final[str] = "summarize"


class Stage:
    """Where a job had reached. Mirrors ``WorkerStage`` on the host side."""

    HANDSHAKE: Final[str] = "handshake"
    ACCEPTING: Final[str] = "accepting"
    PREPARING: Final[str] = "preparing"
    READING_AUDIO: Final[str] = "reading_audio"
    TRANSCRIBING_MICROPHONE: Final[str] = "transcribing_microphone"
    TRANSCRIBING_SYSTEM: Final[str] = "transcribing_system"
    MERGING: Final[str] = "merging"
    VALIDATING: Final[str] = "validating"
    WRITING_OUTPUT: Final[str] = "writing_output"
    FINISHED: Final[str] = "finished"

    ALL: Final[frozenset[str]] = frozenset(
        {
            HANDSHAKE,
            ACCEPTING,
            PREPARING,
            READING_AUDIO,
            TRANSCRIBING_MICROPHONE,
            TRANSCRIBING_SYSTEM,
            MERGING,
            VALIDATING,
            WRITING_OUTPUT,
            FINISHED,
        }
    )


class ErrorCode:
    """The classes of failure a worker may report.

    ``timeout`` is deliberately absent: a timeout is the host's verdict on a silent child,
    never something the child claims about itself.
    """

    UNSUPPORTED_PROTOCOL_VERSION: Final[str] = "unsupported_protocol_version"
    PROTOCOL_ERROR: Final[str] = "protocol_error"
    INVALID_REQUEST: Final[str] = "invalid_request"
    INPUT_MISSING: Final[str] = "input_missing"
    INPUT_INVALID: Final[str] = "input_invalid"
    AUDIO_UNREADABLE: Final[str] = "audio_unreadable"
    BACKEND_UNAVAILABLE: Final[str] = "backend_unavailable"
    BACKEND_FAILED: Final[str] = "backend_failed"
    OUTPUT_WRITE_FAILED: Final[str] = "output_write_failed"
    INTERNAL_ERROR: Final[str] = "internal_error"


class WorkerFailure(Exception):
    """A failure that maps cleanly onto a protocol ``error`` message.

    ``detail`` is a technical diagnostic destined for the host's log. It must never contain
    transcript text or audio content, and the host never renders it into user-facing text.
    """

    def __init__(self, code: str, stage: str, detail: str, retryable: bool = False) -> None:
        super().__init__(f"{code} at {stage}: {detail}")
        self.code = code
        self.stage = stage
        self.detail = detail
        self.retryable = retryable


class ProtocolFailure(WorkerFailure):
    """The host said something this worker cannot make sense of."""

    def __init__(self, detail: str, code: str = ErrorCode.PROTOCOL_ERROR) -> None:
        super().__init__(code, Stage.HANDSHAKE, detail)


class Cancelled(Exception):
    """Raised internally when a cancel is observed at a safe boundary."""


def parse_line(line: str) -> dict[str, Any] | None:
    """Turn one line into a message, or ``None`` if the line is blank.

    Raises :class:`ProtocolFailure` for anything that is not a well-formed envelope of a
    version this worker speaks.
    """
    if not line.strip():
        return None

    try:
        message = json.loads(line)
    except json.JSONDecodeError as error:
        raise ProtocolFailure(f"line is not valid JSON: {error.msg}") from error

    if not isinstance(message, dict):
        raise ProtocolFailure("line is not a JSON object")

    version = message.get("protocol_version")
    if not isinstance(version, int) or isinstance(version, bool):
        raise ProtocolFailure("protocol_version is missing or not an integer")

    if version not in SUPPORTED_PROTOCOL_VERSIONS:
        raise ProtocolFailure(
            f"protocol version {version} is not supported by this worker",
            code=ErrorCode.UNSUPPORTED_PROTOCOL_VERSION,
        )

    kind = message.get("type")
    if not isinstance(kind, str) or not kind:
        raise ProtocolFailure("type is missing or not a string")

    return message


class MessageWriter:
    """Writes protocol lines.

    Serialisation is compact and deterministic: no spaces, keys in insertion order, and no
    ASCII escaping, so identical content produces identical bytes. The lock exists because
    fault-injection paths and the normal path can both reach this while a reader thread is
    alive, and half a line on stdout would be unrecoverable.
    """

    def __init__(self, stream: TextIO) -> None:
        self._stream = stream
        self._lock = threading.Lock()

    def send(self, message: dict[str, Any]) -> None:
        line = json.dumps(message, ensure_ascii=False, separators=(",", ":"))
        self.write_raw(line)

    def write_raw(self, line: str) -> None:
        """Write one line verbatim. Used by fault injection to emit deliberate nonsense."""
        with self._lock:
            self._stream.write(line + "\n")
            self._stream.flush()


def ready(backends: list[str], python_version: str) -> dict[str, Any]:
    return {
        "protocol_version": PROTOCOL_VERSION,
        "type": "ready",
        "worker_version": WORKER_VERSION,
        "python_version": python_version,
        "supported_protocol_versions": list(SUPPORTED_PROTOCOL_VERSIONS),
        "backends": backends,
    }


def started(job_id: str, backend: str, recognizes_speech: bool) -> dict[str, Any]:
    return {
        "protocol_version": PROTOCOL_VERSION,
        "type": "started",
        "job_id": job_id,
        "backend": backend,
        "recognizes_speech": recognizes_speech,
    }


def progress(job_id: str, stage: str, completed_units: int, total_units: int) -> dict[str, Any]:
    return {
        "protocol_version": PROTOCOL_VERSION,
        "type": "progress",
        "job_id": job_id,
        "stage": stage,
        "completed_units": completed_units,
        "total_units": total_units,
    }


def warning(job_id: str, code: str, detail: str | None = None) -> dict[str, Any]:
    message: dict[str, Any] = {
        "protocol_version": PROTOCOL_VERSION,
        "type": "warning",
        "job_id": job_id,
        "code": code,
    }
    if detail is not None:
        message["detail"] = detail
    return message


def result(
    job_id: str,
    output_path: str,
    sha256: str,
    segment_count: int,
    duration_seconds: float,
) -> dict[str, Any]:
    return {
        "protocol_version": PROTOCOL_VERSION,
        "type": "result",
        "job_id": job_id,
        "output_path": output_path,
        "sha256": sha256,
        "segment_count": segment_count,
        "duration_seconds": duration_seconds,
    }


def error(
    code: str,
    stage: str,
    detail: str | None = None,
    job_id: str | None = None,
    retryable: bool = False,
) -> dict[str, Any]:
    message: dict[str, Any] = {
        "protocol_version": PROTOCOL_VERSION,
        "type": "error",
        "code": code,
        "stage": stage,
        "retryable": retryable,
    }
    if job_id is not None:
        message["job_id"] = job_id
    if detail is not None:
        message["detail"] = detail
    return message


def cancelled(job_id: str, stage: str) -> dict[str, Any]:
    return {
        "protocol_version": PROTOCOL_VERSION,
        "type": "cancelled",
        "job_id": job_id,
        "stage": stage,
    }
