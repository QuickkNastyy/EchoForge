"""Handshake, framing, and the ways a conversation can go wrong."""

from __future__ import annotations

import json

import pytest
from conftest import hello_line, run_worker, simple_session, start_job_line

from echoforge_worker import protocol
from echoforge_worker.protocol import ErrorCode, ProtocolFailure


# -- handshake ---------------------------------------------------------------------------


def test_a_worker_answers_hello_with_ready() -> None:
    run = run_worker([hello_line()])

    ready = run.first("ready")
    assert ready["protocol_version"] == protocol.PROTOCOL_VERSION
    assert protocol.PROTOCOL_VERSION in ready["supported_protocol_versions"]
    assert "mock" in ready["backends"]
    assert ready["worker_version"] == protocol.WORKER_VERSION


def test_a_host_that_cannot_speak_this_version_is_refused() -> None:
    run = run_worker([hello_line(versions=[2, 3])])

    error = run.terminal()
    assert error["type"] == "error"
    assert error["code"] == ErrorCode.UNSUPPORTED_PROTOCOL_VERSION
    assert run.exit_code == 2
    # The refusal happens before anything is read, so no job can have started.
    assert run.of_type("started") == []


def test_a_message_of_an_unknown_version_is_refused_without_being_interpreted() -> None:
    line = json.dumps({"protocol_version": 99, "type": "hello", "supported_protocol_versions": [99]})
    run = run_worker([line])

    assert run.terminal()["code"] == ErrorCode.UNSUPPORTED_PROTOCOL_VERSION


def test_the_first_message_must_be_hello() -> None:
    line = json.dumps({"protocol_version": 1, "type": "cancel", "job_id": "job-1"})
    run = run_worker([line])

    assert run.terminal()["code"] == ErrorCode.PROTOCOL_ERROR


def test_a_closed_stdin_before_the_handshake_is_a_protocol_error() -> None:
    run = run_worker([])

    assert run.terminal()["code"] == ErrorCode.PROTOCOL_ERROR


# -- framing -----------------------------------------------------------------------------


def test_every_line_of_output_is_exactly_one_json_object(tmp_path) -> None:
    request = simple_session(tmp_path)
    run = run_worker([hello_line(), start_job_line(request)])

    assert run.raw_lines, "the worker said nothing at all"
    for line in run.raw_lines:
        parsed = json.loads(line)
        assert isinstance(parsed, dict)
        assert "\n" not in line


def test_blank_lines_in_the_input_are_skipped(tmp_path) -> None:
    request = simple_session(tmp_path)
    run = run_worker(["", "   ", hello_line(), "", start_job_line(request), ""])

    assert run.terminal()["type"] == "result"


