"""The production path, proven without a 1.6 GB model on disk.

The recogniser is injected, so everything between "a window of audio" and "a validated
transcript" is exercised for real: slicing the derivative, rebasing onto the session, refusing
to place speech in a gap, removing the overlap, choosing a compute plan, and climbing down when
the GPU will not cooperate. Only the model weights are stood in for.
"""

from __future__ import annotations

import json
import math
import struct
import sys
import wave
from pathlib import Path
from types import SimpleNamespace

import pytest
from conftest import hello_line, run_worker, start_job_line

from echoforge_worker import compute
from echoforge_worker.models import (
    RequestDerivative,
    RequestOptions,
    RequestWindow,
    Segment,
    Word,
)
from echoforge_worker.whisper_backend import (
    FasterWhisperBackend,
    WindowResult,
    build_initial_prompt,
    read_window,
)
from echoforge_worker.windows import WindowSegment, deduplicate, normalize_text, rebase


# --------------------------------------------------------------------------------------
# fixtures
# --------------------------------------------------------------------------------------


def write_derivative(root: Path, seconds: float = 30.0, silent: bool = False) -> Path:
    """A 16 kHz mono derivative, exactly as the host's builder produces."""
    path = root / "derived" / "audio" / "derivative-v1" / "microphone.wav"
    path.parent.mkdir(parents=True, exist_ok=True)

    frames = int(seconds * 16000)
    payload = bytearray()
    for frame in range(frames):
        value = 0 if silent else int(8000 * math.sin(2 * math.pi * 220 * frame / 16000))
        payload += struct.pack("<h", value)

    with wave.open(str(path), "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(16000)
        handle.writeframes(bytes(payload))

    return path


def write_timing_map(root: Path, spans: list[dict]) -> Path:
    path = root / "derived" / "audio" / "derivative-v1" / "microphone.timing.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps({"sample_rate": 16000, "total_frames": 16000 * 30, "spans": spans}),
        encoding="utf-8",
    )
    return path


def source_span(start: float, end: float, epoch: int = 1) -> dict:
    return {
        "kind": "source",
        "derivative_frame": int(start * 16000),
        "frames": int((end - start) * 16000),
        "epoch": epoch,
        "session_start_seconds": start,
        "session_end_seconds": end,
    }


def gap_span(start: float, end: float, epoch: int = 1) -> dict:
    return {**source_span(start, end, epoch), "kind": "gap"}


def window(
    ordinal: int,
    start: float,
    end: float,
    overlap_before: float = 0.0,
    overlap_after: float = 0.0,
    epoch: int = 1,
    fingerprint: str = "",
) -> RequestWindow:
    return RequestWindow(
        input_fingerprint=fingerprint,
        id=f"w-microphone-e{epoch:03d}-{ordinal:04d}",
        source_track="microphone",
        epoch=epoch,
        ordinal=ordinal,
        start_frame=int(start * 16000),
        end_frame=int(end * 16000),
        session_start_seconds=start,
        session_end_seconds=end,
        overlap_before_seconds=overlap_before,
        overlap_after_seconds=overlap_after,
    )


def derivative() -> RequestDerivative:
    return RequestDerivative(
        source_track="microphone",
        relative_path="derived/audio/derivative-v1/microphone.wav",
        timing_map_relative_path="derived/audio/derivative-v1/microphone.timing.json",
        sample_rate=16000,
        channels=1,
        total_frames=16000 * 30,
        sha256="a" * 64,
    )


