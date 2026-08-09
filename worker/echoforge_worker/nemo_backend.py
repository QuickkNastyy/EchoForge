"""Isolated NVIDIA NeMo ASR backend.

This module is import-safe in the faster-whisper environment: NeMo and PyTorch are imported only
after a request selects this backend. EchoForge installs/launches it from a separate environment,
and the worker process remains the authoritative GPU lifetime boundary.

The backend never calls a mutable Hub alias during inference. Parakeet restores the verified
``.nemo`` archive supplied by the host; Canary loads the verified local Hugging Face directory
with offline environment flags forced. Both consume EchoForge's prepared 16 kHz mono derivative.
"""

from __future__ import annotations

import array
import importlib.metadata
import importlib.util
import json
import math
import os
import platform
import tempfile
import time
import wave
from contextlib import contextmanager, nullcontext
from pathlib import Path
from tempfile import TemporaryDirectory
from typing import Any, Callable, Sequence

from . import checkpoints
from .asr_registry import resolve_model
from .audio import PcmAudio, read_pcm16, resolve_inside
from .models import RequestDerivative, RequestOptions, RequestWindow, Segment, TranscriptModel, Word
from .protocol import ErrorCode, Stage, WorkerFailure
from .windows import WindowSegment, deduplicate, load_timing_map, normalize_text, rebase

PARAKEET_ID = "parakeet-unified-en-0.6b"
CANARY_ID = "canary-qwen-2.5b"
EXPECTED_SAMPLE_RATE = 16000
EXPECTED_NEMO_VERSION = "3.0.0"
EXPECTED_TORCH_VERSION = "2.8.0+cu128"

# Canary's released config names Qwen/Qwen3-1.7B as its tokenizer/config source even though the
# final Canary checkpoint contains the trained LLM weights.  Those six small files are therefore
# first-class, verified artifacts in EchoForge's Canary profile.  They are staged with unique
# names (so they cannot collide with Canary's own config.json) and exposed under their canonical
# Hugging Face names only inside a short-lived offline load directory.
QWEN_ARTIFACT_FILES: dict[str, str] = {
    "qwen3-1.7b-config.json": "config.json",
    "qwen3-1.7b-generation-config.json": "generation_config.json",
    "qwen3-1.7b-merges.txt": "merges.txt",
    "qwen3-1.7b-tokenizer.json": "tokenizer.json",
    "qwen3-1.7b-tokenizer-config.json": "tokenizer_config.json",
    "qwen3-1.7b-vocab.json": "vocab.json",
}


def production_stack_available() -> bool:
    """Whether this process is the isolated, supported NeMo runtime.

    NeMo 2.7.3's Parakeet card names Linux as its supported OS. A native Windows Python that
    happens to import part of NeMo is not advertised as usable; a WSL/Linux worker is.
    """
    if (
        platform.system() != "Linux"
        or importlib.util.find_spec("torch") is None
        or importlib.util.find_spec("nemo") is None
    ):
        return False

    try:
        return (
            importlib.metadata.version("nemo_toolkit") == EXPECTED_NEMO_VERSION
            and importlib.metadata.version("torch") == EXPECTED_TORCH_VERSION
        )
    except importlib.metadata.PackageNotFoundError:
        return False


