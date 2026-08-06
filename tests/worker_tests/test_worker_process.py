"""Tests that launch a real worker process.

An in-process test proves the logic. It cannot prove anything about stdio framing across a
pipe, about UTF-8 on a Windows console handle, about exit codes, or about what the stream
looks like when the process dies halfway through a line. Those are the failures a supervisor
exists to survive, so they are exercised against a real child here.
"""

from __future__ import annotations

import json
import subprocess
import sys
import time
from pathlib import Path
from typing import Any

import pytest
from conftest import WORKER_ROOT, hello_line, simple_session, start_job_line

from echoforge_worker.testmodes import ALLOW_VARIABLE

LAUNCH_TIMEOUT = 60.0


class Worker:
    """A live worker child, driven line by line."""

    def __init__(self, process: subprocess.Popen[str]) -> None:
        self.process = process
        self.lines: list[str] = []

    def send(self, line: str) -> None:
        assert self.process.stdin is not None
        self.process.stdin.write(line + "\n")
        self.process.stdin.flush()

    def read_message(self) -> dict[str, Any] | None:
        """Next non-blank line as a message, or None at end of stream."""
        assert self.process.stdout is not None
        while True:
            line = self.process.stdout.readline()
            if line == "":
                return None
            self.lines.append(line.rstrip("\n"))
            if not line.strip():
                continue
            return json.loads(line)

    def read_until_terminal(self) -> dict[str, Any] | None:
        while True:
            message = self.read_message()
            if message is None:
                return None
            if message.get("type") in {"result", "error", "cancelled"}:
                return message

    def finish(self) -> tuple[int, str]:
        assert self.process.stdin is not None
        try:
            self.process.stdin.close()
        except OSError:
            pass
        stderr = self.process.stderr.read() if self.process.stderr else ""
        return self.process.wait(timeout=LAUNCH_TIMEOUT), stderr


@pytest.fixture
def launch(worker_environment):
    started: list[Worker] = []

    def _launch(allow_test_modes: bool = False, cwd: Path | None = None) -> Worker:
        env = dict(worker_environment)
        if allow_test_modes:
            env[ALLOW_VARIABLE] = "1"
        else:
            env.pop(ALLOW_VARIABLE, None)

        process = subprocess.Popen(
            [sys.executable, "-X", "utf8", "-m", "echoforge_worker"],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            cwd=str(cwd or WORKER_ROOT),
            env=env,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
        )
        worker = Worker(process)
        started.append(worker)
        return worker

    yield _launch

    for worker in started:
        if worker.process.poll() is None:
            worker.process.kill()
            worker.process.wait(timeout=LAUNCH_TIMEOUT)


# -- the happy path -----------------------------------------------------------------------


def test_a_real_worker_completes_a_real_job(tmp_path, launch) -> None:
    request = simple_session(tmp_path)
    worker = launch()

    worker.send(hello_line())
    ready = worker.read_message()
    assert ready["type"] == "ready"

    worker.send(start_job_line(request))
    result = worker.read_until_terminal()

    assert result["type"] == "result"
    assert Path(result["output_path"]).is_file()

    exit_code, _ = worker.finish()
    assert exit_code == 0


def test_the_worker_exits_on_its_own_after_one_job(tmp_path, launch) -> None:
    """No service, no loop, no second job. The process is the job's lifetime."""
    request = simple_session(tmp_path)
    worker = launch()

    worker.send(hello_line())
    worker.read_message()
    worker.send(start_job_line(request))
    assert worker.read_until_terminal()["type"] == "result"

    # Stdin is still open, and it still leaves.
    assert worker.process.wait(timeout=LAUNCH_TIMEOUT) == 0


def test_stdout_is_utf8_regardless_of_the_console_code_page(tmp_path, launch) -> None:
    request = simple_session(tmp_path)
    request["session_id"] = "sesión-01-日本語"
    worker = launch()

    worker.send(hello_line())
    worker.read_message()
    worker.send(start_job_line(request))
    result = worker.read_until_terminal()

    document = json.loads(Path(result["output_path"]).read_text(encoding="utf-8"))
    assert document["session_id"] == "sesión-01-日本語"


# -- awkward paths -------------------------------------------------------------------------


@pytest.mark.parametrize(
    "folder",
    [
        "a folder with spaces",
        "sesión-日本語-Ω",
        "деловая встреча (2026)",
        "Meetings 2026/Q3 review & notes",
    ],
    ids=["spaces", "non-ascii", "cyrillic-and-parens", "nested-with-ampersand"],
)
def test_paths_with_spaces_and_non_ascii_characters_work(tmp_path, launch, folder: str) -> None:
    root = tmp_path.joinpath(*folder.split("/"))
    try:
        root.mkdir(parents=True, exist_ok=True)
    except OSError:
        pytest.skip(f"this filesystem will not create {folder!r}")

    request = simple_session(root)
    worker = launch()

    worker.send(hello_line())
    worker.read_message()
    worker.send(start_job_line(request))
    result = worker.read_until_terminal()

    assert result["type"] == "result", result
    assert Path(result["output_path"]).is_file()


# -- misbehaviour ----------------------------------------------------------------------------


def run_with_mode(launch, tmp_path, mode: str, delay: float | None = None) -> tuple[Worker, dict]:
    request = simple_session(tmp_path)
    request["options"]["test_mode"] = mode
    if delay is not None:
        request["options"]["test_delay_seconds"] = delay

    worker = launch(allow_test_modes=True)
    worker.send(hello_line())
    worker.read_message()
    worker.send(start_job_line(request))
    return worker, request