class Context:
    """Everything the backend is allowed to reach, with the calls recorded."""

    def __init__(self, root: Path, duration: float = 30.0) -> None:
        self.session_root = str(root)
        self.session_duration_seconds = duration
        self.started: list[str] = []
        self.completed: list[tuple[str, int]] = []
        self.warnings: list[tuple[str, str]] = []
        self.cancel_after: int | None = None

    def warn(self, code: str, detail: str) -> None:
        self.warnings.append((code, detail))

    def check_cancelled(self) -> None:
        if self.cancel_after is not None and len(self.started) > self.cancel_after:
            from echoforge_worker.protocol import Cancelled

            raise Cancelled()

    def window_started(self, w: RequestWindow) -> None:
        self.started.append(w.id)

    def window_completed(self, w: RequestWindow, segments: int) -> None:
        self.completed.append((w.id, segments))


def backend_with(script: dict[str, list[WindowSegment]], language: str = "en") -> FasterWhisperBackend:
    """A backend whose recogniser answers from a script rather than a model."""

    def factory(options: RequestOptions, plan: compute.ComputePlan):
        def recognise(samples, w: RequestWindow, opts: RequestOptions) -> WindowResult:
            return WindowResult(
                window_id=w.id,
                segments=tuple(script.get(w.id, [])),
                language=opts.language or language,
                language_probability=0.98,
            )

        return recognise

    return FasterWhisperBackend(recogniser_factory=factory)


def options(**kwargs) -> RequestOptions:
    base = {
        "backend": "faster-whisper",
        "model_path": r"C:\models\large-v3-turbo",
        "compute_profile": compute.CPU_INT8,
    }
    base.update(kwargs)
    return RequestOptions(**base)


# --------------------------------------------------------------------------------------
# reading windows
# --------------------------------------------------------------------------------------


def test_a_window_reads_exactly_its_own_frames(tmp_path) -> None:
    write_derivative(tmp_path)
    from echoforge_worker.audio import read_pcm16

    audio = read_pcm16(tmp_path / "derived" / "audio" / "derivative-v1" / "microphone.wav")
    samples = read_window(audio, window(0, 5.0, 8.0))

    assert len(samples) == 3 * 16000
    assert all(-1.0 <= s <= 1.0 for s in samples[:100])


def test_a_window_past_the_end_of_the_audio_is_clamped(tmp_path) -> None:
    write_derivative(tmp_path, seconds=10.0)
    from echoforge_worker.audio import read_pcm16

    audio = read_pcm16(tmp_path / "derived" / "audio" / "derivative-v1" / "microphone.wav")
    samples = read_window(audio, window(0, 8.0, 60.0))

    # Whatever is in memory past the end of the file is not audio.
    assert len(samples) == 2 * 16000


# --------------------------------------------------------------------------------------
# rebasing
# --------------------------------------------------------------------------------------


def test_window_relative_times_are_rebased_onto_the_session() -> None:
    result = [WindowSegment(1.0, 3.0, "hello there", (Word("hello", 1.0, 1.5), Word("there", 1.5, 3.0)))]

    placed = rebase(window(1, 600.0, 900.0), result, None, 900.0)

    assert len(placed) == 1
    assert placed[0].start_seconds == 601.0
    assert placed[0].end_seconds == 603.0
    assert placed[0].words[0].start_seconds == 601.0
    assert placed[0].words[-1].end_seconds == 603.0


def test_a_result_running_past_its_window_is_clamped_to_it() -> None:
    result = [WindowSegment(0.0, 999.0, "over-eager")]

    placed = rebase(window(0, 10.0, 20.0), result, None, 100.0)

    assert placed[0].end_seconds <= 20.0


def test_nothing_escapes_the_session_duration() -> None:
    placed = rebase(window(0, 0.0, 30.0), [WindowSegment(0.0, 30.0, "long")], None, 12.0)

    assert placed[0].end_seconds <= 12.0


def test_every_word_stays_inside_its_segment() -> None:
    result = [
        WindowSegment(
            2.0, 4.0, "two words",
            (Word("two", 0.0, 3.0), Word("words", 3.0, 99.0)),
        )
    ]

    placed = rebase(window(0, 100.0, 200.0), result, None, 500.0)
    segment = placed[0]

    for word in segment.words:
        assert word.start_seconds >= segment.start_seconds
        assert word.end_seconds <= segment.end_seconds