class NemoBackend:
    name = "nemo"
    recognizes_speech = True

    def __init__(self, loader: Callable[[RequestOptions], tuple[Any, Any, dict[str, str]]] | None = None) -> None:
        self._loader = loader or _load_model
        self._model: Any = None
        self._torch: Any = None
        self._versions: dict[str, str] = {}
        self._model_id = "unknown"
        self._model_load_seconds = 0.0
        self._processing_seconds = 0.0
        self._audio_duration_seconds = 0.0
        self._asr_segment_count = 0
        self._window_count = 0
        self._signal_windows_without_text = 0
        self._peak_vram_bytes: int | None = None
        self._languages: dict[str, tuple[str, float | None]] = {}
        self._warnings: list[str] = []
        self._timestamp_capability = "segment"
        self._timestamp_precision = "window-approximate"

    def describe(self, options: RequestOptions) -> TranscriptModel:
        from .protocol import WORKER_VERSION

        return TranscriptModel(
            runtime="NVIDIA NeMo/PyTorch (isolated worker)",
            backend=self.name,
            model_id=self._model_id if self._model_id != "unknown" else (options.model_id or "unknown"),
            revision=options.model_revision or (options.model_id or "unknown"),
            compute_type=options.compute_profile or "not-loaded-checkpoint-reuse",
            recognizes_speech=True,
            worker_version=WORKER_VERSION,
            requested_compute_type=options.compute_profile,
            backend_runtime_version="; ".join(f"{key} {value}" for key, value in self._versions.items())
            or "NVIDIA NeMo isolated runtime",
            artifact_sha256=options.model_artifact_sha256,
        )

    def language_for(self, source_track: str) -> tuple[str, float | None]:
        return self._languages.get(source_track, ("en", None))

    def run_metadata(self, options: RequestOptions) -> dict[str, Any]:
        capability = resolve_model(options)
        return {
            "requested_compute_profile": options.compute_profile,
            "actual_compute_profile": options.compute_profile if self._model is not None else "not-loaded-checkpoint-reuse",
            "language": "en",
            "vad_mode": options.vad_mode,
            "vad_settings": {},
            "window_strategy": options.window_strategy or capability.window_strategy,
            "window_seconds": float(options.window_seconds or capability.maximum_window_seconds),
            "overlap_seconds": float(options.overlap_seconds or 0.0),
            "timestamp_capability": self._timestamp_capability,
            "timestamp_precision": self._timestamp_precision,
            "model_load_seconds": round(self._model_load_seconds, 6),
            "processing_seconds": round(self._processing_seconds, 6),
            "total_processing_seconds": 0.0,
            "peak_vram_bytes": self._peak_vram_bytes,
            "source_duration_seconds": 0.0,
            "audio_duration_seconds": round(self._audio_duration_seconds, 6),
            "real_time_factor": None,
            # Accuracy/Off are intentionally non-destructive for this backend.
            "vad_retained_seconds": round(self._audio_duration_seconds, 6),
            "vad_excluded_seconds": 0.0,
            "speech_region_count": self._window_count,
            "asr_segment_count": self._asr_segment_count,
            "window_count": self._window_count,
            "signal_windows_without_text": self._signal_windows_without_text,
            "warning_count": len(self._warnings),
            "fallback_count": 0,
            "warnings": list(self._warnings),
        }

    def transcribe_track(self, source_track: str, chunks: Sequence[Any], options: RequestOptions, context: Any) -> list[Segment]:
        raise WorkerFailure(
            ErrorCode.INVALID_REQUEST,
            Stage.PREPARING,
            "the NeMo backend requires model-specific prepared windows",
        )

    def transcribe_windows(
        self,
        source_track: str,
        windows: Sequence[RequestWindow],
        derivative: RequestDerivative,
        options: RequestOptions,
        context: Any,
    ) -> list[Segment]:
        if not windows:
            return []

        capability = resolve_model(options)
        if options.vad_mode not in ("accuracy", "off"):
            raise WorkerFailure(
                ErrorCode.INVALID_REQUEST,
                Stage.PREPARING,
                "the NeMo backend supports only non-destructive Accuracy or No VAD modes",
            )

        audio = _open(context.session_root, derivative)
        timing = _timing(context.session_root, derivative)
        result_directory = checkpoints.directory_for(context.session_root, options.profile or capability.window_strategy)
        placed: list[Segment] = []

        for window in windows:
            context.check_cancelled()
            context.window_started(window)
            result = checkpoints.load(result_directory, window)

            if result is None:
                self._ensure_loaded(options)
                samples = _read_window(audio, window)
                started = time.perf_counter()
                result = self._recognise(samples, window, options)
                self._processing_seconds += time.perf_counter() - started
                self._window_count += 1
                self._audio_duration_seconds += len(samples) / EXPECTED_SAMPLE_RATE
                self._asr_segment_count += len(result)
                if _nontrivial_signal(samples) and not result:
                    self._signal_windows_without_text += 1
                    warning = f"{window.id} contained nontrivial signal but ASR returned no segments"
                    self._note_warning(warning)
                    context.warn(
                        "signal_without_transcript",
                        warning,
                    )
                self._peak_vram_bytes = _maximum(self._peak_vram_bytes, _torch_peak_vram(self._torch))
                checkpoints.save(result_directory, window, result, "en")
            else:
                context.warn("window_reused", f"{window.id} reused a completed checkpoint")

            placed.extend(rebase(window, result, timing, context.session_duration_seconds))
            context.window_completed(window, len(result))

        self._languages[source_track] = ("en", None)
        if options.model_id == CANARY_ID:
            return _deduplicate_canary(placed, windows)
        return deduplicate(placed, windows)

    def _ensure_loaded(self, options: RequestOptions) -> None:
        if self._model is not None:
            return
        started = time.perf_counter()
        try:
            self._model, self._torch, self._versions = self._loader(options)
        except WorkerFailure:
            raise
        except Exception as error:  # noqa: BLE001 - raw framework messages may contain local paths
            raise WorkerFailure(
                ErrorCode.BACKEND_FAILED,
                Stage.PREPARING,
                f"the verified NeMo model could not be loaded: {type(error).__name__}",
            ) from error
        self._model_load_seconds = time.perf_counter() - started
        self._model_id = options.model_id or "unknown"

    def _recognise(
        self, samples: Sequence[float], window: RequestWindow, options: RequestOptions
    ) -> tuple[WindowSegment, ...]:
        path = _scratch_wav(samples)
        try:
            dtype = self._torch.bfloat16 if options.compute_profile == "cuda-bf16" else self._torch.float16
            autocast = self._torch.autocast("cuda", dtype=dtype) if self._torch.cuda.is_available() else nullcontext()
            with self._torch.inference_mode(), autocast:
                if options.model_id == PARAKEET_ID:
                    return self._parakeet(path, window)
                if options.model_id == CANARY_ID:
                    return self._canary(path, window)
                raise WorkerFailure(ErrorCode.INVALID_REQUEST, Stage.PREPARING, "unsupported NeMo model")
        finally:
            try:
                path.unlink(missing_ok=True)
            except OSError:
                pass

    def _parakeet(self, path: Path, window: RequestWindow) -> tuple[WindowSegment, ...]:
        try:
            output = self._model.transcribe([str(path)], return_hypotheses=True, timestamps=True)
        except TypeError:
            output = self._model.transcribe([str(path)], return_hypotheses=True)
        hypothesis = output[0] if isinstance(output, Sequence) else output
        text = _hypothesis_text(hypothesis)
        timestamps = getattr(hypothesis, "timestamp", None)
        segments = _timestamped_segments(text, timestamps, window.frames / EXPECTED_SAMPLE_RATE)
        if any(segment.words for segment in segments):
            self._timestamp_capability = "word"
            self._timestamp_precision = "word-native"
        elif segments:
            self._timestamp_capability = "segment"
            self._timestamp_precision = "segment-native" if timestamps else "window-approximate"
            if not timestamps:
                self._note_warning("native timestamps were unavailable; this run records window-approximate ranges")
        return segments

    def _canary(self, path: Path, window: RequestWindow) -> tuple[WindowSegment, ...]:
        prompts = [[{
            "role": "user",
            "content": f"Transcribe the following: {self._model.audio_locator_tag}",
            "audio": [str(path)],
        }]]
        answer_ids = self._model.generate(prompts=prompts, max_new_tokens=512)
        text = self._model.tokenizer.ids_to_text(answer_ids[0].cpu()).strip()
        self._timestamp_capability = "segment"
        self._timestamp_precision = "window-approximate"
        if not text:
            return ()
        return (WindowSegment(0.0, window.frames / EXPECTED_SAMPLE_RATE, text, language="en"),)

    def _note_warning(self, warning: str) -> None:
        if warning not in self._warnings:
            self._warnings.append(warning)


