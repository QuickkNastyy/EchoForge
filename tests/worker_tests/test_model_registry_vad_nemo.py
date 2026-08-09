from __future__ import annotations

import math
import sys
import types
from pathlib import Path

import pytest

from echoforge_worker import compute, nemo_backend
from echoforge_worker.asr_registry import MODELS, resolve_model, vad_strategy
from echoforge_worker.models import RequestOptions, RequestWindow, Segment
from echoforge_worker.nemo_backend import (
    QWEN_ARTIFACT_FILES,
    _canary_offline_layout,
    _deduplicate_canary,
    _timestamped_segments,
)
from echoforge_worker.protocol import WorkerFailure
from echoforge_worker.whisper_backend import _load_faster_whisper


REPO_ROOT = Path(__file__).resolve().parents[2]


def options(model_id: str, default_backend: str, **changes) -> RequestOptions:
    base = {
        "backend": default_backend,
        "model_id": model_id,
        "model_revision": MODELS[model_id].revision,
        "compute_profile": MODELS[model_id].compute_profiles[0],
        "vad_mode": MODELS[model_id].vad_modes[0],
        "window_strategy": MODELS[model_id].window_strategy,
        "window_seconds": MODELS[model_id].maximum_window_seconds,
        "language": "en" if MODELS[model_id].languages == ("en",) else "auto",
    }
    base.update(changes)
    return RequestOptions(**base)


def test_registry_exposes_all_pinned_models_and_honest_timestamps() -> None:
    assert tuple(MODELS) == (
        "mock-asr",
        "whisper-large-v3-turbo",
        "whisper-large-v3",
        "parakeet-unified-en-0.6b",
        "canary-qwen-2.5b",
    )
    assert MODELS["whisper-large-v3"].timestamp_precision == "word-native"
    assert MODELS["parakeet-unified-en-0.6b"].timestamp_capability == "word"
    assert MODELS["canary-qwen-2.5b"].timestamp_precision == "window-approximate"
    assert MODELS["canary-qwen-2.5b"].maximum_window_seconds == 40.0