def test_speech_landing_wholly_in_a_gap_is_dropped(tmp_path) -> None:
    from echoforge_worker.windows import load_timing_map

    timing = load_timing_map(
        json.loads(json.dumps({"sample_rate": 16000, "total_frames": 0, "spans": [
            source_span(0.0, 5.0), gap_span(5.0, 10.0), source_span(10.0, 20.0),
        ]}))
    )

    # A recogniser given ten minutes including a pause will sometimes emit across it.
    placed = rebase(window(0, 0.0, 20.0), [WindowSegment(6.0, 9.0, "nobody said this")], timing, 20.0)

    assert placed == []


def test_speech_straddling_the_edge_of_a_gap_is_trimmed_back(tmp_path) -> None:
    from echoforge_worker.windows import load_timing_map

    timing = load_timing_map(
        json.loads(json.dumps({"sample_rate": 16000, "total_frames": 0, "spans": [
            source_span(0.0, 5.0), gap_span(5.0, 10.0),
        ]}))
    )

    placed = rebase(window(0, 0.0, 20.0), [WindowSegment(4.0, 8.0, "trailing off")], timing, 20.0)

    assert len(placed) == 1
    assert placed[0].end_seconds <= 5.0


def test_empty_text_is_never_emitted() -> None:
    placed = rebase(window(0, 0.0, 10.0), [WindowSegment(1.0, 2.0, "   ")], None, 10.0)

    assert placed == []


# --------------------------------------------------------------------------------------
# overlap de-duplication
# --------------------------------------------------------------------------------------


def _segment(start: float, end: float, text: str, words: tuple[Word, ...] = ()) -> Segment:
    return Segment(
        source_track="microphone",
        epoch=1,
        start_seconds=start,
        end_seconds=end,
        text=text,
        words=words,
    )


def test_an_exact_duplicate_inside_the_overlap_is_removed() -> None:
    windows = [window(0, 0.0, 600.0, overlap_after=5.0), window(1, 595.0, 900.0, overlap_before=5.0)]
    segments = [
        _segment(596.0, 598.0, "we should ship on Friday"),
        _segment(596.0, 598.0, "we should ship on Friday"),
    ]

    kept = deduplicate(segments, windows)

    assert len(kept) == 1


def test_punctuation_and_casing_differences_still_count_as_the_same_speech() -> None:
    windows = [window(0, 0.0, 600.0, overlap_after=5.0), window(1, 595.0, 900.0, overlap_before=5.0)]
    segments = [
        _segment(596.0, 598.0, "We should ship on Friday."),
        _segment(596.2, 598.1, "we should ship on friday"),
    ]

    kept = deduplicate(segments, windows)

    assert len(kept) == 1
    # The original text is preserved; normalisation is only ever used for comparison.
    assert kept[0].text in {"We should ship on Friday.", "we should ship on friday"}


def test_the_better_supported_result_survives() -> None:
    windows = [window(0, 0.0, 600.0, overlap_after=5.0), window(1, 595.0, 900.0, overlap_before=5.0)]
    words = (Word("we", 596.0, 596.4), Word("agreed", 596.4, 598.0))

    segments = [
        _segment(596.0, 598.0, "we agreed"),
        _segment(596.1, 598.0, "we agreed", words),
    ]

    kept = deduplicate(segments, windows)

    assert len(kept) == 1
    assert kept[0].words == words


def test_a_phrase_genuinely_repeated_outside_the_overlap_is_kept() -> None:
    windows = [window(0, 0.0, 600.0, overlap_after=5.0), window(1, 595.0, 900.0, overlap_before=5.0)]

    # "agreed" said twice, minutes apart. Neither is in a shared region.
    segments = [_segment(120.0, 121.0, "agreed"), _segment(700.0, 701.0, "agreed")]

    kept = deduplicate(segments, windows)

    assert len(kept) == 2


