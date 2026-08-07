"""The worker's one conversation: handshake, one job, one terminal message, exit.

The worker never loops waiting for more work. It is launched for a job, it answers, and the
process ends. That is what keeps EchoForge free of a background service, and it is what
makes the Job Object a sufficient cancellation mechanism rather than a blunt one.

Nothing here writes to a source recording. The only file this process creates is the
transcript it was asked for, written to a temporary neighbour, flushed, and moved into
place atomically so a crash mid-write cannot leave a half-transcript looking finished.
"""

from __future__ import annotations

import hashlib
import json
import os
import platform
import sys
import threading
from pathlib import Path
from typing import Any, Final, Mapping, TextIO

from . import protocol
from .models import TranscriptionRequest
from .protocol import (
    Cancelled,
    ErrorCode,
    MessageWriter,
    ProtocolFailure,
    Stage,
    WorkerFailure,
)
from .testmodes import DeliberateCrash, FaultInjector
from .transcribe import (
    BackendContext,
    available_backends,
    build_transcript,
    resolve_backend,
)

EXIT_OK: Final[int] = 0
EXIT_FAILED: Final[int] = 1
EXIT_PROTOCOL: Final[int] = 2


class WorkerSession:
    """One worker lifetime, with its streams injected so it can be driven in a test."""

    def __init__(
        self,
        stdin: TextIO,
        stdout: TextIO,
        stderr: TextIO,
        env: Mapping[str, str],
        watch_stdin: bool = True,
    ) -> None:
        self._stdin = stdin
        self._stderr = stderr
        self._env = env
        self._writer = MessageWriter(stdout)
        self._watch_stdin = watch_stdin

        self._cancel = threading.Event()
        self._job_id: str | None = None
        self._stage = Stage.HANDSHAKE
        self._completed_units = 0
        self._total_units = 0

    # -- lifecycle ---------------------------------------------------------------------

    def run(self) -> int:
        try:
            self._handshake()
            job_id, request = self._accept_job()
            return self._run_job(job_id, request)
        except ProtocolFailure as failure:
            self._send_error(failure)
            return EXIT_PROTOCOL
        except WorkerFailure as failure:
            self._send_error(failure)
            return EXIT_FAILED
        except Cancelled:
            # Cancelled before a job existed. Nothing was started, so nothing is reported.
            return EXIT_OK

    def _handshake(self) -> None:
        self._stage = Stage.HANDSHAKE
        message = self._read_message()
        if message is None:
            raise ProtocolFailure("stdin closed before the handshake")

        if message["type"] != "hello":
            raise ProtocolFailure(f"expected 'hello', received {message['type']!r}")

        host_versions = message.get("supported_protocol_versions")
        if not isinstance(host_versions, list) or not host_versions:
            raise ProtocolFailure("hello declares no supported protocol versions")

        # Both sides must agree, not merely one. A host that can only speak version 2 is
        # refused here rather than discovering the mismatch halfway through a job.
        if protocol.PROTOCOL_VERSION not in host_versions:
            raise ProtocolFailure(
                f"host speaks {host_versions}; this worker speaks "
                f"{list(protocol.SUPPORTED_PROTOCOL_VERSIONS)}",
                code=ErrorCode.UNSUPPORTED_PROTOCOL_VERSION,
            )

        self._writer.send(protocol.ready(available_backends(), platform.python_version()))

    def _accept_job(self) -> tuple[str, TranscriptionRequest]:
        self._stage = Stage.ACCEPTING
        message = self._read_message()
        if message is None:
            raise ProtocolFailure("stdin closed before a job arrived")

        if message["type"] == "cancel":
            raise Cancelled()

        if message["type"] != "start_job":
            raise ProtocolFailure(f"expected 'start_job', received {message['type']!r}")

        job_id = message.get("job_id")
        if not isinstance(job_id, str) or not job_id:
            raise ProtocolFailure("start_job carries no job_id")
        self._job_id = job_id

        kind = message.get("job_kind")

        if kind == protocol.SUMMARIZE_JOB_KIND:
            if "summary_request" not in message:
                raise WorkerFailure(
                    ErrorCode.INVALID_REQUEST, Stage.ACCEPTING, "start_job carries no summary request"
                )

            from .summarize import SummaryRequest

            return job_id, SummaryRequest.from_json(message["summary_request"])

        if kind != protocol.TRANSCRIBE_JOB_KIND:
            raise WorkerFailure(
                ErrorCode.INVALID_REQUEST,
                Stage.ACCEPTING,
                f"unsupported job kind {kind!r}",
            )

        if "request" not in message:
            raise WorkerFailure(
                ErrorCode.INVALID_REQUEST, Stage.ACCEPTING, "start_job carries no request"
            )

        return job_id, TranscriptionRequest.from_json(message["request"])

    def _run_job(self, job_id: str, request: Any) -> int:
        from .summarize import SummaryRequest

        if isinstance(request, SummaryRequest):
            return self._run_summary_job(job_id, request)

        return self._run_transcription_job(job_id, request)

    def _run_summary_job(self, job_id: str, request: Any) -> int:
        """Summarise one transcript revision, then exit.

        Structurally the same as a transcription job - the same faults, the same cancellation,
        the same one terminal message - because a worker runs one job of one kind and leaves.
        """
        from .summarize import (
            build_summary,
            read_transcript,
            resolve_summary_backend,
            slice_chunk,
            synthesize,
        )

        faults = FaultInjector.from_environment(
            request.test_mode,
            request.test_delay_seconds,
            self._env,
            repair_attempt=request.repair_attempt,
        )
        if faults.requested_but_refused:
            self._writer.send(
                protocol.warning(
                    job_id,
                    "test_mode_refused",
                    "a test mode was requested but this worker was not started with test modes enabled",
                )
            )

        # The server, when there is one, is started before anything is read and stopped in the
        # finally below whatever happens next - including a cancel arriving mid-generation. The
        # Job Object would take it down anyway; this is so the ordinary path does not rely on that.
        server = None

        try:
            backend, server = self._resolve_summary_backend(job_id, request)
        except Cancelled:
            self._writer.send(protocol.cancelled(job_id, self._stage))
            return EXIT_OK

        self._writer.send(protocol.started(job_id, backend.name, backend.produces_summaries))

        if self._watch_stdin:
            self._start_cancel_watcher(job_id)

        faults.after_started(self._writer, self._stderr, job_id)

        self._total_units = max(1, len(request.chunks))
        self._emit_progress(Stage.PREPARING)

        try:
            faults.during_transcription(self._cancel.is_set)

            document, segments = read_transcript(Path(request.transcript_path))

            candidates = []
            for chunk in request.chunks:
                self._check_cancelled()
                candidates.extend(backend.extract(chunk, slice_chunk(segments, chunk), request))
                self._completed_units += 1
                self._emit_progress(Stage.TRANSCRIBING_MICROPHONE)

            self._check_cancelled()
            self._emit_progress(Stage.MERGING)

            def folded_a_level(level: int, groups: int, remaining: int) -> None:
                # A fold over a long meeting is several passes, and silence during them looks
                # like a stall. The check is here too so a cancel is noticed between levels.
                self._check_cancelled()
                self._emit_progress(Stage.MERGING)

            synthesis = synthesize(
                candidates,
                backend,
                request,
                group_size=request.synthesis_group_size,
                on_level=folded_a_level,
            )

            self._check_cancelled()
            self._emit_progress(Stage.VALIDATING)
            summary = build_summary(request, document, segments, synthesis.candidates, backend, synthesis)

            self._check_cancelled()
            self._emit_progress(Stage.WRITING_OUTPUT)
            digest = _write_json(request.output_path, faults.corrupt_summary(summary))
        except Cancelled:
            self._writer.send(protocol.cancelled(job_id, self._stage))
            return EXIT_OK
        finally:
            if server is not None:
                server.stop()

        message = faults.corrupt_result(
            protocol.result(
                job_id=job_id,
                output_path=str(Path(request.output_path)),
                sha256=digest,
                segment_count=len(summary.get("decisions", [])) + len(summary.get("action_items", [])),
                duration_seconds=0.0,
            )
        )
        self._writer.send(message)
        faults.after_result(self._writer, message, job_id)
        return EXIT_OK

    def _resolve_summary_backend(self, job_id: str, request: Any) -> tuple[Any, Any]:
        """The placeholder, or a real model with a server behind it.

        Returns the backend and the server that has to be stopped afterwards - ``None`` for the
        placeholder, which has nothing to tear down.
        """
        from .summarize import PRODUCTION_BACKEND, resolve_summary_backend

        if request.backend != PRODUCTION_BACKEND:
            return resolve_summary_backend(request.backend), None

        # Imported here so a machine with no summary model installed never loads any of it.
        from .gemma_summary import GemmaSummaryBackend
        from .llama_server import (
            CPU_ONLY_LADDER,
            DEFAULT_LADDER,
            LlamaServerError,
            failure_from,
            start_with_fallback,
        )

        if not request.llama_binary_path or not request.model_path:
            raise WorkerFailure(
                ErrorCode.BACKEND_UNAVAILABLE,
                Stage.PREPARING,
                "the production summary backend was asked for without a verified runtime and model",
            )

        ladder = CPU_ONLY_LADDER if request.summary_profile == "summary-cpu-q4" else DEFAULT_LADDER

        def stepped_down(tried: Any, next_profile: Any, detail: str) -> None:
            # Never silent. The user is told the model would not run at the size EchoForge asked
            # for, and the revision records what it actually ran at.
            self._writer.send(
                protocol.warning(
                    job_id,
                    "summary_context_reduced",
                    f"{tried.description} did not start ({detail}); trying {next_profile.description}",
                )
            )

        try:
            server = start_with_fallback(
                binary_path=Path(request.llama_binary_path),
                model_path=Path(request.model_path),
                ladder=ladder,
                seed=request.seed,
                cancelled=self._cancel.is_set,
                on_fallback=stepped_down,
            )
        except LlamaServerError as error:
            raise failure_from(error) from error

        return (
            GemmaSummaryBackend(
                server,
                prompt_version=request.prompt_version,
                model_revision=Path(request.model_path).parent.name,
            ),
            server,
        )

    def _run_transcription_job(self, job_id: str, request: TranscriptionRequest) -> int:
        faults = FaultInjector.from_environment(
            request.options.test_mode, request.options.test_delay_seconds, self._env
        )
        if faults.requested_but_refused:
            # Say so rather than silently succeeding: a test that believed it injected a
            # fault and did not would pass for the wrong reason.
            self._writer.send(
                protocol.warning(
                    job_id,
                    "test_mode_refused",
                    "a test mode was requested but this worker was not started with test modes enabled",
                )
            )

        backend = resolve_backend(request.options.backend)
        self._writer.send(protocol.started(job_id, backend.name, backend.recognizes_speech))

        if self._watch_stdin:
            self._start_cancel_watcher(job_id)

        faults.after_started(self._writer, self._stderr, job_id)

        self._total_units = request.total_chunks()
        self._emit_progress(Stage.PREPARING)

        try:
            faults.during_transcription(self._cancel.is_set)

            context = BackendContext(
                session_root=request.session_root,
                cancelled=self._cancel.is_set,
                on_chunk_completed=self._chunk_completed,
                on_warning=lambda code, detail: self._writer.send(
                    protocol.warning(job_id, code, detail)
                ),
            )

            transcript = build_transcript(request, backend, context, self._emit_progress)

            self._check_cancelled()
            self._emit_progress(Stage.VALIDATING)
            _reject_impossible_times(request, transcript)

            self._check_cancelled()
            self._emit_progress(Stage.WRITING_OUTPUT)
            digest = _write_transcript(request.output_path, transcript)
        except Cancelled:
            self._writer.send(protocol.cancelled(job_id, self._stage))
            return EXIT_OK

        message = faults.corrupt_result(
            protocol.result(
                job_id=job_id,
                output_path=str(Path(request.output_path)),
                sha256=digest,
                segment_count=len(transcript.segments),
                duration_seconds=transcript.duration_seconds,
            )
        )
        self._writer.send(message)
        faults.after_result(self._writer, message, job_id)
        return EXIT_OK

    # -- plumbing ----------------------------------------------------------------------

    def _read_message(self) -> dict[str, Any] | None:
        """Read until a line means something. Blank lines are skipped, not refused."""
        while True:
            line = self._stdin.readline()
            if line == "":
                return None
            message = protocol.parse_line(line)
            if message is not None:
                return message

    def _start_cancel_watcher(self, job_id: str) -> None:
        """Watch stdin for a cancel while the job runs.

        Losing stdin counts as a cancel. The Job Object should already have taken the
        process down if the host died, but a worker that would otherwise transcribe an
        hour of audio for a parent that no longer exists is worth guarding against twice.
        """

        def watch() -> None:
            while True:
                try:
                    line = self._stdin.readline()
                except (OSError, ValueError):
                    self._cancel.set()
                    return
                if line == "":
                    self._cancel.set()
                    return
                try:
                    message = protocol.parse_line(line)
                except ProtocolFailure:
                    # A malformed line mid-job is not worth killing the job over; the host
                    # is the one that decides to stop, and it can still send a valid cancel.
                    continue
                if message and message.get("type") == "cancel" and message.get("job_id") == job_id:
                    self._cancel.set()
                    return

        thread = threading.Thread(target=watch, name="echoforge-cancel-watcher", daemon=True)
        thread.start()

    def _check_cancelled(self) -> None:
        if self._cancel.is_set():
            raise Cancelled()

    def _chunk_completed(self, source_track: str) -> None:
        self._completed_units += 1
        self._emit_progress(self._stage)

    def _emit_progress(self, stage: str) -> None:
        self._stage = stage
        if self._job_id is None:
            return
        self._writer.send(
            protocol.progress(self._job_id, stage, self._completed_units, self._total_units)
        )

    def _send_error(self, failure: WorkerFailure) -> None:
        self._writer.send(
            protocol.error(
                code=failure.code,
                stage=failure.stage,
                detail=failure.detail,
                job_id=self._job_id,
                retryable=failure.retryable,
            )
        )


