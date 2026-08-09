"""Telemetry and model profiles: what gets measured, and what must never be measured with it.

The privacy test in here is not decoration. Telemetry is written to report files, quoted in
comparisons and read by people who were not in the meeting, and the measurement path is exactly
where somebody adds "just a bit of helpful context" without thinking about where it ends up.
"""

from __future__ import annotations

import json

from echoforge_worker.measurements import (
    RunMeasurements,
    process_vram_bytes,
    telemetry_path,
    write_telemetry,
)
from echoforge_worker.model_profiles import (
    GEMMA_4_12B,
    GPT_OSS_20B,
    MINISTRAL_3_14B,
    available_model_profiles,
    is_local_model,
    resolve_model_profile,
)

# -- model profiles ---------------------------------------------------------------------------


def test_both_bake_off_candidates_are_resolvable() -> None:
    assert available_model_profiles() == ["gemma-4-12b", "gpt-oss-20b", "ministral-3-14b"]
    assert resolve_model_profile("gemma-4-12b") is GEMMA_4_12B
    assert resolve_model_profile("gpt-oss-20b") is GPT_OSS_20B
    assert resolve_model_profile("ministral-3-14b") is MINISTRAL_3_14B


def test_the_placeholder_is_not_a_local_model() -> None:
    assert not is_local_model("mock-summary")
    assert resolve_model_profile("mock-summary") is None


def test_only_one_candidate_may_be_the_default() -> None:
    # A bake-off candidate is something EchoForge measures. Installing it must never be a way for
    # it to become the summariser.
    assert GEMMA_4_12B.is_default_candidate
    assert not GPT_OSS_20B.is_default_candidate
    assert not MINISTRAL_3_14B.is_default_candidate


def test_gemma_pins_reasoning_off_and_ministral_needs_nothing() -> None:
    assert "--reasoning" in GEMMA_4_12B.server_args
    assert "off" in GEMMA_4_12B.server_args
    assert "--reasoning-budget" in GEMMA_4_12B.server_args

    # Ministral has no thinking mode to disable. Recorded explicitly so "not required" stays
    # distinguishable from "forgotten" - Gemma's default turned out to be the opposite of what
    # its own model card described.
    assert MINISTRAL_3_14B.server_args == ()
    assert MINISTRAL_3_14B.reasoning_note


def test_gpt_oss_pins_harmony_and_bounds_private_reasoning() -> None:
    assert GPT_OSS_20B.model_id == "gpt-oss-20b-mxfp4"
    assert GPT_OSS_20B.quantization == "MXFP4"
    assert "--jinja" in GPT_OSS_20B.server_args
    assert "--reasoning-budget" in GPT_OSS_20B.server_args
    assert '{"reasoning_effort":"low"}' in GPT_OSS_20B.server_args
    assert "final content" in GPT_OSS_20B.reasoning_note.casefold()


def test_every_profile_records_its_quantization_and_identity() -> None:
    for profile in (GEMMA_4_12B, GPT_OSS_20B, MINISTRAL_3_14B):
        assert profile.backend and profile.model_id and profile.display_name
        assert profile.quantization
        assert profile.reasoning_note


def test_a_profile_cannot_relax_what_an_answer_must_satisfy() -> None:
    # There is deliberately no field for loosening validation, adjusting evidence rules or
    # lowering a threshold. A model that needs those to pass has not passed.
    forbidden = {"threshold", "relax", "skip_validation", "allow", "lenient", "evidence"}
    for name in GEMMA_4_12B.__dataclass_fields__:
        assert not any(word in name.casefold() for word in forbidden), name


# -- measurements -----------------------------------------------------------------------------


def test_llama_cpp_accounting_is_folded_in_rather_than_timed_from_outside() -> None:
    measurements = RunMeasurements()

    measurements.record_response({
        "usage": {"prompt_tokens": 1000, "completion_tokens": 200},
        "timings": {"prompt_ms": 500.0, "predicted_ms": 4000.0},
    })
    measurements.record_response({
        "usage": {"prompt_tokens": 500, "completion_tokens": 100},
        "timings": {"prompt_ms": 250.0, "predicted_ms": 2000.0},
    })

    assert measurements.prompt_tokens == 1500
    assert measurements.completion_tokens == 300

    # Prompt evaluation and decoding have very different speeds, and one elapsed time hides which
    # one a model was slow at.
    assert measurements.prompt_tokens_per_second == 2000.0
    assert measurements.generation_tokens_per_second == 50.0


