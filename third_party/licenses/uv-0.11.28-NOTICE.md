# uv 0.11.28 — Linux x86_64 release archive

**Artifact:** `runtime.uv-linux`
**Upstream:** https://github.com/astral-sh/uv
**Release:** https://github.com/astral-sh/uv/releases/tag/0.11.28
**File:** `uv-x86_64-unknown-linux-gnu.tar.gz`
**SHA-256:** `e490a6464492183c5d4534a5527fb4440f7f2bb2f228162ad7e4afe076dc0224`

## Licence

uv is distributed by Astral under a dual licence, at the recipient's option:

- the MIT License, and
- the Apache License, Version 2.0.

The full texts are published in the upstream repository as `LICENSE-MIT` and `LICENSE-APACHE`,
and are reproduced in every source and binary distribution of uv, including this archive.

## Why EchoForge ships it

EchoForge uses uv for exactly one thing: provisioning an isolated CPython 3.11 and the hash-locked
NeMo/PyTorch closure inside the WSL runtime that the NVIDIA speech models require.

It is a pinned, SHA-256-verified artifact like every other download, and it is staged into
EchoForge's own directory under `~/.local/share/echoforge/nemo`. It is never fetched by an install
script, never placed on the user's `PATH`, and it never modifies the distribution's system Python
or any global site-packages. Removing that one directory removes it.

## Redistribution

The archive is downloaded from the upstream release at install time. It is not vendored into this
repository, and it is not modified. This file records the licence and provenance of what EchoForge
is permitted to fetch, which is what `artifacts/manifest.json` and `scripts/verify-models.ps1`
enforce.
