"""A worker that answers in a protocol version the host does not speak.

The real worker can never do this, which is exactly why it cannot be used to test the
supervisor's refusal. A version mismatch is the failure most likely to appear after an
upgrade, and it must be a clear refusal rather than a hopeful parse.
"""

import sys


def main() -> int:
    sys.stdin.readline()
    sys.stdout.write(
        '{"protocol_version":99,"type":"ready","worker_version":"stub",'
        '"supported_protocol_versions":[99],"backends":["mock"]}\n'
    )
    sys.stdout.flush()
    # Wait to be told to go, so the supervisor's refusal is what ends this, not an exit.
    sys.stdin.readline()
    return 0


if __name__ == "__main__":
    sys.exit(main())
