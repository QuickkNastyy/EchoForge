"""A worker that starts a helper of its own and then refuses to finish.

This is the case a Job Object exists for. Killing the worker alone would leave the helper
running, and a stranded inference helper holding a GPU is exactly the sort of thing that
makes the *next* job fail for no visible reason.

Both processes write heartbeat files beside the requested output. A test can therefore tell
the difference between "the supervisor returned" and "everything it started is actually
gone", which a process handle alone cannot show.
"""

import json
import os
import subprocess
import sys
import time

CHILD_SOURCE = (
    "import sys, time\n"
    "path = sys.argv[1]\n"
    "while True:\n"
    "    open(path, 'w').write(str(time.time()))\n"
    "    time.sleep(0.05)\n"
)


def main() -> int:
    sys.stdin.readline()
    sys.stdout.write(
        '{"protocol_version":1,"type":"ready","worker_version":"stub",'
        '"supported_protocol_versions":[1],"backends":["mock"]}\n'
    )
    sys.stdout.flush()

    line = sys.stdin.readline()
    message = json.loads(line)
    job_id = message["job_id"]
    output_path = message["request"]["output_path"]
    os.makedirs(os.path.dirname(output_path), exist_ok=True)

    subprocess.Popen(
        [sys.executable, "-c", CHILD_SOURCE, output_path + ".grandchild"],
        stdin=subprocess.DEVNULL,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )

    sys.stdout.write(
        '{"protocol_version":1,"type":"started","job_id":"%s",'
        '"backend":"mock","recognizes_speech":false}\n' % job_id
    )
    sys.stdout.flush()

    # Never finishes, and never looks at stdin again. A cancel cannot reach this.
    while True:
        with open(output_path + ".worker", "w") as handle:
            handle.write(str(time.time()))
        time.sleep(0.05)


if __name__ == "__main__":
    sys.exit(main())