def test_nemo_backend_is_advertised_only_for_the_exact_isolated_runtime(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setattr(nemo_backend.platform, "system", lambda: "Linux")
    monkeypatch.setattr(nemo_backend.importlib.util, "find_spec", lambda name: object())
    versions = {
        "nemo_toolkit": nemo_backend.EXPECTED_NEMO_VERSION,
        "torch": nemo_backend.EXPECTED_TORCH_VERSION,
    }
    monkeypatch.setattr(nemo_backend.importlib.metadata, "version", versions.__getitem__)

    assert nemo_backend.production_stack_available()

    versions["torch"] = "2.8.0+cu126"
    assert not nemo_backend.production_stack_available()


def test_isolated_nemo_runtime_has_a_complete_hash_lock() -> None:
    locked = (REPO_ROOT / "worker-nemo" / "requirements-production.txt").read_text(encoding="utf-8")
    packages = [line for line in locked.splitlines() if line and not line[0].isspace() and "==" in line]

    assert len(packages) == 148
    # 3.0.0, not the 2.7.3 the Parakeet model card names: the pinned checkpoint carries an
    # encoder option 2.7.3 does not accept and will not restore under it. Found by running
    # the model, which is why installation ends in a smoke test rather than a file check.
    assert "nemo-toolkit==3.0.0 \\" in locked
    assert "torch==2.8.0+cu128 \\" in locked
    assert locked.count("--hash=sha256:") >= len(packages)
    assert "git+" not in locked
    assert " @ http" not in locked


@pytest.mark.parametrize(
    ("change", "value"),
    [
        ("backend", "faster-whisper"),
        ("compute_profile", "cpu-int8"),
        ("vad_mode", "turbo"),
        ("window_strategy", "whisper-long-v2"),
        ("window_seconds", 41.0),
        ("language", "fr"),
        ("glossary", ("EchoForge",)),
    ],
)
def test_canary_refuses_unsupported_combinations_instead_of_falling_back(change: str, value) -> None:
    request = options("canary-qwen-2.5b", "nemo", **{change: value})
    with pytest.raises(WorkerFailure):
        resolve_model(request)


def test_accuracy_and_off_are_explicit_non_destructive_policies() -> None:
    for mode in ("accuracy", "off"):
        strategy = vad_strategy(mode)
        assert strategy.filter_audio is False
        assert strategy.parameters == {}


def test_balanced_and_fast_are_pinned_and_fast_is_more_aggressive() -> None:
    balanced = vad_strategy("balanced")
    fast = vad_strategy("fast")

    assert balanced.parameters == {
        "threshold": 0.35,
        "neg_threshold": 0.20,
        "min_speech_duration_ms": 80,
        "min_silence_duration_ms": 1000,
        "speech_pad_ms": 500,
    }
    assert fast.parameters == {
        "threshold": 0.50,
        "neg_threshold": 0.35,
        "min_speech_duration_ms": 200,
        "min_silence_duration_ms": 500,
        "speech_pad_ms": 200,
    }
    assert fast.parameters["threshold"] > balanced.parameters["threshold"]
    assert fast.parameters["speech_pad_ms"] < balanced.parameters["speech_pad_ms"]


@pytest.mark.parametrize("mode", ["accuracy", "off"])
def test_non_destructive_modes_pass_low_amplitude_short_speech_and_surrounding_silence_intact(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    mode: str,
) -> None:
    model_dir = tmp_path / "model"
    model_dir.mkdir()
    (model_dir / "model.bin").write_bytes(b"test")

    captured: dict = {}

    class Info:
        language = "en"
        language_probability = 1.0

    class FakeWhisperModel:
        def __init__(self, *args, **kwargs) -> None:
            captured["load"] = (args, kwargs)

        def transcribe(self, values, **kwargs):
            captured["values"] = values.copy()
            captured["kwargs"] = kwargs
            return iter(()), Info()

    module = types.ModuleType("faster_whisper")
    module.WhisperModel = FakeWhisperModel
    monkeypatch.setitem(sys.modules, "faster_whisper", module)

    class FakeArray(list):
        def __mul__(self, other):
            return FakeArray(left * right for left, right in zip(self, other, strict=True))

        def copy(self):
            return FakeArray(self)

    numpy = types.ModuleType("numpy")
    numpy.float32 = object()
    numpy.asarray = lambda values, dtype=None: FakeArray(values)
    numpy.mean = lambda values: sum(values) / len(values)
    monkeypatch.setitem(sys.modules, "numpy", numpy)

    request = options(
        "whisper-large-v3",
        "faster-whisper",
        model_path=str(model_dir),
        vad_mode=mode,
        compute_profile="cuda-fp16",
    )
    recognise = _load_faster_whisper(
        request,
        compute.ComputePlan("cuda-fp16", "cuda", "float16", 1),
    )
    # Long silence, a clipped low-amplitude "yes/no/Tuesday/I sent it"-sized burst, then silence.
    samples = [0.0] * 1000 + [0.0008, -0.0008] * 40 + [0.0] * 1000
    window = RequestWindow("w", "system", 1, 0, 0, len(samples), 0.0, len(samples) / 16000)

    result = recognise(samples, window, request)

    assert len(captured["values"]) == len(samples)
    assert math.isclose(captured["values"][1000], 0.0008)
    assert captured["kwargs"]["vad_filter"] is False
    assert captured["kwargs"]["vad_parameters"] is None
    assert math.isclose(result.audio_duration_seconds, len(samples) / 16000)
    assert result.vad_retained_seconds == result.audio_duration_seconds


def test_canary_overlap_dedup_preserves_short_responses() -> None:
    windows = (
        RequestWindow("a", "system", 1, 0, 0, 35 * 16000, 0, 35, 0, 5),
        RequestWindow("b", "system", 1, 1, 30 * 16000, 65 * 16000, 30, 65, 5, 0),
    )
    segments = [
        Segment("system", 1, 30, 35, "I sent it"),
        Segment("system", 1, 31, 36, "yeah"),
        Segment("system", 1, 32, 37, "no"),
        Segment("system", 1, 33, 38, "Tuesday"),
    ]

    kept = _deduplicate_canary(segments, windows)

    assert [segment.text for segment in kept] == ["I sent it", "yeah", "no", "Tuesday"]


def test_canary_does_not_fabricate_native_words_or_word_times() -> None:
    segments = _timestamped_segments("The remote speaker answered", raw=None, duration=35.0)

    assert len(segments) == 1
    segment = segments[0]
    assert segment.start_seconds == 0.0
    assert segment.end_seconds == 35.0
    assert segment.words == ()


def test_parakeet_preserves_native_word_times_when_the_runtime_returns_them() -> None:
    segments = _timestamped_segments(
        "I sent it",
        {
            "word": [
                {"word": "I", "start": 0.1, "end": 0.2},
                {"word": "sent", "start": 0.25, "end": 0.5},
                {"word": "it", "start": 0.55, "end": 0.7},
            ]
        },
        duration=1.0,
    )

    assert len(segments) == 1
    assert [word.text for word in segments[0].words] == ["I", "sent", "it"]
    assert segments[0].start_seconds == pytest.approx(0.1)
    assert segments[0].end_seconds == pytest.approx(0.7)


def test_canary_offline_layout_uses_only_verified_local_qwen_sidecars(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    verified = tmp_path / "verified"
    verified.mkdir()
    (verified / "config.json").write_text(
        '{"pretrained_llm":"Qwen/Qwen3-1.7B","pretrained_asr":"nvidia/canary-1b-flash"}',
        encoding="utf-8",
    )
    (verified / "model.safetensors").write_bytes(b"checkpoint")
    for staged_name in QWEN_ARTIFACT_FILES:
        (verified / staged_name).write_text(staged_name, encoding="utf-8")

    # The production runtime is Linux and creates links. Make this layout test independent of
    # Windows' developer-mode symlink policy while still asserting every link target is local.
    linked: dict[Path, Path] = {}

    def fake_symlink(path: Path, target: Path, *args, **kwargs) -> None:
        linked[path] = Path(target)
        path.write_text(str(target), encoding="utf-8")

    monkeypatch.setattr(Path, "symlink_to", fake_symlink)

    with _canary_offline_layout(verified) as offline:
        config = __import__("json").loads((offline / "config.json").read_text(encoding="utf-8"))
        qwen = Path(config["pretrained_llm"])
        assert qwen == offline / "qwen3-1.7b"
        assert set(path.name for path in qwen.iterdir()) == set(QWEN_ARTIFACT_FILES.values())
        assert linked[offline / "model.safetensors"] == (verified / "model.safetensors").resolve()
        assert all(target.is_relative_to(verified.resolve()) for target in linked.values())
        offline_path = offline

    assert not offline_path.exists()


def test_canary_offline_layout_fails_closed_when_a_dependency_is_missing(tmp_path: Path) -> None:
    verified = tmp_path / "verified"
    verified.mkdir()
    (verified / "config.json").write_text("{}", encoding="utf-8")
    (verified / "model.safetensors").write_bytes(b"checkpoint")

    with pytest.raises(WorkerFailure, match="qwen3-1.7b-config.json"):
        with _canary_offline_layout(verified):
            pytest.fail("an incomplete offline layout must never be yielded")
