"""Prove one NVIDIA model can actually be loaded and can actually recognise speech, then exit.

Downloading a 2.5 GB checkpoint tells you the bytes are right and nothing else. This runs inside
the provisioned Linux runtime, restores the model onto the GPU, transcribes a short synthesised
signal, records what the card was holding while it did, and leaves. Only after that has happened
does EchoForge call an NVIDIA model installed.

The audio is generated here rather than shipped: a fixed tone sequence is enough to prove the
pipeline runs end to end without adding a binary fixture that would have to be licensed, hashed and
kept. What is asserted is that the model loaded, ran, and returned a hypothesis object of the
expected shape - never that it produced particular words, which a tone cannot support.

    python -m echoforge_worker.nemo_smoke

Reads ``ECHOFORGE_NEMO_MODEL_DIR`` and prints one JSON object. Exit code 0 means qualified.
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

from .nemo_backend import (
    CANARY_ID,
    EXPECTED_SAMPLE_RATE,
    PARAKEET_ID,
    _load_model,
    _hypothesis_text,
    production_stack_available,
)

#: Long enough that the model has something to do, short enough that a smoke test stays a smoke
#: test. Comfortably inside Canary's verified 40-second training window.
PROBE_SECONDS = 4.0


class _Options:
    """The shape ``_load_model`` reads. Deliberately not the real request type: nothing here has a
    session, a transcript, or anything belonging to a user."""

    def __init__(self, model_id: str, model_path: str) -> None:
        self.model_id = model_id
        self.model_path = model_path
        self.compute_profile = "cuda-fp16"


def _probe_wave(path: Path) -> None:
    """A short, non-silent 16 kHz mono signal. Speech-shaped enough to exercise the front end."""
    frames = int(EXPECTED_SAMPLE_RATE * PROBE_SECONDS)
    samples = bytearray()
    for index in range(frames):
        t = index / EXPECTED_SAMPLE_RATE
        # Two formant-ish tones with an envelope, so the feature extractor sees something with
        # structure rather than a constant.
        value = 0.30 * math.sin(2 * math.pi * 140 * t) + 0.18 * math.sin(2 * math.pi * 720 * t)
        value *= 0.5 * (1 - math.cos(2 * math.pi * min(t / PROBE_SECONDS, 1.0)))
        samples += struct.pack("<h", max(-32768, min(32767, int(value * 20000))))

    with wave.open(str(path), "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(EXPECTED_SAMPLE_RATE)
        handle.writeframes(bytes(samples))


def main() -> int:
    result: dict = {"ready": False}

    directory = os.environ.get("ECHOFORGE_NEMO_MODEL_DIR", "")
    if not directory or not Path(directory).is_dir():
        result["error"] = "ECHOFORGE_NEMO_MODEL_DIR does not name a directory"
        print(json.dumps(result), flush=True)
        return 1

    if not production_stack_available():
        result["error"] = "this interpreter is not EchoForge's pinned Linux NeMo runtime"
        print(json.dumps(result), flush=True)
        return 1

    # Which model this directory holds is decided by what is in it, not by an argument that could
    # disagree with it.
    model_id = PARAKEET_ID if list(Path(directory).glob("*.nemo")) else CANARY_ID
    result["model_id"] = model_id

    try:
        model, torch, versions = _load_model(_Options(model_id, directory))
    except Exception as error:  # noqa: BLE001 - a qualification reports, never raises
        result["error"] = f"{type(error).__name__}: {error}"
        print(json.dumps(result), flush=True)
        return 1

    result["versions"] = versions
    result["device"] = torch.cuda.get_device_name(0)

    try:
        with tempfile.TemporaryDirectory(prefix="echoforge-nemo-smoke-") as scratch:
            audio = Path(scratch) / "probe.wav"
            _probe_wave(audio)

            torch.cuda.reset_peak_memory_stats()
            with torch.inference_mode():
                if model_id == PARAKEET_ID:
                    hypotheses = model.transcribe([str(audio)], batch_size=1)
                    produced = hypotheses is not None and len(list(hypotheses)) > 0
                    sample = _hypothesis_text(list(hypotheses)[0]) if produced else ""
                else:
                    # The same shape the transcription path uses. Canary's SALM is prompted with a
                    # chat turn carrying an audio locator, not with a bare file path, and asking it
                    # differently here would qualify a call EchoForge never makes.
                    prompts = [[{
                        "role": "user",
                        "content": f"Transcribe the following: {model.audio_locator_tag}",
                        "audio": [str(audio)],
                    }]]
                    answer_ids = model.generate(prompts=prompts, max_new_tokens=32)
                    produced = answer_ids is not None and len(answer_ids) > 0
                    sample = model.tokenizer.ids_to_text(answer_ids[0].cpu()).strip() if produced else ""

            result["peak_vram_bytes"] = int(torch.cuda.max_memory_allocated())
            # The words are not asserted - a tone has none. That the model returned output of the
            # expected shape is exactly what a smoke test can honestly claim.
            result["produced_output"] = produced
            result["sample"] = sample[:60]
    except Exception as error:  # noqa: BLE001
        result["error"] = f"{type(error).__name__}: {error}"
        print(json.dumps(result), flush=True)
        return 1
    finally:
        # The process boundary is the real guarantee, but releasing here means the number above is
        # the model's own footprint rather than whatever the allocator happened to be holding.
        del model
        torch.cuda.empty_cache()

    result["ready"] = bool(result.get("produced_output"))
    print(json.dumps(result), flush=True)
    return 0 if result["ready"] else 1


if __name__ == "__main__":
    sys.exit(main())