def test_different_words_inside_the_overlap_are_both_kept() -> None:
    windows = [window(0, 0.0, 600.0, overlap_after=5.0), window(1, 595.0, 900.0, overlap_before=5.0)]
    segments = [
        _segment(596.0, 598.0, "we should ship on Friday"),
        _segment(596.0, 598.0, "we should skip on Friday"),
    ]

    # Keeping both is the safer error: two windows that heard different words did not
    # necessarily hear the same thing.
    assert len(deduplicate(segments, windows)) == 2


def test_the_same_phrase_far_apart_inside_one_overlap_is_kept() -> None:
    windows = [window(0, 0.0, 600.0, overlap_after=60.0), window(1, 540.0, 900.0, overlap_before=60.0)]
    segments = [_segment(545.0, 546.0, "yes"), _segment(590.0, 591.0, "yes")]

    assert len(deduplicate(segments, windows)) == 2


def test_deduplication_is_deterministic_regardless_of_input_order() -> None:
    windows = [window(0, 0.0, 600.0, overlap_after=5.0), window(1, 595.0, 900.0, overlap_before=5.0)]
    a = _segment(596.0, 598.0, "same words")
    b = _segment(596.1, 598.1, "Same words!")

    forward = deduplicate([a, b], windows)
    backward = deduplicate([b, a], windows)

    assert [(s.start_seconds, s.text) for s in forward] == [(s.start_seconds, s.text) for s in backward]


def test_nothing_is_removed_when_there_is_no_overlap_at_all() -> None:
    windows = [window(0, 0.0, 100.0), window(1, 200.0, 300.0, epoch=2)]
    segments = [_segment(10.0, 11.0, "hello"), _segment(210.0, 211.0, "hello")]

    assert len(deduplicate(segments, windows)) == 2


def test_normalisation_folds_only_what_it_should() -> None:
    assert normalize_text("We should ship, on Friday.") == normalize_text("we should ship on friday")
    assert normalize_text("café") != normalize_text("cafe")
    assert normalize_text("  spaced   out  ") == "spaced out"


# --------------------------------------------------------------------------------------
# the backend end to end
# --------------------------------------------------------------------------------------


def test_the_backend_transcribes_windows_and_places_them_on_the_session(tmp_path) -> None:
    write_derivative(tmp_path)
    write_timing_map(tmp_path, [source_span(0.0, 30.0)])

    windows = [window(0, 0.0, 20.0, overlap_after=5.0), window(1, 15.0, 30.0, overlap_before=5.0)]
    backend = backend_with({
        windows[0].id: [WindowSegment(1.0, 3.0, "first thing", (Word("first", 1.0, 2.0), Word("thing", 2.0, 3.0)))],
        windows[1].id: [WindowSegment(6.0, 8.0, "second thing")],
    })

    context = Context(tmp_path)
    segments = backend.transcribe_windows("microphone", windows, derivative(), options(), context)

    assert [s.text for s in segments] == ["first thing", "second thing"]
    assert segments[0].start_seconds == 1.0
    assert segments[1].start_seconds == 21.0

    # Every window was announced before it ran and recorded after it finished.
    assert context.started == [w.id for w in windows]
    assert [c[0] for c in context.completed] == [w.id for w in windows]


def test_silence_produces_no_segments_rather_than_invented_text(tmp_path) -> None:
    write_derivative(tmp_path, silent=True)
    write_timing_map(tmp_path, [source_span(0.0, 30.0)])

    windows = [window(0, 0.0, 30.0)]
    backend = backend_with({windows[0].id: []})

    segments = backend.transcribe_windows("microphone", windows, derivative(), options(), Context(tmp_path))

    assert segments == []


