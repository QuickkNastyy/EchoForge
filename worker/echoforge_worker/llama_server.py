"""The local llama.cpp server: start it, use it, make sure it is gone.

**Why a server rather than one-shot invocations.** ``llama-cli`` can generate once and exit, which
looks like the simpler and more supervisable choice. It is not, for this workload. Summarising a
meeting is many generations - one per transcript chunk, then one per synthesis fold, then possibly
one repair - and each ``llama-cli`` run would load the 6.98 GB model again from scratch. Loading is
about seven seconds; a fifteen-chunk meeting would spend nearly two minutes doing nothing but
re-reading the same weights, and a cancel would have to be handled once per invocation anyway. The
server loads once, answers every request for the job, and exits with it. The process supervision
problem is identical either way: one child, one Job Object, one thing to make sure is dead.

**The server is not a service.** It listens on the loopback interface on a port chosen at launch,
for the duration of one summarisation job. There is no installed service, no autostart, and no
process that outlives the job that asked for it. The .NET supervisor puts the Python worker in a
Windows Job Object with kill-on-close, and this process is a child of that worker, so the operating
system removes it even if this module never gets the chance to.

Nothing here ever puts transcript text or generated summary text into an error message. The server
writes prompts to its own log at high verbosity, which is why the log is not read for anything but
diagnosis and is never surfaced to a user.
"""

from __future__ import annotations

import json
import os
import socket
import subprocess
import threading
import time
import urllib.error
import urllib.request
from collections import deque
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Final, Sequence

from .protocol import Cancelled, ErrorCode, Stage, WorkerFailure

#: How long the model may take to load before the attempt is abandoned. A cold 7 GB read off a
#: slow disk is minutes, not seconds, so this is generous; it exists to catch a server that will
#: never become ready, not to police a slow machine.
STARTUP_TIMEOUT_SECONDS: Final[float] = 300.0

#: How long one generation may take. Long transcript chunks on the CPU profile are genuinely slow.
GENERATION_TIMEOUT_SECONDS: Final[float] = 900.0

#: How much of the server's stderr to keep. Bounded because a verbose server can produce megabytes
#: and because the tail is the part that says why it died.
STDERR_LINES: Final[int] = 400

#: Substrings that mean the server ran out of memory rather than failing for some other reason.
#: Matched case-insensitively against the captured stderr.
_OOM_MARKERS: Final[tuple[str, ...]] = (
    "out of memory",
    "cudamalloc",
    "failed to allocate",
    "unable to allocate",
    "insufficient memory",
    "cuda error",
    "ggml_backend_cuda_buffer_type_alloc_buffer",
)


@dataclass(frozen=True, slots=True)
class LlamaProfile:
    """One attempt's worth of memory settings.

    ``name`` is what gets recorded against the summary revision and shown to the user, so it has
    to mean something to somebody reading it later, not just to this module.
    """

    name: str
    context_tokens: int
    gpu_layers: int
    cache_type: str
    description: str

    @property
    def uses_gpu(self) -> bool:
        return self.gpu_layers > 0


#: The fallback ladder, most capable first.
#:
#: The order is deliberate and each step gives up exactly one thing. Context is halved before
#: layers are moved off the GPU, because a shorter context costs more chunks - which EchoForge
#: handles correctly and visibly - while moving layers to system memory costs throughput on every
#: token of every chunk. The CPU rung is last and is genuinely slow; it exists so a machine with no
#: usable GPU has a real path rather than a hang.
DEFAULT_LADDER: Final[tuple[LlamaProfile, ...]] = (
    LlamaProfile("cuda-32k", 32768, 99, "q8_0", "GPU, 32K context"),
    LlamaProfile("cuda-16k", 16384, 99, "q8_0", "GPU, 16K context"),
    LlamaProfile("cuda-8k", 8192, 99, "q8_0", "GPU, 8K context"),
    LlamaProfile("cuda-8k-partial", 8192, 24, "q8_0", "GPU, 8K context, half the layers in system memory"),
    LlamaProfile("cpu-8k", 8192, 0, "q8_0", "CPU only"),
)

CPU_ONLY_LADDER: Final[tuple[LlamaProfile, ...]] = (
    LlamaProfile("cpu-8k", 8192, 0, "q8_0", "CPU only"),
)


