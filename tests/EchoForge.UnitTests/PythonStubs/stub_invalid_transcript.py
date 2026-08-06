"""A worker that writes a transcript whose times could not have happened.

The file is well-formed JSON, the digest it reports is correct, and the supervisor has no
reason to object. Only validating the content catches it: a segment sitting outside every
capture epoch names a moment when nothing was recorded, so nothing could ever seek to it or
cite it. Activating that as canonical would be worse than failing the job.
"""

import hashlib
import json
import os
import sys


def source_manifest_sha256(request: dict) -> str:
    """The same canonical form the host and the real worker both compute.

    Getting this right matters: a mismatched manifest is refused *before* the transcript is
    validated, and this stub is here to exercise validation, not the manifest check.
    """
    lines = []
    for track in request["tracks"]:
        for chunk in track["chunks"]:
            lines.append(
                f"{track['source_track']}|{chunk['epoch']}|{chunk['index']}|"
                f"{chunk['relative_path']}|{chunk['frames']}|{chunk.get('sha256') or ''}\n"
            )
    return hashlib.sha256("".join(lines).encode("utf-8")).hexdigest()


def main() -> int:
    sys.stdin.readline()
    sys.stdout.write(
        '{"protocol_version":1,"type":"ready","worker_version":"stub",'
        '"supported_protocol_versions":[1],"backends":["mock"]}\n'
    )
    sys.stdout.flush()

    message = json.loads(sys.stdin.readline())
    job_id = message["job_id"]
    request = message["request"]
    output_path = request["output_path"]

    transcript = {
        "schema_version": 1,
        "session_id": request["session_id"],
        "transcript_revision": request["transcript_revision"],
        "created_at_utc": request["created_at_utc"],
        "source_manifest_sha256": source_manifest_sha256(request),
        "duration_seconds": request["duration_seconds"],
        "model": {
            "runtime": "stub",
            "backend": "mock",
            "model_id": "stub",
            "revision": "stub",
            "compute_type": "none",
            "recognizes_speech": False,
            "worker_version": "stub",
        },
        "epochs": request["epochs"],
        "speakers": [
            {"id": "speaker-you", "name": "You", "source_track": "microphone"},
        ],
        "languages": [{"source_track": "microphone", "code": "und", "probability": None}],
        "segments": [
            {
                "id": "segment-000001",
                "epoch": 1,
                # Far beyond the end of the only epoch the session has.
                "start_seconds": 9000.0,
                "end_seconds": 9003.0,
                "speaker_id": "speaker-you",
                "speaker_name": "You",
                "source_track": "microphone",
                "text": "impossible",
                "confidence": None,
                "language": "und",
                "words": [],
                "overlaps_segment_ids": [],
            }
        ],
    }

    payload = json.dumps(transcript, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    with open(output_path, "wb") as handle:
        handle.write(payload)

    sys.stdout.write(
        '{"protocol_version":1,"type":"started","job_id":"%s",'
        '"backend":"mock","recognizes_speech":false}\n' % job_id
    )
    sys.stdout.write(
        '{"protocol_version":1,"type":"result","job_id":"%s","output_path":%s,'
        '"sha256":"%s","segment_count":1,"duration_seconds":%s}\n'
        % (
            job_id,
            json.dumps(output_path),
            hashlib.sha256(payload).hexdigest(),
            json.dumps(request["duration_seconds"]),
        )
    )
    sys.stdout.flush()
    return 0


if __name__ == "__main__":
    sys.exit(main())