def test_a_prepared_file_at_the_wrong_rate_is_refused(tmp_path) -> None:
    path = tmp_path / "derived" / "audio" / "derivative-v1" / "microphone.wav"
    path.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "wb") as handle:
        handle.setnchannels(2)
        handle.setsampwidth(2)
        handle.setframerate(48000)
        handle.writeframes(b"\x00\x00" * 4000)

    write_timing_map(tmp_path, [source_span(0.0, 30.0)])

    from echoforge_worker.protocol import WorkerFailure

    with pytest.raises(WorkerFailure) as failure:
        backend_with({}).transcribe_windows(
            "microphone", [window(0, 0.0, 1.0)], derivative(), options(), Context(tmp_path)
        )

    assert "16000 Hz mono was expected" in str(failure.value)


def test_a_missing_timing_map_is_refused_rather_than_guessed_around(tmp_path) -> None:
    write_derivative(tmp_path)

    from echoforge_worker.protocol import WorkerFailure

    with pytest.raises(WorkerFailure) as failure:
        backend_with({}).transcribe_windows(
            "microphone", [window(0, 0.0, 1.0)], derivative(), options(), Context(tmp_path)
        )

    assert "timing map" in str(failure.value)


def test_the_language_the_recogniser_reports_is_carried_through(tmp_path) -> None:
    write_derivative(tmp_path)
    write_timing_map(tmp_path, [source_span(0.0, 30.0)])

    windows = [window(0, 0.0, 10.0)]
    backend = backend_with({windows[0].id: [WindowSegment(1.0, 2.0, "bonjour")]}, language="fr")

    backend.transcribe_windows("microphone", windows, derivative(), options(), Context(tmp_path))

    assert backend.language_for("microphone") == ("fr", 0.98)


def test_an_explicitly_chosen_language_overrides_detection(tmp_path) -> None:
    write_derivative(tmp_path)
    write_timing_map(tmp_path, [source_span(0.0, 30.0)])

    windows = [window(0, 0.0, 10.0)]
    backend = backend_with({windows[0].id: [WindowSegment(1.0, 2.0, "hallo")]}, language="fr")

    backend.transcribe_windows(
        "microphone", windows, derivative(), options(language="de"), Context(tmp_path)
    )

    assert backend.language_for("microphone")[0] == "de"


def test_a_glossary_becomes_an_initial_prompt() -> None:
    prompt = build_initial_prompt(options(initial_prompt="A planning call", glossary=("EchoForge", "WASAPI")))

    assert prompt is not None
    assert "EchoForge" in prompt and "WASAPI" in prompt and "A planning call" in prompt


def test_no_glossary_and_no_prompt_means_no_prompt() -> None:
    assert build_initial_prompt(options()) is None


def test_cancellation_stops_between_windows(tmp_path) -> None:
    write_derivative(tmp_path)
    write_timing_map(tmp_path, [source_span(0.0, 30.0)])

    windows = [window(0, 0.0, 10.0), window(1, 10.0, 20.0), window(2, 20.0, 30.0)]
    backend = backend_with({w.id: [WindowSegment(1.0, 2.0, f"line {i}")] for i, w in enumerate(windows)})

    context = Context(tmp_path)

    # Cancellation is observed at the top of each window, so the two already under way finish
    # and only the third is refused.
    context.cancel_after = 1

    from echoforge_worker.protocol import Cancelled

    with pytest.raises(Cancelled):
        backend.transcribe_windows("microphone", windows, derivative(), options(), context)

    # The windows that did finish still finished: cancelling costs the rest, not the lot.
    assert [c[0] for c in context.completed] == [windows[0].id, windows[1].id]
    assert windows[2].id not in context.started


