"""The production backend: faster-whisper over the host's prepared windows.

It loads the CTranslate2 model **from a directory the host verified**. There is no repository
id, no alias, and no download: faster-whisper's convenience aliases resolve to third-party
conversions that can and do move, and one of them had already moved by the time it was pinned.
Once installed, nothing here touches the network.

Windows arrive already placed on the session timeline. This module reads each one out of the
prepared derivative, recognises it, and hands the result to :mod:`windows` to be rebased and
de-duplicated. The recogniser itself is injectable, so the whole path - window slicing,
rebasing, gap handling, overlap removal, transcript assembly - is testable without a 1.6 GB
model on disk.
"""

from __future__ import annotations

import math
import os
import subprocess
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Final, Sequence

from . import checkpoints, compute
from .asr_registry import resolve_model, vad_strategy
from .audio import PcmAudio, read_pcm16, resolve_inside
from .models import (
    RequestDerivative,
    RequestOptions,
    RequestWindow,
    Segment,
    TranscriptModel,
    Word,
)
from .protocol import ErrorCode, Stage, WorkerFailure
from .windows import WindowSegment, deduplicate, load_timing_map, rebase

#: Whisper works at 16 kHz mono. The derivative was built to exactly that, and a mismatch means
#: the wrong file was handed over rather than something to resample around.
EXPECTED_SAMPLE_RATE: Final[int] = 16000

#: Full-scale for PCM16, for the float conversion faster-whisper expects.
_FULL_SCALE: Final[float] = 32768.0


@dataclass(frozen=True, slots=True)
class WindowResult:
    """One window's recognised speech, before rebasing."""

    window_id: str
    segments: tuple[WindowSegment, ...]
    language: str | None
    language_probability: float | None
    audio_duration_seconds: float = 0.0
    vad_retained_seconds: float = 0.0
    speech_region_count: int = 0
    nontrivial_signal: bool = False


#: A recogniser: given samples and options, answer with window-relative segments. Injectable so
#: the pipeline can be proven without a model.
Recogniser = Callable[["FloatSamples", RequestWindow, RequestOptions], WindowResult]

FloatSamples = Sequence[float]


def read_window(audio: PcmAudio, window: RequestWindow) -> list[float]:
    """Slice one window out of a derivative and scale it to floats.

    Clamped to what the file actually holds. A window running past the end of the derivative
    would otherwise read whatever happened to be in memory next.
    """
    import array

    samples = array.array("h")
    samples.frombytes(audio.data)

    first = max(0, min(window.start_frame, len(samples)))
    last = max(first, min(window.end_frame, len(samples)))

    return [samples[i] / _FULL_SCALE for i in range(first, last)]


def build_initial_prompt(options: RequestOptions) -> str | None:
    """Seed the recogniser with names and jargon it would otherwise mis-hear.

    A glossary is joined into the prompt rather than passed separately because that is the only
    lever Whisper offers. It biases; it does not guarantee, and nothing here pretends otherwise.
    """
    parts = [part for part in (options.initial_prompt, ", ".join(options.glossary)) if part and part.strip()]
    return ". ".join(part.strip().rstrip(".") for part in parts) + "." if parts else None


