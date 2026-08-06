"""Fault injection, used only to prove the supervisor handles a misbehaving child.

Every mode here is locked behind ``ECHOFORGE_WORKER_ALLOW_TEST_MODES=1`` in the worker's
environment. The application never sets it, so a production run cannot reach any of this
however the request is filled in — which matters, because several of these modes
deliberately corrupt the protocol stream or kill the process.

These exist because mocking the process layer proves only that the mock behaves. A
supervisor is judged on what it does when a real child writes half a line and dies.
"""

from __future__ import annotations

import os
import sys
import threading
import time
from typing import Any, Callable, Final, Mapping

from .protocol import MessageWriter, PROTOCOL_VERSION

ALLOW_VARIABLE: Final[str] = "ECHOFORGE_WORKER_ALLOW_TEST_MODES"

NORMAL: Final[str] = "normal"
DELAY: Final[str] = "delay"
CRASH: Final[str] = "crash"
NONZERO_EXIT: Final[str] = "nonzero_exit"
INVALID_JSON: Final[str] = "invalid_json"
UNKNOWN_MESSAGE: Final[str] = "unknown_message"
MALFORMED_PROGRESS: Final[str] = "malformed_progress"
MALFORMED_RESULT: Final[str] = "malformed_result"
DUPLICATE_RESULT: Final[str] = "duplicate_result"
OUTPUT_AFTER_COMPLETION: Final[str] = "output_after_completion"
STDERR: Final[str] = "stderr"
HANG: Final[str] = "hang"

KNOWN_MODES: Final[frozenset[str]] = frozenset(
    {
        NORMAL,
        DELAY,
        CRASH,
        NONZERO_EXIT,
        INVALID_JSON,
        UNKNOWN_MESSAGE,
        MALFORMED_PROGRESS,
        MALFORMED_RESULT,
        DUPLICATE_RESULT,
        OUTPUT_AFTER_COMPLETION,
        STDERR,
        HANG,
    }
)

#: How long ``hang`` refuses to finish. Long enough that any sane timeout fires first, and
#: bounded so a stray process cannot outlive a test run by more than a few minutes.
HANG_SECONDS: Final[float] = 600.0


class DeliberateCrash(RuntimeError):
    """Raised by the ``crash`` mode. Escapes to the top level on purpose."""


class FaultInjector:
    """Applies one requested fault, or nothing at all."""

    def __init__(self, mode: str | None, delay_seconds: float | None, allowed: bool) -> None:
        self.mode = mode if (allowed and mode in KNOWN_MODES and mode != NORMAL) else None
        self.delay_seconds = delay_seconds
        self.requested_but_refused = bool(mode) and mode != NORMAL and not allowed

    @staticmethod
    def from_environment(
        mode: str | None, delay_seconds: float | None, env: Mapping[str, str]
    ) -> FaultInjector:
        return FaultInjector(mode, delay_seconds, env.get(ALLOW_VARIABLE) == "1")

    @property
    def active(self) -> bool:
        return self.mode is not None

    def after_started(self, writer: MessageWriter, stderr: Any, job_id: str) -> None:
        """Corrupt the stream immediately after the job is acknowledged."""
        if self.mode == INVALID_JSON:
            writer.write_raw('{"protocol_version":1,"type":"progress"  <- not json at all')
        elif self.mode == UNKNOWN_MESSAGE:
            writer.send(
                {
                    "protocol_version": PROTOCOL_VERSION,
                    "type": "unheard_of_message",
                    "job_id": job_id,
                }
            )
        elif self.mode == MALFORMED_PROGRESS:
            writer.send(
                {
                    "protocol_version": PROTOCOL_VERSION,
                    "type": "progress",
                    "job_id": job_id,
                    "stage": "transcribing_microphone",
                    "completed_units": 99,
                    "total_units": 3,
                }
            )
        elif self.mode == STDERR:
            for index in range(5):
                print(f"diagnostic line {index}: backend probe", file=stderr)
            stderr.flush()

    def during_transcription(self, cancelled: Callable[[], bool]) -> None:
        """Stall, die, or leave abruptly, in the middle of the job."""
        if self.mode == DELAY:
            _sleep_until(self.delay_seconds or 1.0, cancelled)
        elif self.mode == HANG:
            # Deliberately ignores cancellation: this is what the Job Object is for.
            time.sleep(HANG_SECONDS)
        elif self.mode == CRASH:
            raise DeliberateCrash("deliberate worker crash for supervisor tests")
        elif self.mode == NONZERO_EXIT:
            # No terminal message, no cleanup, no flush. Exactly what a hard exit looks like.
            sys.stdout.flush()
            os._exit(7)

    def corrupt_result(self, message: dict[str, Any]) -> dict[str, Any]:
        if self.mode == MALFORMED_RESULT:
            corrupted = dict(message)
            corrupted["sha256"] = "not-a-real-digest"
            return corrupted
        return message

    def after_result(self, writer: MessageWriter, message: dict[str, Any], job_id: str) -> None:
        """Say something after the conversation was already over."""
        if self.mode == DUPLICATE_RESULT:
            writer.send(message)
        elif self.mode == OUTPUT_AFTER_COMPLETION:
            writer.send(
                {
                    "protocol_version": PROTOCOL_VERSION,
                    "type": "progress",
                    "job_id": job_id,
                    "stage": "finished",
                    "completed_units": 1,
                    "total_units": 1,
                }
            )


def _sleep_until(seconds: float, cancelled: Callable[[], bool]) -> None:
    """Sleep in slices so a cancel is noticed promptly."""
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if cancelled():
            return
        threading.Event().wait(0.02)
