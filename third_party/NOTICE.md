# Third-party notices

EchoForge is distributed with, or downloads and installs, the components listed here. Each one's
full licence text is retained in `third_party/licenses/` and ships beside the application, because
that is what most of these licences actually require: a link is not a copy, and a user who never
goes online should still have the terms.

Nothing is summarised in place of its licence. The one-line descriptions below say what a
component is and where it came from; the file named against it is the authority.

**Downloaded rather than bundled.** The installer ships the application and its .NET runtime.
Everything under *Inference runtimes* and *Models* is fetched at setup time from the exact URL
pinned in `artifacts/manifest.json`, verified against the length and SHA-256 recorded there, and
installed under the user's own profile. That is deliberate: it keeps the installer small, it keeps
the model licences a thing the user accepts rather than something silently copied onto their disk,
and it means a re-pin is a manifest change rather than a new installer.

---

## Bundled with the application

| Component | Version | Licence | Retained text |
|---|---|---|---|
| .NET runtime and WPF (Microsoft) | 10.0 | MIT | `Apache-2.0-LICENSE.txt` does not cover this; see the .NET licence shipped in the publish output |
| NAudio | 2.3.0 | MIT | published with the package |
| Microsoft.Data.Sqlite.Core | 10.0.0 | MIT | published with the package |
| SQLitePCLRaw + `e_sqlite3` native | 3.0.2 | Apache-2.0 | `Apache-2.0-LICENSE.txt` |
| EchoForge worker (`echoforge_worker`) | — | MIT (this project) | — |

The self-contained publish embeds the .NET runtime and WPF assemblies. Microsoft's licence terms
for the .NET redistributable are included in the publish output, and the SQLite native library and
NAudio ship as ordinary package files beside the application.

## Interpreter

| Component | Version | Licence | Retained text |
|---|---|---|---|
| CPython | 3.12.13 | PSF-2.0 | `cpython-3.12.13-LICENSE.txt` |

