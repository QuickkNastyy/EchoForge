"""EchoForge's short-lived processing worker.

The worker exists because the strongest speech-to-text ecosystem is Python, not because
EchoForge wants a second long-running process. It is started for one job, speaks NDJSON
over stdin/stdout, and exits when that job finishes, fails, or is cancelled. There is no
service, no port, no daemon, and no state that survives the process.

Nothing here writes to a source recording. Source WAVs and their metadata are opened
read-only, always.
"""

from .protocol import PROTOCOL_VERSION, SUPPORTED_PROTOCOL_VERSIONS, WORKER_VERSION

__all__ = ["PROTOCOL_VERSION", "SUPPORTED_PROTOCOL_VERSIONS", "WORKER_VERSION"]
