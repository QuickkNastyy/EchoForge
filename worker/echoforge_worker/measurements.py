"""What one summary run cost, measured while it happens.

**This file must never see meeting content, and it never does.** Everything here is a duration, a
count, an identity or a byte figure. Telemetry gets written to report files, quoted in
comparisons, and read by people who were not in the meeting; a transcript line reaching one of
those is a privacy failure, and the error and measurement paths are exactly where "just a bit of
helpful context" gets added without anyone noticing. There is deliberately no field that could
hold a sentence from a transcript or a summary.

The numbers come from llama.cpp's own ``timings`` and ``usage`` blocks rather than from wall-clock
arithmetic around them, because prompt processing and generation have very different speeds and a
single elapsed time hides which one a model was slow at. Peak VRAM is asked of NVIDIA for the
server process specifically, not for the card, so a browser with a video open does not become part
of the measurement.
"""

from __future__ import annotations

import json
import os
import subprocess
import time
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any


@dataclass
class StageTimer:
    """Accumulates time spent in one stage across many calls."""

    seconds: float = 0.0
    _started: float | None = None

    def start(self) -> None:
        self._started = time.perf_counter()

    def stop(self) -> None:
        if self._started is not None:
            self.seconds += time.perf_counter() - self._started
            self._started = None


@dataclass
class RunMeasurements:
    """One run's telemetry. Identities, counts and durations only."""

    backend: str = ""
    model_id: str = ""
    model_revision: str = ""
    quantization: str = ""
    llama_version: str = ""
    prompt_version: str = ""

    requested_context: int = 0
    actual_context: int = 0
    requested_gpu_layers: int = 0
    kv_cache_type: str = ""
    runtime_tier: str = ""
    fell_back: bool = False
    fallback_steps: list[str] = field(default_factory=list)
    used_cpu_only: bool = False
    oom_retries: int = 0

    total_seconds: float = 0.0
    model_load_seconds: float = 0.0
    extraction_seconds: float = 0.0
    synthesis_seconds: float = 0.0
    repair_seconds: float = 0.0

    prompt_tokens: int = 0
    completion_tokens: int = 0
    #: Weighted by tokens rather than averaged across calls, so one tiny request cannot dominate.
    prompt_ms: float = 0.0
    generation_ms: float = 0.0

    peak_vram_bytes: int = 0
    vram_source: str = "unavailable"

    repair_attempts: int = 0
    synthesis_levels: int = 0
    chunks: int = 0

    @property
    def prompt_tokens_per_second(self) -> float:
        return (self.prompt_tokens / (self.prompt_ms / 1000.0)) if self.prompt_ms > 0 else 0.0

    @property
    def generation_tokens_per_second(self) -> float:
        return (self.completion_tokens / (self.generation_ms / 1000.0)) if self.generation_ms > 0 else 0.0

    def record_response(self, response: dict[str, Any]) -> None:
        """Fold one completion's own accounting in.

        llama.cpp reports what it actually did, which is worth more than timing the HTTP call:
        the durations here exclude request overhead and separate prompt evaluation from decoding.
        """
        usage = response.get("usage") or {}
        self.prompt_tokens += int(usage.get("prompt_tokens") or 0)
        self.completion_tokens += int(usage.get("completion_tokens") or 0)

        timings = response.get("timings") or {}
        self.prompt_ms += float(timings.get("prompt_ms") or 0.0)
        self.generation_ms += float(timings.get("predicted_ms") or 0.0)

    def note_fallback(self, tried: str, detail: str, out_of_memory: bool) -> None:
        self.fell_back = True
        self.fallback_steps.append(f"{tried}: {detail}")
        if out_of_memory:
            self.oom_retries += 1

    def to_json(self) -> dict[str, Any]:
        payload = asdict(self)
        payload.pop("prompt_ms", None)
        payload.pop("generation_ms", None)
        payload["prompt_tokens_per_second"] = round(self.prompt_tokens_per_second, 3)
        payload["generation_tokens_per_second"] = round(self.generation_tokens_per_second, 3)

        for key in ("total_seconds", "model_load_seconds", "extraction_seconds", "synthesis_seconds", "repair_seconds"):
            payload[key] = round(payload[key], 4)

        return payload


def telemetry_path(output_path: str) -> Path:
    """Beside the summary, never inside it. Prose and measurements are different things."""
    destination = Path(output_path)
    return destination.with_name(destination.name + ".telemetry.json")


def write_telemetry(output_path: str, measurements: RunMeasurements) -> Path | None:
    """Write the sidecar. A failure here never fails the summary that was produced."""
    path = telemetry_path(output_path)
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(measurements.to_json(), indent=2), encoding="utf-8")
        return path
    except OSError:
        return None


def process_vram_bytes(pid: int | None) -> tuple[int, str]:
    """Peak VRAM for one process, asked of NVIDIA.

    Scoped to the server's own PID rather than the whole card, because whatever else is on the GPU
    is not what the bake-off is measuring - and on a desktop there is always something else.
    Returns ``(0, "unavailable")`` rather than guessing when nvidia-smi is absent or says nothing.
    """
    if pid is None:
        return 0, "unavailable"

    try:
        completed = subprocess.run(
            [
                "nvidia-smi",
                "--query-compute-apps=pid,used_gpu_memory",
                "--format=csv,noheader,nounits",
            ],
            capture_output=True,
            text=True,
            timeout=15,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except (OSError, subprocess.SubprocessError):
        return 0, "unavailable"

    if completed.returncode != 0:
        return 0, "unavailable"

    for line in completed.stdout.splitlines():
        parts = [part.strip() for part in line.split(",")]
        if len(parts) < 2:
            continue
        try:
            if int(parts[0]) == pid:
                return int(float(parts[1])) * 1024 * 1024, "nvidia-smi:compute-apps"
        except ValueError:
            continue

    return _device_vram_bytes()


def _device_vram_bytes() -> tuple[int, str]:
    """Whole-card usage, when per-process usage cannot be had.

    Windows in WDDM mode does not report per-process compute memory through nvidia-smi, which is
    exactly the machine this runs on. The device figure includes the desktop and whatever else is
    resident, so it is an upper bound rather than the model's footprint - and it is labelled as
    one, because a number that overstates by an unknown amount is only useful if nobody mistakes
    it for a measurement of the model.
    """
    try:
        completed = subprocess.run(
            ["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"],
            capture_output=True,
            text=True,
            timeout=15,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except (OSError, subprocess.SubprocessError):
        return 0, "unavailable"

    if completed.returncode != 0:
        return 0, "unavailable"

    for line in completed.stdout.splitlines():
        try:
            return int(float(line.strip())) * 1024 * 1024, "nvidia-smi:device-total (includes other processes)"
        except ValueError:
            continue

    return 0, "unavailable"


def llama_version(binary_path: str) -> str:
    """The runtime's own version string, so a report names the build that produced it."""
    try:
        completed = subprocess.run(
            [binary_path, "--version"],
            capture_output=True,
            text=True,
            timeout=30,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            cwd=str(Path(binary_path).parent),
        )
    except (OSError, subprocess.SubprocessError):
        return ""

    for stream in (completed.stderr or "", completed.stdout or ""):
        for line in stream.splitlines():
            if "version:" in line:
                return line.strip()

    return ""
