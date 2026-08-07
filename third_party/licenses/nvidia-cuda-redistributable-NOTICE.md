# NVIDIA CUDA runtime redistributable — licence notice

**Artifact ID:** `summary.llama-cpp-cudart`
**File:** `cudart-llama-bin-win-cuda-13.3-x64.zip`
**Size:** 390,970,417 bytes
**SHA-256:** `1462a050eb4c684921ba51dcc4cc488a036674c3e73e9945ee705b854808d03e`
**Obtained from:** the ggml-org/llama.cpp release `b10298`
(commit `15586e2d7165570fb3aa7c26e0d442e289ef69de`)
**Verified:** 2026-08-07

## What this is

The NVIDIA CUDA 13.3 runtime libraries (`cudart`, `cuBLAS`, `cuBLASLt`) that llama.cpp's CUDA build
links against, redistributed by the llama.cpp project as a companion archive to its own binaries.
These are NVIDIA's redistributable runtime components, not the CUDA Toolkit and not a driver.

## Licence

Governed by the **NVIDIA CUDA Toolkit End User Licence Agreement**, whose *Attachment A*
enumerates the runtime files that may be redistributed with an application. `cudart`, `cuBLAS` and
`cuBLASLt` are on that list. The current EULA text is published by NVIDIA at
<https://docs.nvidia.com/cuda/eula/index.html> and is not vendored here because NVIDIA versions it
alongside the toolkit; the applicable version is the one for CUDA 13.3.

## Why this is pinned when Phase 2 deliberately did not pin CUDA

`artifacts/manifest.json` records, for the speech stack, that "NVIDIA CUDA and cuDNN runtime
libraries are deliberately absent: EchoForge uses a system-installed CUDA runtime when one is
present and falls back to CPU INT8 when it is not, and redistributing NVIDIA binaries needs the
release-time review scheduled for Phase 6."

That reasoning does not carry over unchanged, for two reasons:

1. **There is no system CUDA 13 to fall back to.** The Phase 2 stack is CTranslate2 4.8.1, a CUDA
   12 build. A CUDA 13 llama.cpp binary needs CUDA 13 runtime libraries, and nothing already
   installed on a machine for Phase 2 supplies them.
2. **The alternative is worse than the concern.** Not pinning it means the CUDA summary path works
   or fails according to whatever happens to be on the machine — the exact failure mode the
   artifact manifest exists to prevent, and one the Phase 3B brief calls out by name.

**Downloading these libraries at runtime for local use is not redistribution.** Shipping them
inside an EchoForge installer is, and that decision still belongs to the Phase 6 release-time
licence review, together with the same review for cuDNN. This notice exists so that review starts
from a written record rather than a discovery.

## The CPU path does not need this

`summary.llama-cpp-cpu` is a pinned CUDA-free build of the same llama.cpp release. A machine that
cannot or should not use the NVIDIA runtime has a real, verified, documented path that does not
involve this archive at all.