def _reject_impossible_times(request: TranscriptionRequest, transcript: Any) -> None:
    """Refuse to write a transcript whose times could not have happened.

    The worker checks its own output before it becomes a file. A segment outside its epoch
    names a moment when nothing was captured; a word outside its segment cannot be played.
    Either would be discovered later by the host's validator, but a bad revision that never
    reaches the disk is better than one that has to be retracted.
    """
    for segment in transcript.segments:
        epoch = request.epoch(segment.epoch)
        if segment.end_seconds < segment.start_seconds:
            raise WorkerFailure(
                ErrorCode.INTERNAL_ERROR,
                Stage.VALIDATING,
                f"{segment.id} ends before it starts",
            )
        if segment.start_seconds < epoch.start_seconds - 1e-6 or segment.end_seconds > epoch.end_seconds + 1e-6:
            raise WorkerFailure(
                ErrorCode.INTERNAL_ERROR,
                Stage.VALIDATING,
                f"{segment.id} falls outside epoch {segment.epoch}",
            )
        for word in segment.words:
            if word.start_seconds < segment.start_seconds - 1e-6 or word.end_seconds > segment.end_seconds + 1e-6:
                raise WorkerFailure(
                    ErrorCode.INTERNAL_ERROR,
                    Stage.VALIDATING,
                    f"a word in {segment.id} falls outside its segment",
                )


