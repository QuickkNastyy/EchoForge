# EchoForge isolated NeMo worker

NeMo/PyTorch is intentionally not installed into EchoForge's faster-whisper environment. This
runtime is Linux/WSL2-only because the pinned Parakeet release identifies Linux as its supported
OS. It runs the same `echoforge_worker` package and protocol, but advertises the `nemo` backend.

## EchoForge provisions this itself

**This is no longer an administrator step.** Installing NVIDIA Parakeet or Canary from
Settings → Models & runtime builds the whole thing:

```
check WSL and the distribution
  → create ~/.local/share/echoforge/nemo
  → stage the pinned, SHA-256-verified uv 0.11.28
  → install an exact CPython 3.11.14 into EchoForge's own directory
  → create the environment and install the hash-locked NeMo/PyTorch closure
  → probe CUDA from inside it
  → stage the verified model onto ext4
  → run the model on this GPU
  → Ready
```

The user opens no shell, runs no `pip`, and sets no environment variable.
`scripts/provision-nemo-runtime.ps1` is what does it, and it ships with the application so an
installed copy can run it with no source tree beside it. It is idempotent: a step already satisfied
is skipped, so it is safe to run again after a failure, and Repair does exactly that.

Nothing outside `~/.local/share/echoforge/nemo` is touched. The distribution's system Python is
left exactly as it was found, no global site-packages are modified, and deleting that one directory
removes the runtime completely.

### Why uv rather than the distribution's Python

The pinned closure resolves for CPython 3.11. Current Ubuntu images ship 3.14, `apt install
python3.11` both mutates the user's system and is unavailable on several supported releases, and
`deadsnakes` is a third-party archive EchoForge would then depend on. uv installs a standalone
CPython build into a directory EchoForge owns, which makes the exact interpreter reproducible and
leaves the distribution alone. uv itself is a pinned manifest artifact verified by SHA-256 before
it is used — never a `curl | sh`.

The closure is installed by `pip`, not by `uv pip`. The lock was generated for pip's
`--extra-index-url` semantics, and uv's first-index policy — which exists to prevent dependency
confusion, and is right to — refuses it. Loosening that policy to make an install succeed would
trade an integrity guarantee for convenience. Every distribution is hash-verified either way.

## Pinned versions, and why they are these versions

`requirements-production.txt` pins the complete Linux/CPython 3.11 closure by hash. Regenerate or
check it with `scripts/lock-nemo-runtime.ps1`.

**NeMo 3.0.0, not the 2.7.3 named on the Parakeet model card.** The pinned
`parakeet-unified-en-0.6b` checkpoint carries an encoder option (`att_chunk_context_size`) that
2.7.3's `ConformerEncoder` does not accept; restoring it raises before any audio is read. This was
found by *running* the model rather than by reading the card, which is precisely why installation
ends in a smoke test instead of a file check. 3.0.0 restores it, runs it, and still contains
Canary's SALM implementation.

`peft` is pinned explicitly. Canary's SALM loader imports it and NeMo 3.0.0's `[asr]` extra no
longer pulls it in — another failure the smoke test caught and a file listing never would have.

One further accommodation lives in `nemo_backend.py`: the Parakeet checkpoint ships
`validation_ds: null`, and NeMo 3.0.0's transcription dataloader reads it without checking. An
empty section is supplied at load time rather than editing the verified checkpoint, which must stay
byte-identical to what its digest describes.

## Inference

Forced offline. Model files come only from EchoForge's verified artifact registry, staged onto ext4
rather than read across the `/mnt/c` 9p mount — that mount caches directory listings, so a model
verified moments earlier is routinely invisible to Linux for a while, and reading a five-gigabyte
checkpoint across it is slow on every transcription rather than only the first.

Each invocation processes one ASR job and exits. The Python process boundary is the final GPU
memory cleanup guarantee. The worker advertises the NeMo backend only when the installed metadata
is exactly the pinned NeMo and PyTorch versions, and it checks those again before loading a model.
