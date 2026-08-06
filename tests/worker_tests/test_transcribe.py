"""What the transcript must be true of, whatever backend produced it."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

from conftest import (
    hello_line,
    make_request,
    run_worker,
    simple_session,
    start_job_line,
    write_wav,
)

from echoforge_worker.models import (
    SPEAKER_REMOTE_ID,
    SPEAKER_REMOTE_NAME,
    SPEAKER_YOU_ID,
    SPEAKER_YOU_NAME,
)


def transcribe(tmp_path: Path, **kwargs) -> tuple[dict, dict]:
    """Run a normal job and return (result message, transcript document)."""
    request = simple_session(tmp_path, **kwargs)
    run = run_worker([hello_line(), start_job_line(request)])
    result = run.terminal()
    assert result["type"] == "result", result
    document = json.loads(Path(result["output_path"]).read_text(encoding="utf-8"))
    return result, document


# -- attribution -------------------------------------------------------------------------


def test_microphone_content_is_always_you_and_system_content_is_always_remote(tmp_path) -> None:
    _, document = transcribe(tmp_path)

    assert document["segments"], "the fixture audio produced no segments"
    for segment in document["segments"]:
        if segment["source_track"] == "microphone":
            assert segment["speaker_id"] == SPEAKER_YOU_ID
            assert segment["speaker_name"] == SPEAKER_YOU_NAME
        else:
            assert segment["speaker_id"] == SPEAKER_REMOTE_ID
            assert segment["speaker_name"] == SPEAKER_REMOTE_NAME

    speakers = {s["source_track"]: s for s in document["speakers"]}
    assert speakers["microphone"]["name"] == "You"
    assert speakers["system"]["name"] == "Remote"


def test_attribution_cannot_be_overridden_by_the_request(tmp_path) -> None:
    """There is no field for it, so inventing one changes nothing."""
    request = simple_session(tmp_path)
    request["tracks"][0]["speaker_name"] = "Alex"

    run = run_worker([hello_line(), start_job_line(request)])

    # The extra field is not read. Attribution is derived from the track and from nothing
    # else, so there is no path by which a caller could reach it.
    terminal = run.terminal()
    assert terminal["type"] == "result"
    document = json.loads(Path(terminal["output_path"]).read_text(encoding="utf-8"))
    assert all(s["speaker_name"] in {"You", "Remote"} for s in document["segments"])


# -- ordering and identity ---------------------------------------------------------------


def test_segments_are_ordered_and_uniquely_identified(tmp_path) -> None:
    _, document = transcribe(tmp_path)

    segments = document["segments"]
    ids = [s["id"] for s in segments]
    assert len(set(ids)) == len(ids)
    assert ids == sorted(ids)

    ranks = {"microphone": 0, "system": 1}
    keys = [(s["start_seconds"], s["end_seconds"], ranks[s["source_track"]]) for s in segments]
    assert keys == sorted(keys)


def test_word_timestamps_are_ordered_and_contained_by_their_segment(tmp_path) -> None:
    _, document = transcribe(tmp_path)

    for segment in document["segments"]:
        words = segment["words"]
        assert words, f"{segment['id']} has no words"
        previous = -1.0
        for word in words:
            assert word["start_seconds"] >= previous - 1e-9
            assert word["end_seconds"] >= word["start_seconds"]
            assert word["start_seconds"] >= segment["start_seconds"] - 1e-9
            assert word["end_seconds"] <= segment["end_seconds"] + 1e-9
            previous = word["start_seconds"]

        assert words[0]["start_seconds"] == segment["start_seconds"]
        assert words[-1]["end_seconds"] == segment["end_seconds"]


def test_segment_times_stay_inside_their_epoch(tmp_path) -> None:
    _, document = transcribe(tmp_path)

    epochs = {e["index"]: e for e in document["epochs"]}
    for segment in document["segments"]:
        epoch = epochs[segment["epoch"]]
        assert segment["start_seconds"] >= epoch["start_seconds"] - 1e-9
        assert segment["end_seconds"] <= epoch["end_seconds"] + 1e-9
        assert segment["end_seconds"] <= document["duration_seconds"] + 1e-9


def test_a_second_epoch_places_its_segments_after_the_first(tmp_path) -> None:
    session_root = tmp_path / "session"
    first = "tracks/microphone/chunks/000001.wav"
    second = "tracks/microphone/chunks/000002.wav"
    frames = write_wav(session_root / first, seconds=2.0, seed=1)
    write_wav(session_root / second, seconds=2.0, seed=3)

    request = make_request(
        session_root=session_root,
        output_path=tmp_path / "transcript.json",
        tracks={"microphone": []},
        duration_seconds=5.0,
        epochs=[
            {"index": 1, "start_seconds": 0.0, "end_seconds": 2.0},
            {"index": 2, "start_seconds": 3.0, "end_seconds": 5.0},
        ],
    )
    request["tracks"] = [
        {
            "source_track": "microphone",
            "chunks": [
                {
                    "index": 1,
                    "epoch": 1,
                    "relative_path": first,
                    "start_seconds": 0.0,
                    "end_seconds": 2.0,
                    "sample_rate": 8000,
                    "channels": 1,
                    "frames": frames,
                },
                {
                    "index": 2,
                    "epoch": 2,
                    "relative_path": second,
                    "start_seconds": 3.0,
                    "end_seconds": 5.0,
                    "sample_rate": 8000,
                    "channels": 1,
                    "frames": frames,
                },
            ],
        }
    ]

    run = run_worker([hello_line(), start_job_line(request)])
    document = json.loads(Path(run.terminal()["output_path"]).read_text(encoding="utf-8"))

    by_epoch = {1: [], 2: []}
    for segment in document["segments"]:
        by_epoch[segment["epoch"]].append(segment)

    assert by_epoch[1] and by_epoch[2]
    # The gap between epochs is real: nothing may be placed inside it.
    assert max(s["end_seconds"] for s in by_epoch[1]) <= 2.0
    assert min(s["start_seconds"] for s in by_epoch[2]) >= 3.0


# -- cross-track overlap -------------------------------------------------------------------


def test_overlaps_are_recorded_across_tracks_and_never_within_one(tmp_path) -> None:
    _, document = transcribe(tmp_path)

    tracks = {s["id"]: s["source_track"] for s in document["segments"]}
    linked = 0
    for segment in document["segments"]:
        for other in segment["overlaps_segment_ids"]:
            assert other != segment["id"]
            assert tracks[other] != segment["source_track"]
            linked += 1

    # Both fixture tracks span the same three seconds, so they must have found each other.
    assert linked > 0


# -- silence and emptiness -----------------------------------------------------------------


def test_silent_audio_produces_a_valid_transcript_with_no_segments(tmp_path) -> None:
    result, document = transcribe(tmp_path, silent=True)

    assert document["segments"] == []
    assert result["segment_count"] == 0
    assert document["duration_seconds"] == 3.0
    # The tracks still exist even though nobody spoke.
    assert {s["source_track"] for s in document["speakers"]} == {"microphone", "system"}


def test_a_session_with_no_chunks_at_all_still_produces_a_transcript(tmp_path) -> None:
    request = make_request(
        session_root=tmp_path / "session",
        output_path=tmp_path / "transcript.json",
        tracks={"microphone": [], "system": []},
        duration_seconds=0.0,
    )
    (tmp_path / "session").mkdir(parents=True, exist_ok=True)

    run = run_worker([hello_line(), start_job_line(request)])
    result = run.terminal()

    assert result["type"] == "result"
    assert result["segment_count"] == 0


# -- determinism ---------------------------------------------------------------------------


def test_identical_input_produces_a_byte_identical_transcript(tmp_path) -> None:
    first_result, _ = transcribe(tmp_path / "run-a")
    second_result, _ = transcribe(tmp_path / "run-b")

    # Paths differ, so compare the content rather than the message.
    first = Path(first_result["output_path"]).read_bytes()
    second = Path(second_result["output_path"]).read_bytes()
    assert first == second
    assert first_result["sha256"] == second_result["sha256"]


def test_the_reported_digest_is_the_digest_of_the_file_that_was_written(tmp_path) -> None:
    result, _ = transcribe(tmp_path)

    written = Path(result["output_path"]).read_bytes()
    assert hashlib.sha256(written).hexdigest() == result["sha256"]


def test_different_audio_produces_a_different_transcript(tmp_path) -> None:
    _, quiet = transcribe(tmp_path / "quiet", silent=True)
    _, loud = transcribe(tmp_path / "loud")

    assert quiet["segments"] != loud["segments"]


def test_the_source_manifest_digest_covers_the_chunk_identities(tmp_path) -> None:
    request = simple_session(tmp_path / "a")
    run = run_worker([hello_line(), start_job_line(request)])
    document = json.loads(Path(run.terminal()["output_path"]).read_text(encoding="utf-8"))

    changed = simple_session(tmp_path / "b")
    changed["tracks"][0]["chunks"][0]["sha256"] = "a" * 64
    changed_run = run_worker([hello_line(), start_job_line(changed)])
    changed_document = json.loads(
        Path(changed_run.terminal()["output_path"]).read_text(encoding="utf-8")
    )

    assert document["source_manifest_sha256"] != changed_document["source_manifest_sha256"]


# -- the sources are left alone --------------------------------------------------------------


def test_source_audio_bytes_and_hashes_are_unchanged_by_transcription(tmp_path) -> None:
    request = simple_session(tmp_path)
    session_root = Path(request["session_root"])

    before = {
        path: (path.stat().st_mtime_ns, hashlib.sha256(path.read_bytes()).hexdigest())
        for path in sorted(session_root.rglob("*"))
        if path.is_file()
    }
    assert before, "the fixture wrote no source files"

    run = run_worker([hello_line(), start_job_line(request)])
    assert run.terminal()["type"] == "result"

    after = {
        path: (path.stat().st_mtime_ns, hashlib.sha256(path.read_bytes()).hexdigest())
        for path in sorted(session_root.rglob("*"))
        if path.is_file()
    }
    assert after == before

    # And nothing new appeared beside the sources either.
    assert set(after) == set(before)


def test_the_transcript_is_written_atomically_and_leaves_no_staging_file(tmp_path) -> None:
    result, _ = transcribe(tmp_path)

    output = Path(result["output_path"])
    assert output.is_file()
    assert not output.with_name(output.name + ".partial").exists()


# -- honesty about the placeholder ------------------------------------------------------------


def test_the_placeholder_backend_says_it_does_not_recognise_speech(tmp_path) -> None:
    request = simple_session(tmp_path)
    run = run_worker([hello_line(), start_job_line(request)])

    assert run.first("started")["recognizes_speech"] is False

    document = json.loads(Path(run.terminal()["output_path"]).read_text(encoding="utf-8"))
    assert document["model"]["recognizes_speech"] is False
    assert document["model"]["backend"] == "mock"
    # Every segment carries the marker, so its text cannot be mistaken for a transcript.
    assert all(s["text"].startswith("[mock]") for s in document["segments"])


def test_confidence_is_null_because_no_calibrated_score_exists(tmp_path) -> None:
    _, document = transcribe(tmp_path)

    for segment in document["segments"]:
        assert segment["confidence"] is None
        assert all(word["probability"] is None for word in segment["words"])


def test_the_language_is_undetermined_rather_than_guessed(tmp_path) -> None:
    _, document = transcribe(tmp_path)

    assert {language["code"] for language in document["languages"]} == {"und"}
    assert all(language["probability"] is None for language in document["languages"])
