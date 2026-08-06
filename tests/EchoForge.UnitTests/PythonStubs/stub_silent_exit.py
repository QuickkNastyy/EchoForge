"""A worker that starts, says nothing at all, and leaves.

Covers the early-exit case: the process ran, so this is not a launch failure, but the
handshake never happened, so there is nothing to report except that it is gone.
"""

import sys


def main() -> int:
    sys.stdin.readline()
    return 0


if __name__ == "__main__":
    sys.exit(main())