def test_a_finished_window_is_checkpointed_and_reused_on_the_next_run(tmp_path) -> None:
    write_derivative(tmp_path)
    write_timing_map(tmp_path, [source_span(0.0, 30.0)])

    windows = [window(0, 0.0, 10.0, fingerprint="fp-0"), window(1, 10.0, 20.0, fingerprint="fp-1")]

    calls: list[str] = []

    def factory(opts, plan):
        def recognise(samples, w, o):
            calls.append(w.id)
            return WindowResult(w.id, (WindowSegment(1.0, 2.0, f"line {w.ordinal}"),), "en", 0.9)

        return recognise

    first = FasterWhisperBackend(recogniser_factory=factory)
    first.transcribe_windows("microphone", windows, derivative(), options(), Context(tmp_path))

    assert calls == [windows[0].id, windows[1].id]

    # A second run over identical inputs recognises nothing again.
    second = FasterWhisperBackend(recogniser_factory=factory)
    segments = second.transcribe_windows("microphone", windows, derivative(), options(), Context(tmp_path))

    assert calls == [windows[0].id, windows[1].id]
    assert [s.text for s in segments] == ["line 0", "line 1"]


def test_a_checkpoint_from_different_audio_is_not_reused(tmp_path) -> None:
    write_derivative(tmp_path)
    write_timing_map(tmp_path, [source_span(0.0, 30.0)])

    original = window(0, 0.0, 10.0, fingerprint="fp-old")
    calls: list[str] = []

    def factory(opts, plan):
        def recognise(samples, w, o):
            calls.append(w.input_fingerprint)
            return WindowResult(w.id, (WindowSegment(1.0, 2.0, "hello"),), "en", 0.9)

        return recognise

    FasterWhisperBackend(recogniser_factory=factory).transcribe_windows(
        "microphone", [original], derivative(), options(), Context(tmp_path)
    )

    # Same window ID, different fingerprint: the audio behind it changed.
    changed = window(0, 0.0, 10.0, fingerprint="fp-new")
    FasterWhisperBackend(recogniser_factory=factory).transcribe_windows(
        "microphone", [changed], derivative(), options(), Context(tmp_path)
    )

    assert calls == ["fp-old", "fp-new"]


def test_a_window_that_failed_does_not_lose_the_ones_that_succeeded(tmp_path) -> None:
    write_derivative(tmp_path)
    write_timing_map(tmp_path, [source_span(0.0, 30.0)])

    windows = [window(i, i * 10.0, (i + 1) * 10.0, fingerprint=f"fp-{i}") for i in range(3)]

    attempts: list[str] = []

    def failing(opts, plan):
        def recognise(samples, w, o):
            attempts.append(w.id)
            if w.ordinal == 2:
                raise RuntimeError("the third window fell over")
            return WindowResult(w.id, (WindowSegment(1.0, 2.0, f"line {w.ordinal}"),), "en", 0.9)

        return recognise

    with pytest.raises(RuntimeError):
        FasterWhisperBackend(recogniser_factory=failing).transcribe_windows(
            "microphone", windows, derivative(), options(), Context(tmp_path)
        )

    def succeeding(opts, plan):
        def recognise(samples, w, o):
            attempts.append(w.id)
            return WindowResult(w.id, (WindowSegment(1.0, 2.0, f"line {w.ordinal}"),), "en", 0.9)

        return recognise

    segments = FasterWhisperBackend(recogniser_factory=succeeding).transcribe_windows(
        "microphone", windows, derivative(), options(), Context(tmp_path)
    )

    # The retry only re-ran the window that failed.
    assert attempts == [windows[0].id, windows[1].id, windows[2].id, windows[2].id]
    assert len(segments) == 3


def test_the_model_is_described_from_what_actually_ran(tmp_path) -> None:
    write_derivative(tmp_path)
    write_timing_map(tmp_path, [source_span(0.0, 30.0)])

    backend = backend_with({})
    backend.transcribe_windows("microphone", [window(0, 0.0, 5.0)], derivative(), options(), Context(tmp_path))

    model = backend.describe(options())

    assert model.recognizes_speech is True
    assert model.backend == "faster-whisper"
    assert model.model_id == "large-v3-turbo"
    assert model.compute_type == "int8"