class FasterWhisperBackend:
    """Production speech recognition. Real, and labelled as such."""

    name = "faster-whisper"
    recognizes_speech = True

    def __init__(self, recogniser_factory: Callable[[RequestOptions, compute.ComputePlan], Recogniser] | None = None) -> None:
        self._factory = recogniser_factory or _load_faster_whisper
        self._outcome: compute.ComputeOutcome | None = None
        self._model_id = "unknown"
        self._languages: dict[str, tuple[str, float | None]] = {}
        self._model_load_seconds = 0.0
        self._processing_seconds = 0.0
        self._audio_duration_seconds = 0.0
        self._vad_retained_seconds = 0.0
        self._speech_region_count = 0
        self._asr_segment_count = 0
        self._window_count = 0
        self._signal_windows_without_text = 0
        self._peak_vram_bytes: int | None = None
        self._fallback_warnings: list[str] = []

    # -- metadata -------------------------------------------------------------------------

    @property
    def outcome(self) -> compute.ComputeOutcome | None:
        return self._outcome

    def language_for(self, source_track: str) -> tuple[str, float | None]:
        return self._languages.get(source_track, ("und", None))

    def describe(self, options: RequestOptions) -> TranscriptModel:
        from .protocol import WORKER_VERSION

        plan = self._outcome.plan if self._outcome else None

        # The request always carries the model identity; the backend only learns it when it
        # actually loads a checkpoint. A run that reused an already-prepared one therefore recorded
        # "unknown", and the meeting page dutifully showed "unknown → Gemma 4 12B" over a brief
        # produced by Whisper. Provenance that degrades when nothing went wrong is worse than
        # useless, because it looks like something did.
        model_id = self._model_id if self._model_id != "unknown" else (options.model_id or self._model_id)

        return TranscriptModel(
            runtime="faster-whisper/CTranslate2",
            backend=self.name,
            model_id=model_id,
            revision=options.model_revision or model_id,
            compute_type=plan.compute_type if plan else "not-loaded-checkpoint-reuse",
            recognizes_speech=True,
            worker_version=WORKER_VERSION,
            requested_compute_type=options.compute_profile,
            backend_runtime_version=(
                "; ".join(f"{name} {version}" for name, version in self._outcome.runtime_versions.items())
                if self._outcome
                else "faster-whisper 1.2.1; CTranslate2 4.8.1"
            ),
            artifact_sha256=options.model_artifact_sha256,
        )

    def run_metadata(self, options: RequestOptions) -> dict[str, Any]:
        capability = resolve_model(options)
        strategy = vad_strategy(options.vad_mode)
        outcome = self._outcome
        actual = outcome.plan.profile if outcome else "not-loaded-checkpoint-reuse"
        return {
            "requested_compute_profile": options.compute_profile or compute.CPU_INT8,
            "actual_compute_profile": actual,
            "language": options.language or "auto",
            "vad_mode": strategy.mode,
            "vad_settings": {key: float(value) for key, value in strategy.parameters.items()},
            "window_strategy": options.window_strategy or capability.window_strategy,
            "window_seconds": float(options.window_seconds or capability.maximum_window_seconds),
            "overlap_seconds": float(options.overlap_seconds or 0.0),
            "timestamp_capability": options.timestamp_capability or capability.timestamp_capability,
            "timestamp_precision": options.timestamp_precision or capability.timestamp_precision,
            "model_load_seconds": round(self._model_load_seconds, 6),
            "processing_seconds": round(self._processing_seconds, 6),
            "total_processing_seconds": 0.0,
            "peak_vram_bytes": self._peak_vram_bytes,
            "source_duration_seconds": 0.0,
            "audio_duration_seconds": round(self._audio_duration_seconds, 6),
            "real_time_factor": None,
            "vad_retained_seconds": round(self._vad_retained_seconds, 6),
            "vad_excluded_seconds": round(
                max(0.0, self._audio_duration_seconds - self._vad_retained_seconds), 6
            ),
            "speech_region_count": self._speech_region_count,
            "asr_segment_count": self._asr_segment_count,
            "window_count": self._window_count,
            "signal_windows_without_text": self._signal_windows_without_text,
            "warning_count": len(self._fallback_warnings),
            "fallback_count": 1 if outcome and outcome.fell_back else 0,
            "warnings": list(self._fallback_warnings),
        }

    # -- the job ---------------------------------------------------------------------------

    def transcribe_windows(
        self,
        source_track: str,
        windows: Sequence[RequestWindow],
        derivative: RequestDerivative,
        options: RequestOptions,
        context: Any,
    ) -> list[Segment]:
        """Recognise every window of one track and place the results on the session."""
        if not windows:
            return []

        audio = self._open(context.session_root, derivative)
        timing = self._timing(context.session_root, derivative)

        results = checkpoints.directory_for(
            context.session_root, options.profile or "windows-v1"
        )

        # The model is loaded only if some window actually needs running. A fully
        # checkpointed track costs nothing on a resume, which is the point of checkpoints.
        recogniser: Recogniser | None = None

        placed: list[Segment] = []
        detected: tuple[str, float | None] | None = None

        for window in windows:
            context.check_cancelled()
            context.window_started(window)

            segments = checkpoints.load(results, window)

            if segments is None:
                if recogniser is None:
                    recogniser = self._recogniser(options)
                    if self._outcome and self._outcome.fell_back:
                        detail = self._outcome.fallback_reason or "the requested compute profile was not used"
                        warning = (
                            f"Requested {self._outcome.requested_profile}; actually ran "
                            f"{self._outcome.plan.profile}: {detail}"
                        )
                        self._fallback_warnings.append(warning)
                        context.warn("compute_fallback", warning)

                started = time.perf_counter()
                result = recogniser(read_window(audio, window), window, options)
                self._processing_seconds += time.perf_counter() - started
                segments = result.segments
                self._window_count += 1
                self._audio_duration_seconds += result.audio_duration_seconds
                self._vad_retained_seconds += result.vad_retained_seconds
                self._speech_region_count += result.speech_region_count
                self._asr_segment_count += len(segments)
                if result.nontrivial_signal and not segments:
                    self._signal_windows_without_text += 1
                    warning = f"{window.id} contained nontrivial signal but ASR returned no segments"
                    if warning not in self._fallback_warnings:
                        self._fallback_warnings.append(warning)
                    context.warn(
                        "signal_without_transcript",
                        warning,
                    )
                self._peak_vram_bytes = _maximum(self._peak_vram_bytes, _process_vram_bytes())

                if detected is None and result.language:
                    detected = (result.language, result.language_probability)

                # Written the moment it succeeds: window seventeen failing must not cost
                # the sixteen that already worked.
                checkpoints.save(results, window, segments, result.language)
            else:
                context.warn("window_reused", f"{window.id} reused a completed checkpoint")

            placed.extend(rebase(window, segments, timing, context.session_duration_seconds))

            context.window_completed(window, len(segments))

        self._languages[source_track] = detected or (options.language or "und", None)

        # De-duplication happens per track, after every window: a duplicate only exists
        # relative to the window next to it, and both have to be on the timeline first.
        return deduplicate(placed, windows)

    # -- plumbing ---------------------------------------------------------------------------

    def _recogniser(self, options: RequestOptions) -> Recogniser:
        """Load the model, climbing down through compute plans until one works."""
        if options.model_path is None:
            raise WorkerFailure(
                ErrorCode.BACKEND_UNAVAILABLE,
                Stage.PREPARING,
                "no verified model directory was supplied; the host resolves and verifies it",
            )

        requested = options.compute_profile or compute.CPU_INT8
        if requested not in compute.PROFILES:
            raise WorkerFailure(
                ErrorCode.INVALID_REQUEST, Stage.PREPARING, f"unknown compute profile {requested!r}"
            )

        resolve_model(options)
        self._model_id = options.model_id or Path(options.model_path).name

        try:
            started = time.perf_counter()
            recogniser, outcome = compute.run_with_fallback(
                requested,
                lambda plan: self._factory(options, plan),
                allow_cpu_fallback=options.allow_cpu_fallback,
            )
            self._model_load_seconds += time.perf_counter() - started
        except WorkerFailure:
            raise
        except Exception as error:  # noqa: BLE001 - every load failure is reported, not raised raw
            raise WorkerFailure(
                ErrorCode.BACKEND_FAILED,
                Stage.PREPARING,
                f"the speech model could not be loaded: {type(error).__name__}",
            ) from error

        self._outcome = outcome
        return recogniser

    @staticmethod
    def _open(session_root: str, derivative: RequestDerivative) -> PcmAudio:
        audio = read_pcm16(resolve_inside(session_root, derivative.relative_path))

        if audio.sample_rate != EXPECTED_SAMPLE_RATE or audio.channels != 1:
            raise WorkerFailure(
                ErrorCode.INPUT_INVALID,
                Stage.READING_AUDIO,
                f"the prepared audio is {audio.sample_rate} Hz / {audio.channels} ch; "
                f"{EXPECTED_SAMPLE_RATE} Hz mono was expected",
            )

        return audio

    @staticmethod
    def _timing(session_root: str, derivative: RequestDerivative) -> Any:
        import json

        path = resolve_inside(session_root, derivative.timing_map_relative_path)
        if not path.is_file():
            # Without a map, a gap is indistinguishable from silence that was recorded. Refuse
            # rather than place segments in time that may not exist.
            raise WorkerFailure(
                ErrorCode.INPUT_MISSING,
                Stage.READING_AUDIO,
                "the prepared audio has no timing map, so nothing could be traced back to it",
            )

        return load_timing_map(json.loads(path.read_text(encoding="utf-8")))


