"""``python -m echoforge_worker`` — how the supervisor launches this."""

import sys

from .main import entry_point

if __name__ == "__main__":
    sys.exit(entry_point())
