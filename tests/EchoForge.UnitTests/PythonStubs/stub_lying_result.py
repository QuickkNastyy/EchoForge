"""A worker that reports a digest for a file it did not write.

The digest is well formed, so nothing about the message itself looks wrong. Only comparing
it against the bytes on disk shows the problem, which is the point: the host does not take
a worker's word for its own output before activating a canonical revision.
"""

import json
import os
import sys


def main() -> int:
    sys.stdin.readline()
    sys.stdout.write(
        '{"protocol_version":1,"type":"ready","worker_version":"stub",'
        '"supported_protocol_versions":[1],"backends":["mock"]}\n'
    )
    sys.stdout.flush()

    message = json.loads(sys.stdin.readline())
    job_id = message["job_id"]
    output_path = message["request"]["output_path"]

    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    with open(output_path, "w", encoding="utf-8") as handle:
        handle.write('{"not":"what was promised"}')

    sys.stdout.write(
        '{"protocol_version":1,"type":"started","job_id":"%s",'
        '"backend":"mock","recognizes_speech":false}\n' % job_id
    )
    sys.stdout.write(
        '{"protocol_version":1,"type":"result","job_id":"%s","output_path":%s,'
        '"sha256":"%s","segment_count":3,"duration_seconds":3.0}\n'
        % (job_id, json.dumps(output_path), "0" * 64)
    )
    sys.stdout.flush()
    return 0


if __name__ == "__main__":
    sys.exit(main())
