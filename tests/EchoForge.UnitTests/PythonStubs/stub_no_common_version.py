"""A worker whose envelope this host can read, but whose capabilities do not overlap.

The envelope says version 1, so the line parses. The worker then declares that it can only
run versions 2 and 3. Agreement has to be mutual: reading the message is not the same as
being able to work together, and this is the case where the difference shows.
"""

import sys


def main() -> int:
    sys.stdin.readline()
    sys.stdout.write(
        '{"protocol_version":1,"type":"ready","worker_version":"stub",'
        '"supported_protocol_versions":[2,3],"backends":["mock"]}\n'
    )
    sys.stdout.flush()
    sys.stdin.readline()
    return 0


if __name__ == "__main__":
    sys.exit(main())