def _load_faster_whisper(options: RequestOptions, plan: compute.ComputePlan) -> Recogniser:
    """Load the real model and return something that recognises one window.

    Imported here rather than at module scope so the worker still starts, and the placeholder
    backend still works, on an installation where the production stack is absent.
    """
    try:
        from faster_whisper import WhisperModel
    except ImportError as error:
        raise WorkerFailure(
            ErrorCode.BACKEND_UNAVAILABLE,
            Stage.PREPARING,
            "the production speech stack is not installed in this worker environment",
        ) from error

    directory = Path(options.model_path or "")
    if not (directory / "model.bin").is_file():
        raise WorkerFailure(
            ErrorCode.INPUT_MISSING,
            Stage.PREPARING,
            "the verified model directory does not contain a CTranslate2 model",
        )

    # local_files_only: the model was verified by the registry and there is nothing to fetch.
    # Left off, faster-whisper would silently reach for the Hub and undo the pinning.
    model = WhisperModel(
        str(directory),
        device=plan.device,
        compute_type=plan.compute_type,
        local_files_only=True,
    )

    prompt = build_initial_prompt(options)
    strategy = vad_strategy(options.vad_mode)

    def recognise(samples: FloatSamples, window: RequestWindow, opts: RequestOptions) -> WindowResult:
        import numpy

        values = numpy.asarray(samples, dtype=numpy.float32)
        duration = float(len(values) / EXPECTED_SAMPLE_RATE)
        retained = duration
        regions = 1 if len(values) else 0
        if strategy.filter_audio:
            from faster_whisper.vad import VadOptions, get_speech_timestamps

            vad_options = VadOptions(**strategy.parameters)
            timestamps = get_speech_timestamps(
                values, vad_options=vad_options, sampling_rate=EXPECTED_SAMPLE_RATE
            )
            retained_samples = sum(
                max(0, int(region["end"]) - int(region["start"])) for region in timestamps
            )
            retained = min(duration, retained_samples / EXPECTED_SAMPLE_RATE)
            regions = len(timestamps)

        rms = math.sqrt(float(numpy.mean(values * values))) if len(values) else 0.0

        segments, info = model.transcribe(
            values,
            language=None if opts.language in (None, "auto") else opts.language,
            beam_size=opts.beam_size or 5,
            word_timestamps=opts.word_timestamps,
            vad_filter=strategy.filter_audio,
            vad_parameters=strategy.parameters or None,
            initial_prompt=prompt,
            condition_on_previous_text=False,
        )

        collected: list[WindowSegment] = []
        for segment in segments:
            words = tuple(
                Word(
                    text=word.word,
                    start_seconds=float(word.start),
                    end_seconds=float(word.end),
                    probability=float(word.probability) if word.probability is not None else None,
                )
                for word in (segment.words or [])
            )

            collected.append(
                WindowSegment(
                    start_seconds=float(segment.start),
                    end_seconds=float(segment.end),
                    text=segment.text,
                    words=words,
                    language=info.language,
                    # Whisper reports an average log probability, which is not a calibrated
                    # confidence. Recording it as one would be a lie the schema forbids.
                    confidence=None,
                )
            )

        return WindowResult(
            window_id=window.id,
            segments=tuple(collected),
            language=info.language,
            language_probability=float(info.language_probability) if info.language_probability is not None else None,
            audio_duration_seconds=duration,
            vad_retained_seconds=retained,
            speech_region_count=regions,
            nontrivial_signal=rms >= 0.0005,
        )

    return recognise