def test_a_backend_without_a_verified_model_directory_refuses(tmp_path) -> None:
    write_derivative(tmp_path)
    write_timing_map(tmp_path, [source_span(0.0, 30.0)])

    from echoforge_worker.protocol import WorkerFailure

    with pytest.raises(WorkerFailure) as failure:
        backend_with({}).transcribe_windows(
            "microphone",
            [window(0, 0.0, 5.0)],
            derivative(),
            RequestOptions(backend="faster-whisper", compute_profile=compute.CPU_INT8),
            Context(tmp_path),
        )

    assert "verified model directory" in str(failure.value)


# --------------------------------------------------------------------------------------
# compute profiles and fallback
# --------------------------------------------------------------------------------------


def test_adapter_enumeration_without_loadable_cuda_libraries_is_not_usable(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setitem(
        sys.modules,
        "ctranslate2",
        SimpleNamespace(get_cuda_device_count=lambda: 1),
    )
    monkeypatch.setattr(compute, "cuda_libraries_loadable", lambda: False)

    assert compute.cuda_device_count() == 0


def test_the_private_cuda_directory_stays_active_for_the_worker_lifetime(
    monkeypatch: pytest.MonkeyPatch, tmp_path: Path
) -> None:
    private = tmp_path / "nvidia" / "cublas" / "bin"
    private.mkdir(parents=True)
    handles: list[object] = []

    class Handle:
        pass

    def add_dll_directory(path: str) -> Handle:
        assert Path(path) == private
        handle = Handle()
        handles.append(handle)
        return handle

    monkeypatch.setattr(compute.sys, "platform", "win32")
    monkeypatch.setattr(compute, "_packaged_cuda_directories", lambda: (private,))
    monkeypatch.setattr(compute.os, "add_dll_directory", add_dll_directory, raising=False)
    monkeypatch.setattr(compute, "_CUDA_DLL_DIRECTORY_HANDLES", [])
    monkeypatch.setattr(compute, "_CUDA_DLL_DIRECTORIES", [])
    monkeypatch.setattr(compute, "_CUDA_DLL_SEARCH_CONFIGURED", False)

    assert compute.configure_cuda_dll_search() == (str(private),)
    assert compute.configure_cuda_dll_search() == (str(private),)
    assert compute._CUDA_DLL_DIRECTORY_HANDLES == handles
    assert len(handles) == 1


def test_a_gpu_profile_falls_back_to_the_cpu_when_no_device_is_visible() -> None:
    seen: list[compute.ComputePlan] = []

    result, outcome = compute.run_with_fallback(
        compute.CUDA_FP16, lambda plan: seen.append(plan) or "ran", cuda_devices=0
    )

    assert result == "ran"
    assert outcome.plan.device == "cpu"
    assert outcome.fell_back
    assert "no CUDA device" in (outcome.fallback_reason or "")
    assert len(seen) == 1


def test_an_out_of_memory_failure_retries_with_a_smaller_batch() -> None:
    attempts: list[int] = []

    def attempt(plan: compute.ComputePlan) -> str:
        attempts.append(plan.batch_size)
        if plan.device == "cuda" and plan.batch_size > 2:
            raise RuntimeError("CUDA failed with error out of memory")
        return "ran"

    result, outcome = compute.run_with_fallback(compute.CUDA_INT8_FLOAT16, attempt, cuda_devices=1)

    assert result == "ran"
    assert attempts == [8, 4, 2]
    assert outcome.plan.device == "cuda"
    assert outcome.plan.batch_size == 2
    assert not outcome.fell_back


def test_a_gpu_that_runs_out_of_memory_at_every_batch_ends_on_the_cpu() -> None:
    def attempt(plan: compute.ComputePlan) -> str:
        if plan.device == "cuda":
            raise RuntimeError("out of memory")
        return "cpu ran"

    result, outcome = compute.run_with_fallback(compute.CUDA_FP16, attempt, cuda_devices=1)

    assert result == "cpu ran"
    assert outcome.plan.profile == compute.CPU_INT8
    assert outcome.fell_back
    assert "falling back to the CPU" in (outcome.fallback_reason or "")


def test_a_gpu_initialisation_failure_skips_straight_to_the_cpu() -> None:
    attempts: list[str] = []

    def attempt(plan: compute.ComputePlan) -> str:
        attempts.append(plan.device)
        if plan.device == "cuda":
            raise RuntimeError("Library cudnn_ops_infer.dll is not found")
        return "cpu ran"

    result, outcome = compute.run_with_fallback(compute.CUDA_FP16, attempt, cuda_devices=1)

    assert result == "cpu ran"
    # One GPU attempt, not four: a missing library will not be fixed by a smaller batch.
    assert attempts == ["cuda", "cpu"]
    assert "could not be used" in (outcome.fallback_reason or "")


def test_a_cpu_failure_is_final_and_is_not_swallowed() -> None:
    def attempt(plan: compute.ComputePlan) -> str:
        raise RuntimeError("the model file is corrupt")

    with pytest.raises(RuntimeError):
        compute.run_with_fallback(compute.CPU_INT8, attempt, cuda_devices=0)


def test_a_cpu_profile_never_tries_a_gpu_even_when_one_exists() -> None:
    plans = compute.plans_for(compute.CPU_INT8, cuda_devices=4)

    assert len(plans) == 1
    assert plans[0].device == "cpu"


def test_the_outcome_records_what_was_asked_for_and_what_ran() -> None:
    _, outcome = compute.run_with_fallback(compute.CUDA_FP16, lambda plan: "ran", cuda_devices=0)

    assert outcome.requested_profile == compute.CUDA_FP16
    assert outcome.plan.profile == compute.CPU_INT8
    assert outcome.attempts
    assert "faster_whisper" in outcome.runtime_versions


def test_out_of_memory_is_recognised_from_what_the_libraries_actually_say() -> None:
    for message in (
        "CUDA failed with error out of memory",
        "CUBLAS_STATUS_ALLOC_FAILED",
        "RuntimeError: CUDA_ERROR_OUT_OF_MEMORY",
    ):
        assert compute.is_out_of_memory(RuntimeError(message)), message

    assert not compute.is_out_of_memory(RuntimeError("file not found"))


def test_an_unknown_profile_is_refused() -> None:
    with pytest.raises(ValueError):
        compute.plans_for("cuda-fp32", cuda_devices=1)


# --------------------------------------------------------------------------------------
# the worker still advertises what it can actually do
# --------------------------------------------------------------------------------------


def test_the_worker_advertises_the_production_backend_only_when_it_is_installed(monkeypatch) -> None:
    from echoforge_worker import transcribe

    monkeypatch.setenv("ECHOFORGE_FORCE_NO_PRODUCTION", "1")
    assert "faster-whisper" not in transcribe.available_backends()
    assert "mock" in transcribe.available_backends()


def test_asking_for_an_uninstalled_backend_is_reported_as_unavailable(tmp_path, monkeypatch) -> None:
    monkeypatch.setenv("ECHOFORGE_FORCE_NO_PRODUCTION", "1")

    session = tmp_path / "session"
    session.mkdir(parents=True, exist_ok=True)

    request = {
        "session_id": "01J",
        "transcript_revision": 1,
        "created_at_utc": "2026-08-06T12:00:00+00:00",
        "session_root": str(session),
        "output_path": str(tmp_path / "out.json"),
        "duration_seconds": 1.0,
        "epochs": [{"index": 1, "start_seconds": 0.0, "end_seconds": 1.0}],
        "tracks": [],
        "options": {"backend": "faster-whisper"},
    }

    run = run_worker([hello_line(), start_job_line(request)], env={"ECHOFORGE_FORCE_NO_PRODUCTION": "1"})

    assert run.terminal()["code"] == "backend_unavailable"
