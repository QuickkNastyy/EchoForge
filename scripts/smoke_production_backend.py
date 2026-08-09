"""Drive the real faster-whisper backend once, over a synthetic two-window recording.

Speech is not synthesised here, so what this proves is the machinery rather than the accuracy:
the pinned model loads from the verified directory with the Hub switched off, CTranslate2 runs
on this hardware, the chosen compute profile is honoured or fallen back from with a recorded
reason, and whatever the recogniser returns survives rebasing, gap handling, and
de-duplication into a transcript that validates.

Recognising near-silence should produce nothing. That is the correct answer and the one thing
a smoke test over synthetic audio can genuinely check: a model that invents words for silence
is a model doing the wrong thing.
"""

from __future__ import annotations

import json
import math
import os
import struct
import sys
import tempfile
import wave
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "worker"))

from echoforge_worker import compute  # noqa: E402
from echoforge_worker.models import RequestDerivative, RequestOptions, RequestWindow  # noqa: E402
from echoforge_worker.whisper_backend import FasterWhisperBackend  # noqa: E402

SAMPLE_RATE = 16000
SECONDS = 25.0


class Context:
    def __init__(self, root: Path) -> None:
        self.session_root = str(root)
        self.session_duration_seconds = SECONDS
        self.windows_run = 0

    def check_cancelled(self) -> None:
        return None

    def window_started(self, window) -> None:
        print(f"    window {window.id} [{window.session_start_seconds:.1f}s - {window.session_end_seconds:.1f}s]")

    def window_completed(self, window, segments: int) -> None:
        self.windows_run += 1
        print(f"      -> {segments} segments")

    def warn(self, code: str, detail: str) -> None:
        print(f"    warning {code}: {detail}")


def write_fixture(root: Path) -> None:
    directory = root / "derived" / "audio" / "derivative-v1"
    directory.mkdir(parents=True, exist_ok=True)

    frames = int(SECONDS * SAMPLE_RATE)
    payload = bytearray()
    for frame in range(frames):
        # A quiet, slowly varying tone. Not speech: a recogniser that returns words for this
        # is hallucinating, which is exactly what the VAD filter exists to prevent.
        value = int(600 * math.sin(2 * math.pi * 120 * frame / SAMPLE_RATE))
        payload += struct.pack("<h", value)

    with wave.open(str(directory / "microphone.wav"), "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(SAMPLE_RATE)
        handle.writeframes(bytes(payload))

    (directory / "microphone.timing.json").write_text(
        json.dumps(
            {
                "sample_rate": SAMPLE_RATE,
                "total_frames": frames,
                "spans": [
                    {
                        "kind": "source",
                        "derivative_frame": 0,
                        "frames": frames,
                        "epoch": 1,
                        "session_start_seconds": 0.0,
                        "session_end_seconds": SECONDS,
                    }
                ],
            }
        ),
        encoding="utf-8",
    )


def main() -> int:
    model_path = os.environ.get("ECHOFORGE_SMOKE_MODEL")
    if not model_path:
        print("ECHOFORGE_SMOKE_MODEL is not set", file=sys.stderr)
        return 2

    print(f"faster-whisper stack: {compute.runtime_versions()}")
    devices = compute.cuda_device_count()
    print(f"CUDA devices visible to CTranslate2: {devices}")

    requested = compute.CUDA_FP16 if devices > 0 else compute.CPU_INT8
    print(f"requested profile: {requested}")

    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        write_fixture(root)

        derivative = RequestDerivative(
            source_track="microphone",
            relative_path="derived/audio/derivative-v1/microphone.wav",
            timing_map_relative_path="derived/audio/derivative-v1/microphone.timing.json",
            sample_rate=SAMPLE_RATE,
            channels=1,
            total_frames=int(SECONDS * SAMPLE_RATE),
            sha256="0" * 64,
        )

        # Two windows with a five-second overlap, exactly as the planner produces.
        windows = [
            RequestWindow(
                id="w-microphone-e001-0000",
                source_track="microphone",
                epoch=1,
                ordinal=0,
                start_frame=0,
                end_frame=int(15 * SAMPLE_RATE),
                session_start_seconds=0.0,
                session_end_seconds=15.0,
                overlap_after_seconds=5.0,
                input_fingerprint="smoke-0",
            ),
            RequestWindow(
                id="w-microphone-e001-0001",
                source_track="microphone",
                epoch=1,
                ordinal=1,
                start_frame=int(10 * SAMPLE_RATE),
                end_frame=int(SECONDS * SAMPLE_RATE),
                session_start_seconds=10.0,
                session_end_seconds=SECONDS,
                overlap_before_seconds=5.0,
                input_fingerprint="smoke-1",
            ),
        ]

        options = RequestOptions(
            backend="faster-whisper",
            model_id=os.environ.get("ECHOFORGE_SMOKE_MODEL_ID", "whisper-large-v3-turbo"),
            model_revision=os.environ.get("ECHOFORGE_SMOKE_MODEL_REVISION"),
            model_path=model_path,
            compute_profile=requested,
            language="en",
            vad_mode="accuracy",
            vad_filter=False,
            word_timestamps=True,
            allow_cpu_fallback=False,
            window_strategy="whisper-long-v2",
            window_seconds=600.0,
            overlap_seconds=5.0,
            timestamp_capability="word",
            timestamp_precision="word-native",
        )

        backend = FasterWhisperBackend()
        context = Context(root)

        print("running...")
        segments = backend.transcribe_windows("microphone", windows, derivative, options, context)

        outcome = backend.outcome
        assert outcome is not None

        print("")
        print(f"actual profile:   {outcome.plan.describe()}")
        print(f"fell back:        {outcome.fell_back}")
        print(f"fallback reason:  {outcome.fallback_reason or '(none)'}")
        print(f"attempts:         {outcome.attempts}")
        print(f"windows run:      {context.windows_run}")
        print(f"segments:         {len(segments)}")

        for segment in segments[:5]:
            print(f"  [{segment.start_seconds:7.2f} - {segment.end_seconds:7.2f}] {segment.text[:70]}")

        # Whatever came back has to be well formed, in time and in structure.
        for segment in segments:
            assert 0 <= segment.start_seconds <= segment.end_seconds <= SECONDS, segment
            for word in segment.words:
                assert segment.start_seconds <= word.start_seconds <= word.end_seconds <= segment.end_seconds, word

        model = backend.describe(options)
        print(f"model:            {model.model_id} / {model.compute_type} / recognises speech: {model.recognizes_speech}")
        run = backend.run_metadata(options)
        peak = run.get("peak_vram_bytes")
        print(f"peak VRAM:        {peak if peak is not None else 'unavailable under WDDM'}")
        assert model.recognizes_speech is True

    print("")
    print("SMOKE TEST PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