class LlamaServerError(RuntimeError):
    """The server could not be started or could not answer. Carries no meeting content."""

    def __init__(self, detail: str, *, out_of_memory: bool = False) -> None:
        super().__init__(detail)
        self.detail = detail
        self.out_of_memory = out_of_memory


@dataclass
class LlamaServer:
    """One ephemeral llama.cpp server, bound to one job."""

    binary_path: Path
    model_path: Path
    profile: LlamaProfile
    seed: int = 7
    cancelled: Callable[[], bool] | None = None
    startup_timeout: float = STARTUP_TIMEOUT_SECONDS
    generation_timeout: float = GENERATION_TIMEOUT_SECONDS

    _process: subprocess.Popen[bytes] | None = field(default=None, init=False, repr=False)
    _port: int = field(default=0, init=False)
    _stderr: deque[str] = field(default_factory=lambda: deque(maxlen=STDERR_LINES), init=False, repr=False)
    _reader: threading.Thread | None = field(default=None, init=False, repr=False)
    _actual_context: int = field(default=0, init=False)

    # -- lifecycle -----------------------------------------------------------------------

    def start(self) -> None:
        """Launch the server and wait until it will answer. Raises rather than half-starting."""
        if not self.binary_path.is_file():
            raise LlamaServerError(f"the llama.cpp server binary is not where the host said it was: {self.binary_path.name}")
        if not self.model_path.is_file():
            raise LlamaServerError(f"the summary model file is not where the host said it was: {self.model_path.name}")

        self._port = _free_port()

        command = [
            str(self.binary_path),
            "-m", str(self.model_path),
            "-c", str(self.profile.context_tokens),
            "-ngl", str(self.profile.gpu_layers),
            "--host", "127.0.0.1",
            "--port", str(self._port),
            # One slot. Summarisation is one job at a time by design, and a second slot would
            # divide the context window without anything ever using it.
            "-np", "1",
            "--cache-type-k", self.profile.cache_type,
            "--cache-type-v", self.profile.cache_type,
            "--no-webui",
            "--no-warmup",
            # Thinking off, twice over, because the model card's account of it is not what
            # llama.cpp actually does. The card says reasoning is *triggered* by a <|think|>
            # token at the start of the system prompt; the chat template baked into this GGUF
            # turns it on regardless. Measured, not assumed: with reasoning left at its default
            # the model spent the entire reply budget in `reasoning_content` and returned an
            # empty message, so every extraction failed as "ran out of room". The plan excludes
            # reasoning mode, a thinking block would break the JSON grammar anyway, and the
            # budget is pinned to 0 as well as the switch to off so that a future template
            # change cannot quietly re-enable it.
            "--reasoning", "off",
            "--reasoning-budget", "0",
        ]

        # Offline, belt and braces: llama.cpp will happily fetch a model from Hugging Face if it
        # is asked to, and nothing here should ever be able to.
        environment = dict(os.environ)
        environment["LLAMA_OFFLINE"] = "1"
        environment["HF_HUB_OFFLINE"] = "1"
        environment["NO_PROXY"] = "*"

        try:
            self._process = subprocess.Popen(
                command,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.PIPE,
                stdin=subprocess.DEVNULL,
                env=environment,
                # The binary loads its backends from DLLs beside itself.
                cwd=str(self.binary_path.parent),
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
        except OSError as error:
            raise LlamaServerError(f"the summary runtime could not be started: {type(error).__name__}") from error

        self._reader = threading.Thread(target=self._drain_stderr, name="echoforge-llama-stderr", daemon=True)
        self._reader.start()

        self._await_ready()

    def _await_ready(self) -> None:
        deadline = time.monotonic() + self.startup_timeout

        while time.monotonic() < deadline:
            if self._is_cancelled():
                self.stop()
                raise Cancelled()

            code = self._process.poll() if self._process else None
            if code is not None:
                captured = self.stderr_tail()
                raise LlamaServerError(
                    f"the summary runtime stopped while loading the model (exit {code})",
                    out_of_memory=_looks_like_oom(captured),
                )

            try:
                with urllib.request.urlopen(f"http://127.0.0.1:{self._port}/health", timeout=3) as response:
                    if response.status == 200 and json.loads(response.read()).get("status") == "ok":
                        self._read_actual_context()
                        return
            except (urllib.error.URLError, OSError, ValueError, json.JSONDecodeError):
                pass

            time.sleep(0.25)

        captured = self.stderr_tail()
        self.stop()
        raise LlamaServerError(
            "the summary runtime did not become ready in time",
            out_of_memory=_looks_like_oom(captured),
        )

    def _read_actual_context(self) -> None:
        """Ask the server what context it actually gave us, rather than assuming it obeyed."""
        try:
            properties = self._get("/props")
        except LlamaServerError:
            self._actual_context = self.profile.context_tokens
            return

        settings = properties.get("default_generation_settings") or {}
        actual = settings.get("n_ctx")
        self._actual_context = int(actual) if isinstance(actual, int) and actual > 0 else self.profile.context_tokens

    @property
    def context_tokens(self) -> int:
        """The context the server is actually running with."""
        return self._actual_context or self.profile.context_tokens

    def stop(self) -> None:
        """Terminate the server, then make sure. Safe to call more than once."""
        process = self._process
        self._process = None

        if process is None:
            return

        if process.poll() is None:
            try:
                process.terminate()
            except OSError:
                pass

            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                try:
                    process.kill()
                except OSError:
                    pass
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    # The Job Object is the backstop, and it does not negotiate.
                    pass

        if process.stderr is not None:
            try:
                process.stderr.close()
            except OSError:
                pass

    def __enter__(self) -> LlamaServer:
        self.start()
        return self

    def __exit__(self, *_: object) -> None:
        self.stop()

    @property
    def is_running(self) -> bool:
        return self._process is not None and self._process.poll() is None

    @property
    def port(self) -> int:
        return self._port

    def stderr_tail(self) -> str:
        return "\n".join(self._stderr)

    def _drain_stderr(self) -> None:
        process = self._process
        if process is None or process.stderr is None:
            return

        try:
            for raw in iter(process.stderr.readline, b""):
                self._stderr.append(raw.decode("utf-8", errors="replace").rstrip())
        except (OSError, ValueError):
            return

    # -- talking to it -------------------------------------------------------------------

    def token_count(self, text: str) -> int:
        """Exact token count, from the tokenizer inside the pinned GGUF.

        This is the whole reason the budget is trustworthy: it is not an estimate of what the
        model will see, it is what the model's own tokenizer produces for the same bytes.
        """
        result = self._post("/tokenize", {"content": text}, timeout=60)
        tokens = result.get("tokens")
        if not isinstance(tokens, list):
            raise LlamaServerError("the summary runtime returned a tokenization this build cannot read")
        return len(tokens)

    def generate_json(
        self,
        system: str,
        user: str,
        schema: dict[str, Any],
        max_tokens: int,
    ) -> str:
        """One schema-constrained generation. Returns the raw text; parsing is the caller's.

        The schema constrains the *shape*. It cannot constrain the truth of what is inside it -
        a grammar will happily produce a perfectly formed citation to a segment that does not
        exist - which is why this returns text for the host's validator to judge rather than
        anything resembling an approved result.
        """
        payload = {
            "messages": [
                {"role": "system", "content": system},
                {"role": "user", "content": user},
            ],
            # Greedy and seeded. A summary that changes between two runs over one transcript
            # cannot be reviewed against its own evidence.
            "temperature": 0.0,
            "top_k": 1,
            "seed": self.seed,
            "max_tokens": max_tokens,
            "stream": False,
            "response_format": {
                "type": "json_schema",
                "json_schema": {"name": "echoforge_extraction", "schema": schema, "strict": True},
            },
        }

        result = self._post("/v1/chat/completions", payload, timeout=self.generation_timeout)

        try:
            choice = result["choices"][0]
            content = choice["message"]["content"]
        except (KeyError, IndexError, TypeError) as error:
            raise LlamaServerError("the summary runtime returned a response this build cannot read") from error

        if choice.get("finish_reason") == "length":
            # Say so rather than letting a half-object reach the parser as if it were an answer.
            raise LlamaServerError("the summary runtime ran out of room before finishing its answer")

        return content if isinstance(content, str) else ""

    def _get(self, path: str, timeout: float = 30) -> dict[str, Any]:
        return self._request(urllib.request.Request(f"http://127.0.0.1:{self._port}{path}"), timeout)

    def _post(self, path: str, payload: dict[str, Any], timeout: float) -> dict[str, Any]:
        request = urllib.request.Request(
            f"http://127.0.0.1:{self._port}{path}",
            data=json.dumps(payload).encode("utf-8"),
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        return self._request(request, timeout)

    def _request(self, request: urllib.request.Request, timeout: float) -> dict[str, Any]:
        if self._is_cancelled():
            raise Cancelled()

        if not self.is_running:
            raise LlamaServerError(
                "the summary runtime stopped before it answered",
                out_of_memory=_looks_like_oom(self.stderr_tail()),
            )

        try:
            with urllib.request.urlopen(request, timeout=timeout) as response:
                body = response.read()
        except urllib.error.HTTPError as error:
            raise LlamaServerError(f"the summary runtime refused the request (HTTP {error.code})") from error
        except (urllib.error.URLError, OSError) as error:
            if self._is_cancelled():
                raise Cancelled() from error
            raise LlamaServerError(
                "the summary runtime stopped responding",
                out_of_memory=_looks_like_oom(self.stderr_tail()),
            ) from error

        try:
            parsed = json.loads(body)
        except json.JSONDecodeError as error:
            raise LlamaServerError("the summary runtime returned something that was not a response") from error

        if not isinstance(parsed, dict):
            raise LlamaServerError("the summary runtime returned something that was not a response")

        return parsed

    def _is_cancelled(self) -> bool:
        return self.cancelled is not None and self.cancelled()


def _free_port() -> int:
    """Take a loopback port by binding it, then hand the number over.

    There is an unavoidable gap between closing this socket and llama-server binding the same
    number. It is narrow, and the alternative - a fixed port - collides with a previous run of
    EchoForge rather than with a random stranger, which is worse and far more likely. A collision
    surfaces as a startup failure, which the caller already retries down the ladder.
    """
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        probe.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        probe.bind(("127.0.0.1", 0))
        return int(probe.getsockname()[1])


def _looks_like_oom(captured: str) -> bool:
    lowered = captured.casefold()
    return any(marker in lowered for marker in _OOM_MARKERS)


def start_with_fallback(
    binary_path: Path,
    model_path: Path,
    ladder: Sequence[LlamaProfile],
    seed: int = 7,
    cancelled: Callable[[], bool] | None = None,
    on_fallback: Callable[[LlamaProfile, LlamaProfile, str], None] | None = None,
    startup_timeout: float = STARTUP_TIMEOUT_SECONDS,
) -> LlamaServer:
    """Start the most capable profile that this machine will actually run.

    Every step down is reported through ``on_fallback`` and ends up recorded against the summary
    revision. A quietly reduced context would show up later as a summary that was chunked more
    finely than the settings say, with nothing anywhere explaining why.
    """
    if not ladder:
        raise LlamaServerError("no summary runtime profiles were offered")

    attempts: list[str] = []

    for index, profile in enumerate(ladder):
        server = LlamaServer(
            binary_path=binary_path,
            model_path=model_path,
            profile=profile,
            seed=seed,
            cancelled=cancelled,
            startup_timeout=startup_timeout,
        )

        try:
            server.start()
            return server
        except Cancelled:
            server.stop()
            raise
        except LlamaServerError as error:
            server.stop()
            attempts.append(f"{profile.name}: {error.detail}")

            remaining = index + 1 < len(ladder)
            if not remaining:
                raise LlamaServerError(
                    "the summary model would not load on this machine at any supported size. "
                    + "; ".join(attempts)
                ) from error

            if on_fallback is not None:
                on_fallback(profile, ladder[index + 1], error.detail)

    raise LlamaServerError("the summary runtime could not be started")


def failure_from(error: LlamaServerError, stage: str = Stage.PREPARING) -> WorkerFailure:
    """Turn a runtime problem into the worker's own failure vocabulary.

    There is deliberately no ``out_of_memory`` error code. Running out of memory is not something
    the host has to handle as a distinct outcome: the ladder above has already tried every smaller
    profile by the time anything is reported, so what reaches the host is "the model would not run
    here at all", which is ``backend_unavailable``. The memory detail rides along in the diagnostic
    text for the log.
    """
    return WorkerFailure(ErrorCode.BACKEND_UNAVAILABLE, stage, error.detail)
