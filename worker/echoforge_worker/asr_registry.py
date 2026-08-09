"""ASR model capabilities and pinned VAD policies.

This module contains no inference imports, so the worker can validate a request before loading a
model or touching CUDA. The C# registry owns presentation/setup; this registry is the worker-side
enforcement boundary that prevents an unsupported model/backend/compute combination from running.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Final

from .models import RequestOptions
from .protocol import ErrorCode, Stage, WorkerFailure


@dataclass(frozen=True, slots=True)
class AsrModelCapability:
    model_id: str
    backend_id: str
    revision: str
    languages: tuple[str, ...]
    compute_profiles: tuple[str, ...]
    vad_modes: tuple[str, ...]
    timestamp_capability: str
    timestamp_precision: str
    expected_sample_rate: int
    window_strategy: str
    maximum_window_seconds: float
    supports_glossary: bool
    experimental: bool = False


MODELS: Final[dict[str, AsrModelCapability]] = {
    "mock-asr": AsrModelCapability(
        "mock-asr",
        "mock",
        "mock-v1",
        ("und",),
        ("cpu-int8",),
        ("off",),
        "segment",
        "window-approximate",
        16000,
        "whisper-long-v2",
        600.0,
        False,
        True,
    ),
    "whisper-large-v3-turbo": AsrModelCapability(
        "whisper-large-v3-turbo",
        "faster-whisper",
        "0a363e9161cbc7ed1431c9597a8ceaf0c4f78fcf",
        ("multilingual",),
        ("cpu-int8", "cuda-int8-float16", "cuda-fp16"),
        ("accuracy", "balanced", "fast", "off"),
        "word",
        "word-native",
        16000,
        "whisper-long-v2",
        600.0,
        True,
    ),
    "whisper-large-v3": AsrModelCapability(
        "whisper-large-v3",
        "faster-whisper",
        "edaa852ec7e145841d8ffdb056a99866b5f0a478",
        ("multilingual",),
        ("cpu-int8", "cuda-int8-float16", "cuda-fp16"),
        ("accuracy", "balanced", "fast", "off"),
        "word",
        "word-native",
        16000,
        "whisper-long-v2",
        600.0,
        True,
    ),
    "parakeet-unified-en-0.6b": AsrModelCapability(
        "parakeet-unified-en-0.6b",
        "nemo",
        "fe53cd885760c96b6a5f51a0bfd362cb4584a98b",
        ("en",),
        ("cuda-fp16", "cuda-bf16"),
        ("accuracy", "off"),
        "word",
        "word-native",
        16000,
        "parakeet-offline-v1",
        300.0,
        False,
        True,
    ),
    "canary-qwen-2.5b": AsrModelCapability(
        "canary-qwen-2.5b",
        "nemo",
        "b1469e1bba1cfe140205529c79c434ca47180960",
        ("en",),
        ("cuda-fp16", "cuda-bf16"),
        ("accuracy", "off"),
        "segment",
        "window-approximate",
        16000,
        "canary-short-v1",
        40.0,
        False,
        True,
    ),
}


@dataclass(frozen=True, slots=True)
class VadStrategy:
    mode: str
    filter_audio: bool
    parameters: dict[str, float | int]
    description: str


# Names and defaults match faster-whisper 1.2.1's locally inspected VadOptions signature.
VAD_STRATEGIES: Final[dict[str, VadStrategy]] = {
    "accuracy": VadStrategy(
        "accuracy",
        False,
        {},
        "No destructive filtering; every sample in the planned window reaches ASR.",
    ),
    "balanced": VadStrategy(
        "balanced",
        True,
        {
            "threshold": 0.35,
            "neg_threshold": 0.20,
            "min_speech_duration_ms": 80,
            "min_silence_duration_ms": 1000,
            "speech_pad_ms": 500,
        },
        "Permissive Silero filtering that removes sustained silence and pads conversational speech.",
    ),
    "fast": VadStrategy(
        "fast",
        True,
        {
            "threshold": 0.50,
            "neg_threshold": 0.35,
            "min_speech_duration_ms": 200,
            "min_silence_duration_ms": 500,
            "speech_pad_ms": 200,
        },
        "More aggressive silence removal for throughput-oriented runs.",
    ),
    "off": VadStrategy(
        "off",
        False,
        {},
        "Diagnostic no-VAD run; every sample in every planned window reaches ASR.",
    ),
}


def resolve_model(options: RequestOptions) -> AsrModelCapability:
    """Resolve and validate model identity without silently guessing for new backends."""
    model_id = options.model_id
    if not model_id:
        # Protocol-v1 compatibility. Historical faster-whisper requests always meant Turbo.
        model_id = {
            "mock": "mock-asr",
            "faster-whisper": "whisper-large-v3-turbo",
        }.get(options.backend)
    capability = MODELS.get(model_id or "")
    if capability is None:
        raise WorkerFailure(
            ErrorCode.INVALID_REQUEST,
            Stage.ACCEPTING,
            "the requested ASR model is not in this worker build",
        )
    if capability.backend_id != options.backend:
        raise WorkerFailure(
            ErrorCode.INVALID_REQUEST,
            Stage.ACCEPTING,
            "the requested ASR model does not belong to the requested backend",
        )
    if options.model_revision and options.model_revision != capability.revision:
        raise WorkerFailure(
            ErrorCode.INVALID_REQUEST,
            Stage.ACCEPTING,
            "the requested ASR model revision does not match this worker build",
        )
    if options.compute_profile and options.compute_profile not in capability.compute_profiles:
        raise WorkerFailure(
            ErrorCode.INVALID_REQUEST,
            Stage.ACCEPTING,
            "the requested compute profile is unsupported by this ASR model",
        )
    if options.vad_mode not in capability.vad_modes:
        raise WorkerFailure(
            ErrorCode.INVALID_REQUEST,
            Stage.ACCEPTING,
            "the requested VAD mode is unsupported by this ASR model",
        )
    if options.window_strategy and options.window_strategy != capability.window_strategy:
        raise WorkerFailure(
            ErrorCode.INVALID_REQUEST,
            Stage.ACCEPTING,
            "the requested window strategy does not match this ASR model",
        )
    if options.window_seconds and options.window_seconds > capability.maximum_window_seconds + 1e-6:
        raise WorkerFailure(
            ErrorCode.INVALID_REQUEST,
            Stage.ACCEPTING,
            "a transcription window exceeds this ASR model's supported duration",
        )
    if options.glossary and not capability.supports_glossary:
        raise WorkerFailure(
            ErrorCode.INVALID_REQUEST,
            Stage.ACCEPTING,
            "this ASR backend does not support glossary prompting",
        )
    if options.language and capability.languages == ("en",) and options.language not in ("en", "english"):
        raise WorkerFailure(
            ErrorCode.INVALID_REQUEST,
            Stage.ACCEPTING,
            "this ASR model supports English only",
        )
    return capability


def vad_strategy(mode: str) -> VadStrategy:
    try:
        return VAD_STRATEGIES[mode]
    except KeyError as exc:  # protected by request parsing; defensive for direct backend tests
        raise WorkerFailure(
            ErrorCode.INVALID_REQUEST, Stage.ACCEPTING, "the requested VAD mode is unknown"
        ) from exc