def production_stack_available() -> bool:
    """Whether this worker environment can run the production backend at all."""
    if os.environ.get("ECHOFORGE_FORCE_NO_PRODUCTION") == "1":
        return False

    try:
        import ctranslate2  # noqa: F401
        import faster_whisper  # noqa: F401
    except Exception:  # noqa: BLE001
        return False

    return True


def _maximum(left: int | None, right: int | None) -> int | None:
    if left is None:
        return right
    if right is None:
        return left
    return max(left, right)


def _process_vram_bytes() -> int | None:
    """Best-effort current-process VRAM, with no content and no required dependency."""
    try:
        completed = subprocess.run(
            [
                "nvidia-smi",
                "--query-compute-apps=pid,used_memory",
                "--format=csv,noheader,nounits",
            ],
            check=False,
            capture_output=True,
            text=True,
            timeout=2,
        )
        if completed.returncode != 0:
            return None
        current = os.getpid()
        values: list[int] = []
        for line in completed.stdout.splitlines():
            fields = [part.strip() for part in line.split(",")]
            if len(fields) >= 2 and fields[0].isdigit() and int(fields[0]) == current:
                values.append(int(float(fields[1])) * 1024 * 1024)
        return max(values) if values else None
    except (OSError, ValueError, subprocess.SubprocessError):
        return None