def _load_model(options: RequestOptions) -> tuple[Any, Any, dict[str, str]]:
    if not production_stack_available():
        raise WorkerFailure(
            ErrorCode.BACKEND_UNAVAILABLE,
            Stage.PREPARING,
            "the isolated Linux NeMo runtime is not installed or CUDA is unavailable",
        )

    import nemo
    import torch

    actual_nemo = str(getattr(nemo, "__version__", "unknown"))
    actual_torch = str(getattr(torch, "__version__", "unknown"))
    if actual_nemo != EXPECTED_NEMO_VERSION or actual_torch != EXPECTED_TORCH_VERSION:
        raise WorkerFailure(
            ErrorCode.BACKEND_UNAVAILABLE,
            Stage.PREPARING,
            "the isolated NeMo runtime does not match EchoForge's pinned NeMo/PyTorch versions",
        )

    if not torch.cuda.is_available():
        raise WorkerFailure(ErrorCode.BACKEND_UNAVAILABLE, Stage.PREPARING, "NeMo ASR requires usable CUDA")
    if options.compute_profile == "cuda-bf16" and not torch.cuda.is_bf16_supported():
        raise WorkerFailure(
            ErrorCode.BACKEND_UNAVAILABLE, Stage.PREPARING, "this GPU does not support the requested CUDA BF16 mode"
        )

    directory = Path(options.model_path or "")
    if not directory.is_dir():
        raise WorkerFailure(ErrorCode.INPUT_MISSING, Stage.PREPARING, "the verified NeMo model directory is missing")

    # Belt and suspenders around the host's offline environment. from_pretrained receives a local
    # directory, but these flags make an incomplete directory fail instead of consulting the Hub.
    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"

    if options.model_id == PARAKEET_ID:
        from nemo.collections.asr.models import ASRModel

        archives = list(directory.glob("*.nemo"))
        if len(archives) != 1:
            raise WorkerFailure(
                ErrorCode.INPUT_MISSING, Stage.PREPARING, "the verified Parakeet directory has no unique .nemo archive"
            )
        model = ASRModel.restore_from(restore_path=str(archives[0]), map_location="cuda")
        _allow_transcription_without_validation_config(model)
    elif options.model_id == CANARY_ID:
        from nemo.collections.speechlm2.models import SALM

        # NeMo's SALM loader sets pretrained_weights=False for the final checkpoint, so the
        # referenced base ASR weights are not fetched.  It still constructs Qwen's architecture
        # and tokenizer from ``pretrained_llm``.  Rewrite that one reference to EchoForge's
        # verified local Qwen sidecars in a temporary layout and explicitly request local-only
        # loading.  No Hub cache, mutable alias, or network endpoint can satisfy a missing file.
        with _canary_offline_layout(directory) as offline_directory:
            model = SALM.from_pretrained(str(offline_directory), local_files_only=True)
        model = model.cuda()
    else:
        raise WorkerFailure(ErrorCode.INVALID_REQUEST, Stage.PREPARING, "unsupported NeMo model")

    model.eval()
    return model, torch, {"NeMo": actual_nemo, "PyTorch": actual_torch}


