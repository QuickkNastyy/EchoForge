"""Independent schema validation.

The point of these tests is that something other than the code that wrote the file decides
whether it is valid. If the worker were the only judge of its own output, "schema-valid"
would mean nothing more than "self-consistent", and the schema files could drift away from
the implementation without anything noticing.
"""

from __future__ import annotations

import json
from pathlib import Path

import pytest
from conftest import hello_line, run_worker, simple_session, start_job_line
from jsonschema import Draft202012Validator, ValidationError


@pytest.fixture(scope="module")
def transcript_validator(request) -> Draft202012Validator:
    schema = json.loads(
        (Path(request.config.rootdir) / "schemas" / "transcript.schema.json").read_text("utf-8")
    )
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema)


@pytest.fixture(scope="module")
def protocol_validator(request) -> Draft202012Validator:
    schema = json.loads(
        (Path(request.config.rootdir) / "schemas" / "worker-protocol.schema.json").read_text("utf-8")
    )
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema)


def test_the_schemas_are_themselves_valid(transcript_validator, protocol_validator) -> None:
    assert transcript_validator is not None
    assert protocol_validator is not None


def test_a_produced_transcript_validates_against_the_published_schema(
    tmp_path, transcript_validator
) -> None:
    request = simple_session(tmp_path)
    run = run_worker([hello_line(), start_job_line(request)])

    document = json.loads(Path(run.terminal()["output_path"]).read_text(encoding="utf-8"))
    transcript_validator.validate(document)


def test_a_silent_transcript_validates_too(tmp_path, transcript_validator) -> None:
    request = simple_session(tmp_path, silent=True)
    run = run_worker([hello_line(), start_job_line(request)])

    document = json.loads(Path(run.terminal()["output_path"]).read_text(encoding="utf-8"))
    transcript_validator.validate(document)


def test_every_message_the_worker_emits_validates_against_the_protocol_schema(
    tmp_path, protocol_validator
) -> None:
    request = simple_session(tmp_path)
    run = run_worker([hello_line(), start_job_line(request)])

    assert len(run.messages) >= 4
    for message in run.messages:
        protocol_validator.validate(message)


def test_the_messages_the_host_sends_also_validate(tmp_path, protocol_validator) -> None:
    request = simple_session(tmp_path)

    protocol_validator.validate(json.loads(hello_line()))
    protocol_validator.validate(json.loads(start_job_line(request)))
    protocol_validator.validate(
        {"protocol_version": 1, "type": "cancel", "job_id": "job-1", "reason": "user"}
    )


def test_an_error_message_validates(tmp_path, protocol_validator) -> None:
    run = run_worker([hello_line(versions=[7])])

    for message in run.messages:
        protocol_validator.validate(message)


def test_the_schema_rejects_a_speaker_attributed_to_the_wrong_track(
    tmp_path, transcript_validator
) -> None:
    """The schema itself enforces You/Remote, not just the code."""
    request = simple_session(tmp_path)
    run = run_worker([hello_line(), start_job_line(request)])
    document = json.loads(Path(run.terminal()["output_path"]).read_text(encoding="utf-8"))

    for segment in document["segments"]:
        if segment["source_track"] == "microphone":
            segment["speaker_name"] = "Remote"
            break
    else:
        pytest.skip("the fixture produced no microphone segments")

    with pytest.raises(ValidationError):
        transcript_validator.validate(document)


def test_the_schema_rejects_an_unknown_field(tmp_path, transcript_validator) -> None:
    request = simple_session(tmp_path)
    run = run_worker([hello_line(), start_job_line(request)])
    document = json.loads(Path(run.terminal()["output_path"]).read_text(encoding="utf-8"))

    document["notes"] = "added later by something that should not have"

    with pytest.raises(ValidationError):
        transcript_validator.validate(document)


def test_the_schema_rejects_a_malformed_segment_id(tmp_path, transcript_validator) -> None:
    request = simple_session(tmp_path)
    run = run_worker([hello_line(), start_job_line(request)])
    document = json.loads(Path(run.terminal()["output_path"]).read_text(encoding="utf-8"))

    if not document["segments"]:
        pytest.skip("the fixture produced no segments")
    document["segments"][0]["id"] = "seg1"

    with pytest.raises(ValidationError):
        transcript_validator.validate(document)
