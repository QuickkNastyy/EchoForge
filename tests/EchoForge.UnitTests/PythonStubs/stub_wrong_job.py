"""A worker that acknowledges a job nobody asked for.

If the host accepted this, it would attribute a transcript to the wrong session. The job
identity has to be checked on the way back, not only on the way out.
"""

import sys


def main() -> int:
    sys.stdin.readline()
    sys.stdout.write(
        '{"protocol_version":1,"type":"ready","worker_version":"stub",'
        '"supported_protocol_versions":[1],"backends":["mock"]}\n'
    )
    sys.stdout.flush()

    sys.stdin.readline()
    sys.stdout.write(
        '{"protocol_version":1,"type":"started","job_id":"some-other-job",'
        '"backend":"mock","recognizes_speech":false}\n'
    )
    sys.stdout.flush()
    sys.stdin.readline()
    return 0


if __name__ == "__main__":
    sys.exit(main())
