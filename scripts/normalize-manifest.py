"""Normalise the pinned artifact manifest on stdin and write it to stdout.

PowerShell's ConvertTo-Json indents by four spaces per level, pads after every colon, and
escapes apostrophes as \\u0027. All of that is valid JSON and none of it is reviewable in a
diff, which matters for a file people have to read before trusting a download.

Reading as utf-8-sig and writing plain utf-8 is deliberate. Windows PowerShell puts a
byte-order mark on a pipe and its Set-Content writes one too; the manifest must not carry
one, because the schema test reads it as plain utf-8 and a BOM would make a perfectly good
manifest look like malformed JSON.
"""

import json
import sys


def main() -> int:
    document = json.loads(sys.stdin.buffer.read().decode("utf-8-sig"))
    payload = json.dumps(document, indent=2, ensure_ascii=False) + "\n"
    sys.stdout.buffer.write(payload.encode("utf-8"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