def _allow_transcription_without_validation_config(model: Any) -> None:
    """Give a checkpoint with no validation_ds section the empty one NeMo's loader assumes.

    The pinned Parakeet release ships ``validation_ds: null``, and NeMo 3.0.0's transcription
    dataloader reads ``self.cfg.validation_ds.get(...)`` without checking. Restoring the model
    succeeds and then the first transcription raises an AttributeError from inside the library.

    Supplying an empty section changes no behaviour - every key the loader reads has a default and
    this checkpoint sets none of them - and it is done here rather than by editing the verified
    checkpoint, which must stay byte-identical to what its digest describes.
    """
    from omegaconf import OmegaConf

    OmegaConf.set_struct(model.cfg, False)
    if model.cfg.get("validation_ds") is None:
        model.cfg.validation_ds = OmegaConf.create({})

@contextmanager
def _canary_offline_layout(verified_directory: Path):
    """Build the exact short-lived directory NeMo needs without copying Canary's 5 GB weights."""

    with TemporaryDirectory(prefix="echoforge-canary-offline-") as temporary:
        required = ["config.json", "model.safetensors", *QWEN_ARTIFACT_FILES]
        missing = [name for name in required if not (verified_directory / name).is_file()]
        if missing:
            raise WorkerFailure(
                ErrorCode.INPUT_MISSING,
                Stage.PREPARING,
                "the verified Canary directory is incomplete; missing " + ", ".join(sorted(missing)),
            )

        root = Path(temporary)
        qwen = root / "qwen3-1.7b"
        qwen.mkdir()

        # A Linux symlink keeps the huge, verified checkpoint single-instanced.  The target is
        # inside the host-staged model directory and remains present for the entire worker job.
        (root / "model.safetensors").symlink_to(
            (verified_directory / "model.safetensors").resolve()
        )
        for staged_name, canonical_name in QWEN_ARTIFACT_FILES.items():
            (qwen / canonical_name).symlink_to((verified_directory / staged_name).resolve())

        try:
            config = json.loads((verified_directory / "config.json").read_text(encoding="utf-8"))
        except (OSError, ValueError, TypeError) as error:
            raise WorkerFailure(
                ErrorCode.INPUT_INVALID,
                Stage.PREPARING,
                "the verified Canary configuration is not valid JSON",
            ) from error
        if not isinstance(config, dict):
            raise WorkerFailure(
                ErrorCode.INPUT_INVALID,
                Stage.PREPARING,
                "the verified Canary configuration is not an object",
            )

        config["pretrained_llm"] = str(qwen)
        (root / "config.json").write_text(
            json.dumps(config, ensure_ascii=False, separators=(",", ":")),
            encoding="utf-8",
        )
        yield root


