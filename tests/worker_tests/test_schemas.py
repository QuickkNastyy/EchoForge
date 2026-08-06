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
from conftest import REPO_ROOT, hello_line, run_worker, simple_session, start_job_line
from jsonschema import Draft202012Validator, ValidationError


def _validator(name: str) -> Draft202012Validator:
    # Resolved from this file's own location rather than pytest's rootdir, which moves
    # depending on where the run was started from.
    schema = json.loads((REPO_ROOT / "schemas" / name).read_text(encoding="utf-8"))
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema)


@pytest.fixture(scope="module")
def transcript_validator() -> Draft202012Validator:
    return _validator("transcript.schema.json")


@pytest.fixture(scope="module")
def protocol_validator() -> Draft202012Validator:
    return _validator("worker-protocol.schema.json")


@pytest.fixture(scope="module")
def manifest_validator() -> Draft202012Validator:
    return _validator("artifact-manifest.schema.json")


@pytest.fixture(scope="module")
def manifest() -> dict:
    return json.loads((REPO_ROOT / "artifacts" / "manifest.json").read_text(encoding="utf-8"))


def test_the_pinned_artifact_manifest_validates(manifest, manifest_validator) -> None:
    manifest_validator.validate(manifest)


def test_every_pinned_artifact_names_an_immutable_revision(manifest) -> None:
    """The whole point of pinning is that it cannot be quietly bypassed."""
    mutable = {"main", "master", "latest", "head", "dev", "develop", "trunk", "stable", "newest"}

    for artifact in manifest["artifacts"]:
        revision = artifact["revision"]
        assert revision.lower() not in mutable, artifact["artifact_id"]
        assert len(revision) >= 7, artifact["artifact_id"]
        assert len(artifact["sha256"]) == 64
        assert artifact["size_bytes"] > 0


def test_the_mutable_reference_guard_actually_compiles_and_rejects(manifest, manifest_validator) -> None:
    """A JSON Schema pattern is ECMA-262 and has no inline (?i) flag.

    Written with one, this guard fails to compile rather than rejecting anything -- so the
    schema would claim to forbid a moving reference while permitting every one of them.
    """
    if not manifest["artifacts"]:
        pytest.skip("nothing pinned yet")

    for moving in ("main", "LATEST", "Stable"):
        tampered = dict(manifest)
        tampered["artifacts"] = [dict(manifest["artifacts"][0], revision=moving)]
        with pytest.raises(ValidationError):
            manifest_validator.validate(tampered)


def test_every_pinned_artifact_retains_its_license_text(manifest) -> None:
    """Licence text is collected at pin time, not reconstructed at release."""
    for artifact in manifest["artifacts"]:
        retained = REPO_ROOT / artifact["license_file"]
        assert retained.is_file(), f"{artifact['artifact_id']} -> {artifact['license_file']}"
        assert retained.stat().st_size > 0


def test_every_download_url_is_https_and_names_its_revision(manifest) -> None:
    for artifact in manifest["artifacts"]:
        url = artifact["url"]
        assert url.startswith("https://"), artifact["artifact_id"]
        assert artifact["filename"] in url, artifact["artifact_id"]

        # A package-index URL is content-addressed and immutable by construction; anything
        # else has to name the revision it was pinned to.
        if "files.pythonhosted.org" not in url:
            assert artifact["revision"] in url, artifact["artifact_id"]


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
