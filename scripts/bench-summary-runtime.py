"""Exercise the fallback ladder and the KV-cache choice on real hardware.

Two things the architecture asserts and nothing had yet demonstrated: that every rung of the
fallback ladder actually starts on this machine, and that Q8 is the right KV cache to have started
from. Both are measured here rather than argued about.

**Nothing is forced to fail.** The lower rungs are started directly rather than by exhausting the
GPU, because deliberately provoking an out-of-memory condition on the machine somebody is using is
a bad way to learn something a direct start tells you just as well. What this proves is that each
tier loads, answers, reports the context it actually got, and leaves no process behind. What it
does not prove is the transition *between* tiers under real memory pressure; that path is unit
tested with an injected failure, and is called out rather than implied.

    python scripts/bench-summary-runtime.py --ladder
    python scripts/bench-summary-runtime.py --kv
    python scripts/bench-summary-runtime.py --model ministral-3-14b --ladder
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "worker"))

from echoforge_worker.llama_server import (  # noqa: E402
    DEFAULT_LADDER,
    LlamaProfile,
    LlamaServer,
    LlamaServerError,
)
from echoforge_worker.local_summary import (  # noqa: E402
    LocalSummaryBackend,
    extraction_schema,
    load_prompt,
    render_segments,
)
from echoforge_worker.measurements import RunMeasurements, process_vram_bytes  # noqa: E402
from echoforge_worker.model_profiles import GEMMA_4_12B, MINISTRAL_3_14B  # noqa: E402
from echoforge_worker.summarize import TranscriptSegment  # noqa: E402

LOCAL = Path(os.environ["LOCALAPPDATA"])

MODELS = {
    GEMMA_4_12B.backend: (
        GEMMA_4_12B,
        "summary-cuda-q4",
        LOCAL / "EchoForge/models/summary.gemma-4-12b-it-qat-q4-0/29d097773436b69ff9feafd636ab4cf873786537/gemma-4-12b-it-qat-q4_0.gguf",
    ),
    MINISTRAL_3_14B.backend: (
        MINISTRAL_3_14B,
        "summary-bakeoff",
        LOCAL / "EchoForge/models/summary.ministral-3-14b-instruct-2512-q4-k-m/74fac473c43357d7fb2671713608183cc72496d0/Ministral-3-14B-Instruct-2512-Q4_K_M.gguf",
    ),
}

SEGMENTS = [
    TranscriptSegment("segment-000001", "microphone", "You", "Right, let's settle the release date today.", 0.0, 8.0),
    TranscriptSegment("segment-000002", "system", "Remote", "We will ship the beta on Friday.", 12.0, 20.0),
    TranscriptSegment("segment-000003", "system", "Remote", "Alex will prepare the release notes by 2026-08-14.", 24.0, 32.0),
]


class Request:
    infer_owners = False
    infer_due_dates = False
    meeting_date = "2026-08-07"


def surviving_servers() -> int:
    """How many llama-server processes are running. Should always be zero between tiers."""
    try:
        completed = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq llama-server.exe", "/NH"],
            capture_output=True, text=True, timeout=30,
        )
    except (OSError, subprocess.SubprocessError):
        return -1

    return sum(1 for line in completed.stdout.splitlines() if "llama-server" in line.lower())


def run_tier(binary: Path, model: Path, profile: LlamaProfile, model_args, timeout: float) -> dict:
    """Start one tier, ask it one real question, and make sure it is gone afterwards."""
    result: dict = {
        "tier": profile.name,
        "requested_context": profile.context_tokens,
        "requested_gpu_layers": profile.gpu_layers,
        "kv_cache": profile.cache_type,
    }

    server = LlamaServer(
        binary_path=binary,
        model_path=model,
        profile=profile,
        model_args=tuple(model_args),
        startup_timeout=timeout,
    )

    started = time.perf_counter()
    try:
        server.start()
    except LlamaServerError as error:
        result.update(started_ok=False, detail=error.detail, out_of_memory=error.out_of_memory)
        server.stop()
        result["survivors_after"] = surviving_servers()
        return result

    result["load_seconds"] = round(time.perf_counter() - started, 2)
    result["started_ok"] = True
    result["actual_context"] = server.context_tokens

    measurements = RunMeasurements()
    backend = LocalSummaryBackend(server, profile=None, model_revision="bench")
    backend.measurements = measurements

    try:
        generation_started = time.perf_counter()
        raw = server.generate_json(
            system=load_prompt("extract-v1"),
            user=render_segments(SEGMENTS),
            schema=extraction_schema(),
            max_tokens=1024,
            on_response=measurements.record_response,
        )
        result["generation_seconds"] = round(time.perf_counter() - generation_started, 2)
        result["answered"] = raw.strip().startswith("{")
        result["prompt_tokens"] = measurements.prompt_tokens
        result["completion_tokens"] = measurements.completion_tokens
        result["prompt_tokens_per_second"] = round(measurements.prompt_tokens_per_second, 1)
        result["generation_tokens_per_second"] = round(measurements.generation_tokens_per_second, 1)

        vram, source = process_vram_bytes(server.pid)
        result["peak_vram_bytes"] = vram
        result["vram_source"] = source

        # The evidence rules do not relax for a benchmark: an answer that cites a segment this
        # slice did not contain is still discarded, and a tier that only produces those has not
        # really answered.
        candidates = backend.extract(type("Chunk", (), {"index": 0})(), SEGMENTS, Request())
        result["valid_candidates"] = len(candidates)
    except (LlamaServerError, Exception) as error:  # noqa: BLE001 - a benchmark reports, never raises
        result["answered"] = False
        result["detail"] = f"{type(error).__name__}: {error}"
    finally:
        server.stop()

    result["survivors_after"] = surviving_servers()
    return result


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", default=GEMMA_4_12B.backend, choices=sorted(MODELS))
    parser.add_argument("--ladder", action="store_true", help="start every fallback tier in turn")
    parser.add_argument("--kv", action="store_true", help="compare Q8 and Q4 KV cache")
    parser.add_argument("--cpu-timeout", type=float, default=420.0, help="seconds to allow the CPU tier to load")
    parser.add_argument("--out", default="artifacts/evaluation/runtime-bench.json")
    arguments = parser.parse_args()

    profile, runtime_profile, model = MODELS[arguments.model]
    binary = LOCAL / "EchoForge" / "models" / "summary-runtime" / runtime_profile / "llama-server.exe"

    if not binary.is_file() or not model.is_file():
        print(f"missing runtime or model for {arguments.model}.")
        print(f"  dotnet run scripts/fetch-artifacts.cs -- --profile {runtime_profile}")
        return 2

    report: dict = {
        "model": arguments.model,
        "model_id": profile.model_id,
        "quantization": profile.quantization,
        "survivors_before": surviving_servers(),
        "ladder": [],
        "kv_cache": [],
    }

    if arguments.ladder:
        print(f"== fallback ladder, {profile.display_name} ==")
        for tier in DEFAULT_LADDER:
            # The CPU rung of a 12-14B model is genuinely slow. What is being proven here is that
            # the path works and the process is cleaned up, not that anybody should wait for it.
            timeout = arguments.cpu_timeout if not tier.uses_gpu else 300.0
            print(f"  {tier.name:18} {tier.description} ... ", end="", flush=True)
            outcome = run_tier(binary, model, tier, profile.server_args, timeout)
            report["ladder"].append(outcome)
            print(
                f"{'started' if outcome.get('started_ok') else 'DID NOT START'}"
                f"  ctx={outcome.get('actual_context', '-')}"
                f"  load={outcome.get('load_seconds', '-')}s"
                f"  tg={outcome.get('generation_tokens_per_second', '-')}/s"
                f"  survivors={outcome.get('survivors_after')}"
            )

    if arguments.kv:
        print(f"== KV cache, {profile.display_name}, otherwise identical ==")
        for cache in ("q8_0", "q4_0"):
            tier = LlamaProfile(f"cuda-32k-{cache}", 32768, 99, cache, f"GPU, 32K context, {cache} KV cache")
            print(f"  {cache:8} ... ", end="", flush=True)
            outcome = run_tier(binary, model, tier, profile.server_args, 300.0)
            report["kv_cache"].append(outcome)
            print(
                f"{'ok' if outcome.get('answered') else 'FAILED'}"
                f"  vram={outcome.get('peak_vram_bytes', 0) / 1e9:.2f}GB"
                f"  tg={outcome.get('generation_tokens_per_second', '-')}/s"
                f"  valid_items={outcome.get('valid_candidates', '-')}"
            )

    report["survivors_after"] = surviving_servers()

    destination = Path(arguments.out)
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(report, indent=2), encoding="utf-8")

    print()
    print(f"no llama-server survived: {report['survivors_after'] == 0}")
    print(f"written: {destination}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