def _timestamped_segments(text: str, raw: Any, duration: float) -> tuple[WindowSegment, ...]:
    if not isinstance(raw, dict):
        return () if not text else (WindowSegment(0.0, duration, text, language="en"),)

    words: list[Word] = []
    for entry in raw.get("word") or []:
        if not isinstance(entry, dict):
            continue
        value = str(entry.get("word") or entry.get("text") or "").strip()
        start = _number(entry.get("start"))
        end = _number(entry.get("end"))
        if value and start is not None and end is not None and 0 <= start < end <= duration + 0.1:
            words.append(Word(value, start, min(end, duration), None))

    native_segments: list[WindowSegment] = []
    for entry in raw.get("segment") or []:
        if not isinstance(entry, dict):
            continue
        value = str(entry.get("segment") or entry.get("text") or "").strip()
        start = _number(entry.get("start"))
        end = _number(entry.get("end"))
        if not value or start is None or end is None or start < 0 or end <= start:
            continue
        contained = tuple(word for word in words if word.start_seconds >= start - 0.01 and word.end_seconds <= end + 0.01)
        native_segments.append(WindowSegment(start, min(end, duration), value, contained, "en"))

    if native_segments:
        return tuple(native_segments)
    if text:
        start = words[0].start_seconds if words else 0.0
        end = words[-1].end_seconds if words else duration
        return (WindowSegment(start, end, text, tuple(words), "en"),)
    return ()