def test_a_response_that_reports_nothing_does_not_produce_a_divide_by_zero() -> None:
    measurements = RunMeasurements()
    measurements.record_response({})

    assert measurements.prompt_tokens_per_second == 0.0
    assert measurements.generation_tokens_per_second == 0.0


def test_every_fallback_is_recorded_and_memory_ones_are_counted() -> None:
    measurements = RunMeasurements()

    measurements.note_fallback("cuda-32k", "would not allocate", out_of_memory=True)
    measurements.note_fallback("cuda-16k", "port collision", out_of_memory=False)

    assert measurements.fell_back
    assert measurements.oom_retries == 1
    assert len(measurements.fallback_steps) == 2
    assert "cuda-32k" in measurements.fallback_steps[0]


def test_the_tier_that_actually_ran_is_recorded_not_the_one_asked_for() -> None:
    measurements = RunMeasurements(requested_context=32768, actual_context=8192, runtime_tier="cuda-8k")

    payload = measurements.to_json()

    assert payload["requested_context"] == 32768
    assert payload["actual_context"] == 8192
    assert payload["runtime_tier"] == "cuda-8k"


def test_the_kv_cache_choice_is_part_of_the_record() -> None:
    assert RunMeasurements(kv_cache_type="q4_0").to_json()["kv_cache_type"] == "q4_0"


def test_telemetry_carries_no_transcript_or_summary_text() -> None:
    measurements = RunMeasurements(
        backend="gemma-4-12b",
        model_id="gemma-4-12b-it-qat-q4_0",
        quantization="Q4_0",
        llama_version="version: 10298 (15586e2d7)",
        prompt_version="meeting-summary-v1",
        kv_cache_type="q8_0",
        runtime_tier="cuda-32k",
        vram_source="nvidia-smi:device-total",
        prompt_tokens=1000,
        completion_tokens=200,
    )
    measurements.note_fallback("cuda-32k", "would not allocate", out_of_memory=True)

    payload = measurements.to_json()

    # Every string in the record must be an identity chosen by EchoForge, never text that came
    # out of a meeting or out of a model.
    allowed_text_keys = {
        "backend", "model_id", "model_revision", "quantization", "llama_version",
        "prompt_version", "kv_cache_type", "runtime_tier", "vram_source",
    }

    for key, value in payload.items():
        if isinstance(value, str):
            assert key in allowed_text_keys, key
        elif isinstance(value, list):
            assert key == "fallback_steps", key


def test_the_sidecar_sits_beside_the_summary_and_not_inside_it(tmp_path) -> None:
    output = tmp_path / "summary.v1.json"
    measurements = RunMeasurements(backend="gemma-4-12b", total_seconds=12.3456789)

    path = write_telemetry(str(output), measurements)

    assert path == telemetry_path(str(output))
    assert path.name == "summary.v1.json.telemetry.json"

    # Prose and measurements are different things with different readers. The summary file is
    # untouched by writing telemetry.
    assert not output.exists()

    written = json.loads(path.read_text(encoding="utf-8"))
    assert written["backend"] == "gemma-4-12b"
    assert written["total_seconds"] == 12.3457


def test_losing_the_sidecar_never_fails_the_summary(tmp_path) -> None:
    # A directory where the file should be. Writing telemetry must not be able to fail a run
    # whose summary was already produced and validated.
    blocked = tmp_path / "summary.v1.json.telemetry.json"
    blocked.mkdir(parents=True)

    assert write_telemetry(str(tmp_path / "summary.v1.json"), RunMeasurements()) is None


def test_vram_reports_unavailable_rather_than_guessing() -> None:
    # No process, no measurement. A fabricated figure would be quoted in a comparison.
    assert process_vram_bytes(None) == (0, "unavailable")


def test_a_vram_figure_always_says_where_it_came_from() -> None:
    measured, source = process_vram_bytes(-1)

    # Whatever this machine supports, the number and its provenance travel together: a
    # whole-device figure overstates the model's footprint by an unknown amount, and is only
    # useful if nobody can mistake it for a measurement of the model.
    assert source in {"unavailable", "nvidia-smi:compute-apps", "nvidia-smi:device-total (includes other processes)"}
    assert measured >= 0
    if source == "unavailable":
        assert measured == 0