def _write_json(output_path: str, payload: Any) -> str:
    """Write a JSON document atomically and return the digest of the bytes written."""
    destination = Path(output_path)
    try:
        destination.parent.mkdir(parents=True, exist_ok=True)
        encoded = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")

        staging = destination.with_name(destination.name + ".partial")
        with open(staging, "wb") as handle:
            handle.write(encoded)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(staging, destination)
    except OSError as error:
        raise WorkerFailure(
            ErrorCode.OUTPUT_WRITE_FAILED,
            Stage.WRITING_OUTPUT,
            f"could not write the output: {error}",
        ) from error

    return hashlib.sha256(encoded).hexdigest()


def _write_transcript(output_path: str, transcript: Any) -> str:
    """Write the transcript atomically and return the digest of the exact bytes written."""
    destination = Path(output_path)
    try:
        destination.parent.mkdir(parents=True, exist_ok=True)
        payload = json.dumps(
            transcript.to_json(), ensure_ascii=False, separators=(",", ":")
        ).encode("utf-8")

        # A temporary neighbour in the destination directory, so the rename is atomic and
        # a crash mid-write cannot leave a partial file wearing the real name.
        staging = destination.with_name(destination.name + ".partial")
        with open(staging, "wb") as handle:
            handle.write(payload)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(staging, destination)
    except OSError as error:
        raise WorkerFailure(
            ErrorCode.OUTPUT_WRITE_FAILED,
            Stage.WRITING_OUTPUT,
            f"could not write the transcript: {error}",
        ) from error

    return hashlib.sha256(payload).hexdigest()


def entry_point() -> int:
    """Console entry point. Fixes the stream encodings before anything is written."""
    for stream in (sys.stdin, sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", newline="\n")  # type: ignore[union-attr]
        except (AttributeError, ValueError):
            pass

    session = WorkerSession(sys.stdin, sys.stdout, sys.stderr, os.environ)
    try:
        return session.run()
    except DeliberateCrash:
        raise
    except Exception as error:  # noqa: BLE001 - last resort, so a crash still says something
        print(f"echoforge worker: unhandled {type(error).__name__}: {error}", file=sys.stderr)
        return EXIT_FAILED


if __name__ == "__main__":
    sys.exit(entry_point())