def test_a_crashing_worker_leaves_no_terminal_message_and_says_why_on_stderr(
    tmp_path, launch
) -> None:
    worker, _ = run_with_mode(launch, tmp_path, "crash")

    assert worker.read_until_terminal() is None
    exit_code, stderr = worker.finish()

    assert exit_code != 0
    assert "DeliberateCrash" in stderr


def test_a_nonzero_exit_is_visible_as_an_exit_code(tmp_path, launch) -> None:
    worker, _ = run_with_mode(launch, tmp_path, "nonzero_exit")

    assert worker.read_until_terminal() is None
    exit_code, _ = worker.finish()
    assert exit_code == 7


def test_stderr_output_does_not_disturb_the_protocol_stream(tmp_path, launch) -> None:
    worker, _ = run_with_mode(launch, tmp_path, "stderr")

    result = worker.read_until_terminal()
    assert result["type"] == "result"

    exit_code, stderr = worker.finish()
    assert exit_code == 0
    assert "diagnostic line 0" in stderr
    for line in worker.lines:
        if line.strip():
            json.loads(line)


def test_an_invalid_json_line_appears_on_stdout_for_the_supervisor_to_catch(
    tmp_path, launch
) -> None:
    worker, _ = run_with_mode(launch, tmp_path, "invalid_json")

    saw_garbage = False
    while True:
        assert worker.process.stdout is not None
        line = worker.process.stdout.readline()
        if line == "":
            break
        try:
            json.loads(line)
        except json.JSONDecodeError:
            saw_garbage = True
        if '"type":"result"' in line:
            break

    assert saw_garbage


def test_a_duplicate_result_is_actually_emitted_twice(tmp_path, launch) -> None:
    worker, _ = run_with_mode(launch, tmp_path, "duplicate_result")

    results = []
    while True:
        message = worker.read_message()
        if message is None:
            break
        if message.get("type") == "result":
            results.append(message)

    assert len(results) == 2
    assert results[0] == results[1]


def test_test_modes_are_refused_unless_the_environment_allows_them(tmp_path, launch) -> None:
    request = simple_session(tmp_path)
    request["options"]["test_mode"] = "crash"

    worker = launch(allow_test_modes=False)
    worker.send(hello_line())
    worker.read_message()
    worker.send(start_job_line(request))

    messages = []
    while True:
        message = worker.read_message()
        if message is None:
            break
        messages.append(message)
        if message.get("type") in {"result", "error", "cancelled"}:
            break

    # The crash never happens, and the worker says the mode was refused rather than
    # letting a test pass in the belief that it injected a fault.
    assert any(m.get("type") == "warning" and m["code"] == "test_mode_refused" for m in messages)
    assert messages[-1]["type"] == "result"


# -- cancellation ------------------------------------------------------------------------------


def test_a_cancel_stops_the_job_promptly_and_is_acknowledged(tmp_path, launch) -> None:
    worker, _ = run_with_mode(launch, tmp_path, "delay", delay=30.0)

    started = worker.read_message()
    assert started["type"] == "started"

    time.sleep(0.2)
    worker.send(json.dumps({"protocol_version": 1, "type": "cancel", "job_id": "job-1"}))

    began = time.monotonic()
    terminal = worker.read_until_terminal()
    elapsed = time.monotonic() - began

    assert terminal["type"] == "cancelled"
    assert terminal["stage"] in {"preparing", "reading_audio", "transcribing_microphone"}
    assert elapsed < 15.0, "cancellation was not observed at a safe boundary"

    exit_code, _ = worker.finish()
    assert exit_code == 0


def test_a_cancelled_job_writes_no_transcript(tmp_path, launch) -> None:
    request = simple_session(tmp_path)
    request["options"]["test_mode"] = "delay"
    request["options"]["test_delay_seconds"] = 30.0

    worker = launch(allow_test_modes=True)
    worker.send(hello_line())
    worker.read_message()
    worker.send(start_job_line(request))
    worker.read_message()

    time.sleep(0.2)
    worker.send(json.dumps({"protocol_version": 1, "type": "cancel", "job_id": "job-1"}))
    assert worker.read_until_terminal()["type"] == "cancelled"
    worker.finish()

    assert not Path(request["output_path"]).exists()


def test_losing_stdin_stops_a_running_job(tmp_path, launch) -> None:
    """A worker whose host has gone must not keep transcribing for nobody."""
    worker, _ = run_with_mode(launch, tmp_path, "delay", delay=30.0)
    assert worker.read_message()["type"] == "started"

    assert worker.process.stdin is not None
    worker.process.stdin.close()

    began = time.monotonic()
    terminal = worker.read_until_terminal()
    assert terminal is not None and terminal["type"] == "cancelled"
    assert time.monotonic() - began < 15.0


# -- a hanging worker --------------------------------------------------------------------------


def test_a_hanging_worker_ignores_cancel_and_must_be_killed(tmp_path, launch) -> None:
    """This is the case the Job Object exists for: a grace period is not always enough."""
    worker, _ = run_with_mode(launch, tmp_path, "hang")

    assert worker.read_message()["type"] == "started"
    worker.send(json.dumps({"protocol_version": 1, "type": "cancel", "job_id": "job-1"}))

    time.sleep(1.0)
    assert worker.process.poll() is None, "the hang mode did not hang"

    worker.process.kill()
    assert worker.process.wait(timeout=LAUNCH_TIMEOUT) != 0
