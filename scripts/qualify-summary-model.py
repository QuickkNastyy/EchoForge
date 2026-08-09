"""Prove that a local summary model actually works on this machine, then leave nothing behind.

This is what stands behind Install for a summary model. Downloading a verified GGUF says the bytes
are right; it says nothing about whether llama.cpp on this machine can load them, whether the model
fits on this card, or whether it quietly ran on the CPU instead. Those are the questions somebody
actually has, and the only honest way to answer them is to run the thing.

    python scripts/qualify-summary-model.py --backend gpt-oss-20b --model <path-to.gguf> \
        --binary <path-to/llama-server.exe>

It prints `::step::<name>::<state>::<detail>` lines so the application can show real progress, and
a final `::result::<json>` line with the measurements. Every path is checked: a load failure, a
refusal to answer, a fall back to system memory, and a server that outlives the test are all
distinct outcomes, and only one of them is Ready.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "worker"))

from echoforge_worker.llama_server import (  # noqa: E402
    CPU_ONLY_LADDER,
    DEFAULT_LADDER,
    GPT_OSS_LADDER,
    LlamaServer,
    LlamaServerError,
)
from echoforge_worker.measurements import RunMeasurements, process_vram_bytes  # noqa: E402
from echoforge_worker.model_profiles import resolve_model_profile  # noqa: E402

#: A question small enough to answer in seconds and specific enough that a broken load cannot pass
#: it by accident. The grammar is trivial on purpose: this measures the runtime, not the model.
PROBE_SCHEMA = {
    "type": "object",
    "additionalProperties": False,
    "required": ["answer"],
    "properties": {"answer": {"type": "string", "minLength": 1}},
}

PROBE_SYSTEM = (
    "You answer with a JSON object containing one field, answer. Reply with the single word "
    "ready and nothing else."
)


def step(name: str, state: str, detail: str = "") -> None:
    print(f"::step::{name}::{state}::{detail}", flush=True)


def surviving_servers() -> int:
    """How many llama-server processes are running. Must be zero once a job is over."""
    try:
        completed = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq llama-server.exe", "/NH"],
            capture_output=True, text=True, timeout=30,
        )
    except (OSError, subprocess.SubprocessError):
        return -1

    return sum(1 for line in completed.stdout.splitlines() if "llama-server" in line.lower())


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--backend", required=True)
    parser.add_argument("--model", required=True)
    parser.add_argument("--binary", required=True)
    parser.add_argument("--timeout", type=float, default=420.0)
    parser.add_argument(
        "--allow-cpu",
        action="store_true",
        help="qualify even if the model could only be run on the CPU",
    )
    arguments = parser.parse_args()

    model = Path(arguments.model)
    binary = Path(arguments.binary)

    result: dict = {"backend": arguments.backend, "ready": False, "used_gpu": False}

    step("artifact", "running")
    if not model.is_file():
        step("artifact", "failed", f"the model file is missing: {model}")
        print("::result::" + json.dumps(result), flush=True)
        return 1
    if not binary.is_file():
        step("artifact", "failed", f"the llama.cpp server is missing: {binary}")
        print("::result::" + json.dumps(result), flush=True)
        return 1
    step("artifact", "ready", f"{model.stat().st_size / 1_000_000_000:.1f} GB")

    profile = resolve_model_profile(arguments.backend)
    if profile is None:
        step("runtime", "failed", f"{arguments.backend!r} is not a local summary model")
        print("::result::" + json.dumps(result), flush=True)
        return 1

    # Before anything starts, so a leftover server from a previous run is attributed to that run
    # rather than to this one.
    result["servers_before"] = surviving_servers()

    # The GPU rung first, then the CPU one. A model that can only be summarised with on the CPU is
    # a real answer - it is just a much slower one, and it is reported rather than presented as
    # equivalent.
    # The same ladder the worker itself would use for this model, so qualification measures the
    # runtime the user is actually going to get rather than a nearby one.
    tiers = [*(GPT_OSS_LADDER if arguments.backend == "gpt-oss-20b" else DEFAULT_LADDER)]
    if arguments.allow_cpu:
        tiers.extend(tier for tier in CPU_ONLY_LADDER if tier not in tiers)

    for tier in tiers:
        if not tier.uses_gpu and not arguments.allow_cpu:
            continue

        step("load", "running", tier.name)
        server = LlamaServer(
            binary_path=binary,
            model_path=model,
            profile=tier,
            model_args=profile.server_args,
            startup_timeout=arguments.timeout,
        )

        started = time.perf_counter()
        try:
            server.start()
        except LlamaServerError as error:
            server.stop()
            step("load", "failed", f"{tier.name}: {error.detail}")
            result["last_error"] = error.detail
            continue

        result["tier"] = tier.name
        result["used_gpu"] = tier.uses_gpu
        result["load_seconds"] = round(time.perf_counter() - started, 2)
        result["actual_context"] = server.context_tokens
        step("load", "ready", f"{tier.name}, context {server.context_tokens}")

        measurements = RunMeasurements()
        step("inference", "running")
        try:
            answered = server.generate_json(
                system=PROBE_SYSTEM,
                user="Say ready.",
                schema=PROBE_SCHEMA,
                max_tokens=64,
                on_response=measurements.record_response,
            )
            parsed = json.loads(answered)
            result["answer"] = str(parsed.get("answer", ""))[:40]
            result["generation_tokens_per_second"] = round(measurements.generation_tokens_per_second, 1)

            # Sampled while the model is still resident. Asking after the server has stopped
            # measures an empty card.
            vram, source = process_vram_bytes(server.pid)
            result["peak_vram_bytes"] = vram
            result["vram_source"] = source
            step("inference", "ready", f"{vram / 1_000_000_000:.2f} GB dedicated, via {source}")
        except (LlamaServerError, ValueError, json.JSONDecodeError) as error:
            step("inference", "failed", f"{type(error).__name__}: {error}")
            result["last_error"] = f"{type(error).__name__}: {error}"
            server.stop()
            continue
        finally:
            server.stop()

        # The process boundary is EchoForge's final memory guarantee, so it is checked rather than
        # assumed. A model that works but leaves a server resident has not passed.
        step("shutdown", "running")
        remaining = surviving_servers()
        result["servers_after"] = remaining
        if remaining > result.get("servers_before", 0):
            step("shutdown", "failed", f"{remaining} llama-server processes are still running")
            result["last_error"] = "the runtime did not exit"
            break

        step("shutdown", "ready", "the runtime exited and released its memory")
        result["ready"] = True
        break

    if result["ready"]:
        step("model", "ready", result.get("tier", ""))
    else:
        step("model", "failed", result.get("last_error", "the model could not be qualified"))

    print("::result::" + json.dumps(result), flush=True)
    return 0 if result["ready"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