def test_a_split_line_is_still_one_message() -> None:
    """Framing is by newline, not by write. A reader must not care how it arrived."""
    whole = hello_line()
    first, second = whole[: len(whole) // 2], whole[len(whole) // 2 :]

    # Reassembled by the stream, exactly as a partial pipe read would be.
    run = run_worker([first + second])
    assert run.of_type("ready")


def test_invalid_json_is_refused_rather_than_guessed_at() -> None:
    run = run_worker(["{this is not json"])

    assert run.terminal()["code"] == ErrorCode.PROTOCOL_ERROR


def test_a_line_that_is_not_an_object_is_refused() -> None:
    run = run_worker(["[1, 2, 3]"])

    assert run.terminal()["code"] == ErrorCode.PROTOCOL_ERROR


def test_parse_line_reports_a_blank_line_as_nothing() -> None:
    assert protocol.parse_line("   \t ") is None


def test_parse_line_rejects_a_missing_type() -> None:
    with pytest.raises(ProtocolFailure):
        protocol.parse_line(json.dumps({"protocol_version": 1}))


# -- job acceptance ----------------------------------------------------------------------


def test_an_unknown_message_where_a_job_belongs_is_a_protocol_error() -> None:
    line = json.dumps({"protocol_version": 1, "type": "invented_message"})
    run = run_worker([hello_line(), line])

    assert run.terminal()["code"] == ErrorCode.PROTOCOL_ERROR


def test_an_unsupported_job_kind_is_an_invalid_request(tmp_path) -> None:
    request = simple_session(tmp_path)
    line = json.dumps(
        {
            "protocol_version": 1,
            "type": "start_job",
            "job_id": "job-1",
            "job_kind": "summarize",
            "request": request,
        }
    )
    run = run_worker([hello_line(), line])

    assert run.terminal()["code"] == ErrorCode.INVALID_REQUEST


@pytest.mark.parametrize(
    "missing",
    ["session_id", "output_path", "epochs", "tracks", "options", "duration_seconds"],
)
def test_a_request_missing_a_required_field_is_refused(tmp_path, missing: str) -> None:
    request = simple_session(tmp_path)
    del request[missing]

    run = run_worker([hello_line(), start_job_line(request)])
    terminal = run.terminal()

    assert terminal["type"] == "error"
    assert terminal["code"] == ErrorCode.INVALID_REQUEST
    assert terminal["stage"] == protocol.Stage.ACCEPTING


def test_a_chunk_naming_an_epoch_the_session_does_not_have_is_refused(tmp_path) -> None:
    request = simple_session(tmp_path)
    request["tracks"][0]["chunks"][0]["epoch"] = 9

    run = run_worker([hello_line(), start_job_line(request)])

    assert run.terminal()["code"] == ErrorCode.INVALID_REQUEST


def test_overlapping_epochs_are_refused(tmp_path) -> None:
    request = simple_session(tmp_path)
    request["epochs"] = [
        {"index": 1, "start_seconds": 0.0, "end_seconds": 3.0},
        {"index": 2, "start_seconds": 1.0, "end_seconds": 4.0},
    ]

    run = run_worker([hello_line(), start_job_line(request)])

    assert run.terminal()["code"] == ErrorCode.INVALID_REQUEST


def test_an_unknown_backend_is_reported_as_unavailable(tmp_path) -> None:
    request = simple_session(tmp_path)
    request["options"]["backend"] = "faster-whisper"

    run = run_worker([hello_line(), start_job_line(request)])
    terminal = run.terminal()

    assert terminal["code"] == ErrorCode.BACKEND_UNAVAILABLE
    assert terminal["stage"] == protocol.Stage.PREPARING


def test_a_missing_source_chunk_is_reported_without_touching_anything(tmp_path) -> None:
    request = simple_session(tmp_path)
    request["tracks"][0]["chunks"][0]["relative_path"] = "tracks/microphone/chunks/000099.wav"

    run = run_worker([hello_line(), start_job_line(request)])

    assert run.terminal()["code"] == ErrorCode.INPUT_MISSING


def test_a_chunk_path_that_escapes_the_session_is_refused(tmp_path) -> None:
    request = simple_session(tmp_path)
    request["tracks"][0]["chunks"][0]["relative_path"] = "../../elsewhere.wav"

    run = run_worker([hello_line(), start_job_line(request)])

    assert run.terminal()["code"] == ErrorCode.INPUT_INVALID


# -- progress and completion ---------------------------------------------------------------


def test_progress_is_monotonic_and_never_exceeds_its_total(tmp_path) -> None:
    request = simple_session(tmp_path)
    run = run_worker([hello_line(), start_job_line(request)])

    updates = run.of_type("progress")
    assert updates, "a job that reads two chunks reported no progress"

    completed = 0
    for update in updates:
        assert update["completed_units"] <= update["total_units"]
        assert update["completed_units"] >= completed
        assert update["stage"] in protocol.Stage.ALL
        completed = update["completed_units"]

    assert completed == updates[-1]["total_units"] == 2


def test_exactly_one_terminal_message_is_emitted(tmp_path) -> None:
    request = simple_session(tmp_path)
    run = run_worker([hello_line(), start_job_line(request)])

    terminals = [m for m in run.messages if m.get("type") in {"result", "error", "cancelled"}]
    assert len(terminals) == 1
    assert terminals[0]["type"] == "result"
    assert run.messages[-1] is terminals[0]


def test_a_cancel_before_the_job_starts_ends_the_worker_quietly(tmp_path) -> None:
    cancel = json.dumps({"protocol_version": 1, "type": "cancel", "job_id": "job-1"})
    run = run_worker([hello_line(), cancel])

    assert run.exit_code == 0
    assert run.of_type("started") == []
    assert run.of_type("error") == []
