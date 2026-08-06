"""A worker that reports a transcript it never wrote.

Nothing about the message is malformed. The file simply is not there, and a host that
activated the revision anyway would record a transcript revision pointing at nothing.
"""

import json
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

    sys.stdout.write(
        '{"protocol_version":1,"type":"started","job_id":"%s",'
        '"backend":"mock","recognizes_speech":false}\n' % job_id
    )
    sys.stdout.write(
        '{"protocol_version":1,"type":"result","job_id":"%s","output_path":%s,'
        '"sha256":"%s","segment_count":0,"duration_seconds":3.0}\n'
        % (job_id, json.dumps(output_path), "0" * 64)
    )
    sys.stdout.flush()
    return 0


if __name__ == "__main__":
    sys.exit(main())