def _deduplicate_canary(segments: Sequence[Segment], windows: Sequence[RequestWindow]) -> list[Segment]:
    """Conservative text-only overlap de-duplication for Canary's approximate timestamps.

    Exact duplicate window answers are removed. A suffix/prefix is trimmed only when at least
    three normalized words match, keeping ambiguous one-word replies rather than risking recall.
    The resulting time remains explicitly window-approximate; no word times are invented.
    """
    ordered = sorted(segments, key=lambda segment: segment.sort_key())
    kept: list[Segment] = []
    for candidate in ordered:
        previous = next(
            (
                item for item in reversed(kept)
                if item.source_track == candidate.source_track
                and item.epoch == candidate.epoch
                and candidate.start_seconds < item.end_seconds
            ),
            None,
        )
        if previous is None:
            kept.append(candidate)
            continue

        before = normalize_text(previous.text).split()
        after = normalize_text(candidate.text).split()
        if before and before == after:
            continue

        match = 0
        for count in range(3, min(len(before), len(after)) + 1):
            if before[-count:] == after[:count]:
                match = count
        if match:
            original_words = candidate.text.split()
            if len(original_words) > match:
                candidate.text = " ".join(original_words[match:])
            else:
                continue
        kept.append(candidate)
    return kept


def _open(session_root: str, derivative: RequestDerivative) -> PcmAudio:
    audio = read_pcm16(resolve_inside(session_root, derivative.relative_path))
    if audio.sample_rate != EXPECTED_SAMPLE_RATE or audio.channels != 1:
        raise WorkerFailure(
            ErrorCode.INPUT_INVALID,
            Stage.READING_AUDIO,
            f"the prepared audio is {audio.sample_rate} Hz / {audio.channels} ch; 16000 Hz mono was expected",
        )
    return audio


def _timing(session_root: str, derivative: RequestDerivative) -> Any:
    path = resolve_inside(session_root, derivative.timing_map_relative_path)
    if not path.is_file():
        raise WorkerFailure(
            ErrorCode.INPUT_MISSING,
            Stage.READING_AUDIO,
            "the prepared audio has no timing map, so nothing could be traced back to it",
        )
    return load_timing_map(json.loads(path.read_text(encoding="utf-8")))


def _read_window(audio: PcmAudio, window: RequestWindow) -> list[float]:
    values = array.array("h")
    values.frombytes(audio.data)
    first = max(0, min(window.start_frame, len(values)))
    last = max(first, min(window.end_frame, len(values)))
    return [values[index] / 32768.0 for index in range(first, last)]


def _scratch_wav(samples: Sequence[float]) -> Path:
    handle, name = tempfile.mkstemp(prefix="echoforge-nemo-", suffix=".wav")
    os.close(handle)
    path = Path(name)
    pcm = array.array("h", (max(-32768, min(32767, round(value * 32767))) for value in samples))
    with wave.open(str(path), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(EXPECTED_SAMPLE_RATE)
        output.writeframes(pcm.tobytes())
    return path


def _hypothesis_text(value: Any) -> str:
    text = getattr(value, "text", value if isinstance(value, str) else "")
    return str(text or "").strip()


def _number(value: Any) -> float | None:
    try:
        result = float(value)
        return result if math.isfinite(result) else None
    except (TypeError, ValueError):
        return None


def _nontrivial_signal(samples: Sequence[float]) -> bool:
    if not samples:
        return False
    mean_square = sum(value * value for value in samples) / len(samples)
    return math.sqrt(mean_square) >= 0.0005


def _torch_peak_vram(torch: Any) -> int | None:
    try:
        return int(torch.cuda.max_memory_allocated()) if torch.cuda.is_available() else None
    except Exception:  # noqa: BLE001 - telemetry must never fail the transcript
        return None


def _maximum(left: int | None, right: int | None) -> int | None:
    values = [value for value in (left, right) if value is not None]
    return max(values) if values else None