Built and published by [python-build-standalone](https://github.com/astral-sh/python-build-standalone),
release `20260805`, `x86_64-pc-windows-msvc`, `install_only`. The retained text is the `LICENSE.txt`
from inside that archive: it carries the Python Software Foundation licence and the additional
conditions for the components bundled into the Windows build (bzip2, libffi, OpenSSL, SQLite, Tcl/Tk
and others). EchoForge ships an interpreter rather than requiring one so that a machine with no
Python — or the wrong Python — still works.

## Inference runtimes

Downloaded and verified at setup. Every one of these is a wheel resolved for CPython 3.12 on
Windows x64 and pinned by digest in `artifacts/manifest.json`.

| Component | Version | Licence | Retained text |
|---|---|---|---|
| faster-whisper | 1.2.1 | MIT | `faster-whisper-1.2.1-LICENSE.txt` |
| CTranslate2 | 4.8.1 | MIT | `ctranslate2-4.8.1-LICENSE.txt` |
| PyAV | 18.0.0 | BSD-3-Clause | `pyav-18.0.0-LICENSE.txt`, `av-18.0.0-LICENSE.txt` |
| ONNX Runtime | 1.28.0 | MIT | `onnxruntime-1.28.0-LICENSE.txt` |
| NumPy | 2.5.1 | BSD-3-Clause and others | `numpy-2.5.1-LICENSE.txt` |
| tokenizers | 0.23.1 | Apache-2.0 | `tokenizers-0.23.1-LICENSE.txt` |
| huggingface-hub | 1.26.1 | Apache-2.0 | `huggingface-hub-1.26.1-LICENSE.txt` |
| hf-xet | 1.6.0 | Apache-2.0 | `hf-xet-1.6.0-LICENSE.txt` |
| protobuf | 7.35.1 | BSD-3-Clause | `protobuf-7.35.1-LICENSE.txt` |
| flatbuffers | 25.12.19 | Apache-2.0 | `flatbuffers-25.12.19-LICENSE.txt` |
| PyYAML | 6.0.3 | MIT | `pyyaml-6.0.3-LICENSE.txt` |
| setuptools | 83.0.0 | MIT | `setuptools-83.0.0-LICENSE.txt` |
| tqdm | 4.70.0 | MPL-2.0 and MIT | `tqdm-4.70.0-LICENSE.txt` |
| packaging | 26.3 | Apache-2.0 or BSD-2-Clause | `packaging-26.3-LICENSE.txt` |
| filelock | 3.32.2 | Unlicense | `filelock-3.32.2-LICENSE.txt` |
| fsspec | 2026.7.0 | BSD-3-Clause | `fsspec-2026.7.0-LICENSE.txt` |
| httpx | 0.28.1 | BSD-3-Clause | `httpx-0.28.1-LICENSE.txt` |
| httpcore | 1.0.9 | BSD-3-Clause | `httpcore-1.0.9-LICENSE.txt` |
| h11 | 0.16.0 | MIT | `h11-0.16.0-LICENSE.txt` |
| anyio | 4.14.2 | MIT | `anyio-4.14.2-LICENSE.txt` |
| idna | 3.18 | BSD-3-Clause | `idna-3.18-LICENSE.txt` |
| certifi | 2026.7.22 | MPL-2.0 | `certifi-2026.7.22-LICENSE.txt` |
| click | 8.4.2 | BSD-3-Clause | `click-8.4.2-LICENSE.txt` |
| colorama | 0.4.6 | BSD-3-Clause | `colorama-0.4.6-LICENSE.txt` |
| typing-extensions | 4.16.0 | PSF-2.0 | `typing-extensions-4.16.0-LICENSE.txt` |

Silero VAD has no entry of its own because faster-whisper 1.2.1 ships it inside its own wheel, so
that wheel's digest already covers it.

| Component | Version | Licence | Retained text |
|---|---|---|---|
| llama.cpp (CPU and CUDA builds) | b10298 | MIT | `llama-cpp-b10298-LICENSE.txt` |
| NVIDIA CUDA runtime redistributables | 13.3 | NVIDIA CUDA Toolkit EULA | `nvidia-cuda-redistributable-NOTICE.md` |

The CUDA runtime libraries are downloaded only when a user chooses the GPU summary profile. They
are redistributed under the terms in the retained notice; a CPU-only installation never fetches
them.

## Models

Downloaded and verified at setup, and only when the user chooses a profile that needs them.

| Model | Revision | Licence | Retained text |
|---|---|---|---|
| faster-whisper large-v3-turbo (CTranslate2) | pinned in the manifest | MIT | `faster-whisper-large-v3-turbo-ct2-MODEL-CARD.md` |
| Gemma 4 12B IT QAT Q4_0 (GGUF) | pinned in the manifest | Gemma Terms of Use | `gemma-4-12b-it-qat-q4_0-MODEL-CARD.md` |
| Ministral 3 14B Instruct Q4_K_M (GGUF) | pinned in the manifest | Apache-2.0 | `ministral-3-14b-instruct-2512-MODEL-CARD.md` |

**Ministral is optional and is never installed by default.** It exists so the default summariser can
be measured against something; setup does not offer it as part of a recommended installation and no
processing profile selects it on its own. It appears in this notice because the packaging layout
makes it *available*, not because it is distributed.

Gemma is distributed under Google's Gemma Terms of Use rather than a standard open-source licence.
The retained model card carries the terms and the use restrictions; they apply to the weights, and
they are the user's to accept at the point the model is downloaded.

---

## How to check this list

Every downloadable entry above corresponds to an entry in `artifacts/manifest.json`, and each
manifest entry names its `license` and the `license_file` retained for it. The two are checked
against each other by `scripts/verify-models.ps1`, which fails the build if an artifact has no
retained licence text.
