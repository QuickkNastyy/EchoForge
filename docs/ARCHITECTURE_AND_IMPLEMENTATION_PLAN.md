# EchoForge architecture and implementation plan

**Research date:** 2026-08-04  
**Target:** Windows 11 desktop, one developer, private/local-first use  
**Product boundary:** Record → Transcribe → Summarize → Extract Actions

This plan makes implementation decisions rather than cataloguing every possible tool. A statement marked **Verified** is supported by a linked primary source. A statement marked **Estimate** is an engineering projection that must be measured on the actual computer. A statement marked **Decision** is the recommended EchoForge design.

The exact GPU, CPU, and system RAM are not known. That does not block the architecture. GPU vendor and generation will change CUDA availability and speed; CPU and RAM will change fallback speed and how much of the summarizer can be offloaded. The default configuration is deliberately sized for a CUDA-capable 16 GB GPU, with CPU fallbacks and a hardware benchmark as a release gate.

## A. Executive recommendation

### Primary stack

| Area | Exact recommendation |
|---|---|
| GUI and orchestration | **C# 14, .NET 10 LTS, WPF**, MVVM, modular monolith. .NET 10 is an LTS release and WPF remains a supported Windows desktop framework ([.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core), [WPF overview](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/)). |
| Windows audio | **Windows Core Audio/WASAPI shared mode through NAudio 2.3.x**: one loopback client for the selected render endpoint and one capture client for the selected microphone. Pin the exact NuGet patch version. NAudio is MIT-licensed and exposes WASAPI on Windows ([NAudio repository](https://github.com/naudio/NAudio), [NuGet package](https://www.nuget.org/packages/NAudio)). **EchoForge owns its own capture loop over `AudioClient`/`AudioCaptureClient` and does not use the high-level `WasapiCapture.DataAvailable` event as a timestamp source** — see “Packet timestamping” below. |
| Recording | Immutable **RIFF/WAVE PCM16**, 60-second chunks, separate `system` and `microphone` tracks. Aim for 48 kHz, system stereo and microphone mono; if an endpoint will not provide that shared-mode format, record its native sample rate/channel layout and record that fact in metadata. Normalize only processing derivatives to 16 kHz mono. |
| Timeline and drift | One monotonic session timeline anchored on the **per-packet `qpcPosition`** reported by `AudioCaptureClient.GetBuffer`. Delivered mix-format frame counts describe the audio written; `devicePosition` is a diagnostic only (measured: it can advance in the endpoint's own clock domain). Missing time during silence or a stall is advanced from the shared QPC session clock. Packet arrival time is never a clock. Preserve source chunks; insert silence and correct drift only in derivatives. |
| Transcription runtime | A short-lived **Python 3.12 worker** using **faster-whisper/CTranslate2**, CUDA 12 + cuDNN 9 when supported, otherwise CPU INT8. Pin Python wheels, model revisions, and SHA-256 hashes ([faster-whisper](https://github.com/SYSTRAN/faster-whisper)). |
| Default STT | **Whisper large-v3-turbo**, FP16 on CUDA; retry with `int8_float16` after an out-of-memory error. It is multilingual, has timestamps, and has materially lower compute than full large-v3 ([OpenAI model card](https://huggingface.co/openai/whisper-large-v3-turbo)). |
| Maximum-accuracy STT | **Whisper large-v3**, FP16 or `int8_float16`, selectable per re-run. It must beat turbo on EchoForge's meeting benchmark before being presented as “more accurate” for this hardware. |
| Low-resource / CPU STT | **Whisper small.en INT8** for English or **small INT8** for multilingual CPU fallback. **Distil-Whisper distil-large-v3.5** is an optional English GPU profile, not the universal fallback. |
| Speaker strategy | Transcribe tracks independently. Every microphone segment is **You**. System-track speech is **Remote** in the MVP. Do not attempt remote identity or biometric matching. Add anonymous remote diarization only in Phase 5. |
| Summarization runtime | A pinned **llama.cpp `llama-server.exe` child process**, bound only to `127.0.0.1` on a random port, one slot, started for a job and stopped afterward. Use schema-constrained output and a 32K operational context. No permanent Ollama service ([llama.cpp server](https://github.com/ggml-org/llama.cpp/blob/master/tools/server/README.md)). |
| Default summary model | **`google/gemma-4-12B-it-qat-q4_0-gguf`**, file `gemma-4-12b-it-qat-q4_0.gguf`, text-only, thinking disabled. The official QAT Q4 model is 6.98 GB, Apache-2.0, 11.95B dense parameters, and supports up to 256K context; EchoForge deliberately uses 32K to preserve memory and quality headroom ([official model card and GGUF](https://huggingface.co/google/gemma-4-12B-it-qat-q4_0-gguf)). |
| Long-transcript method | Segment-boundary chunking → per-chunk factual extraction → evidence validation → deterministic deduplication → hierarchical final synthesis. Feed the model 8K–12K transcript-token chunks and a final 32K context of validated digests, not an entire long transcript merely because the model advertises 256K. |
| Process communication | Parent-owned child processes with **newline-delimited JSON (NDJSON) over stdin/stdout**. Large inputs and outputs are immutable/revisioned files referenced by path. Technical diagnostics go to stderr. |
| Canonical data | Versioned JSON for session metadata, transcript, and summary; append-only JSONL event journal for recovery. A local SQLite database, including FTS where available, is a **rebuildable index**, never the source of truth. |
| Runtime data | `%LOCALAPPDATA%\EchoForge`. Source stays under `C:\EchoForge`; recordings and models never do. |
| Packaging | Self-contained Windows x64 .NET publish plus an Authenticode-signed **Inno Setup 7.0.2** per-user installer. Inno Setup is free for non-commercial use; commercial use should purchase the requested license ([official download verification](https://jrsoftware.org/isdl-verify.php), [commercial terms](https://jrsoftware.org/isorder.php)). |
| First run | Hardware and disk-space detection, dependency diagnostics, explicit model choices, resumable downloads to `.partial` files, SHA-256 verification, atomic activation, license/notice display, and a microphone/loopback test. Models are not bundled into the main installer. |

### Why Gemma 4 12B is the summary default

The user's priority is the best **actual meeting summaries** that retain enough working context on a 16 GB GPU. Gemma 4 12B QAT Q4 is the best starting decision because it combines a full 11.95B dense instruct model, an owner-published 6.98 GB quantization, strong context capacity, multilingual support, thinking-off operation, and enough VRAM headroom for a useful 32K KV cache and runtime buffers. That is a better risk balance than filling nearly all VRAM with a larger quantization and then shrinking context or spilling layers to CPU.

**Estimate:** text-only Gemma 4 12B Q4 at 32K context should occupy roughly 10–14 GB after weights, KV cache, compute buffers, and driver overhead. The exact value depends on the pinned llama.cpp build, KV precision, GPU backend, and context; it must be measured. Start with Q8 KV cache, one sequence, no multimodal projector, no speculative drafter, and no concurrent STT model.

The larger Gemma 4 26B A4B Q4 is not the better 16 GB choice: Google's own memory estimate is about 14.4 GB just to load the quantized model with overhead, before a useful long-context cache and runtime margin ([Gemma model/memory overview](https://ai.google.dev/gemma/docs/core)). A model that technically loads but forces a tiny context or CPU spill is worse for this task.

No public benchmark establishes “best meeting-summary model on EchoForge audio.” Therefore Phase 3 includes a non-negotiable bake-off against **Ministral 3 14B Instruct Q4_K_M** on 10–20 representative meetings. Ministral's owner-published Q4 file is 8.24 GB, supports 256K context and native JSON output, but leaves less 16 GB headroom ([official model card](https://huggingface.co/mistralai/Ministral-3-14B-Instruct-2512-GGUF)). Gemma remains the approved implementation default unless that task-specific test demonstrates a material quality win without memory or reliability regressions.

### Brief alternatives

- **GUI:** WinUI 3 is the modern Microsoft UI stack, but its Windows App SDK runtime/deployment choices add complexity without improving WASAPI capture. Avalonia is useful for cross-platform products, which EchoForge is not. PySide6 and Tauri expand the runtime surface.

- **Audio:** direct C++/COM gives maximum control but more lifetime and marshaling risk. Use it only for a narrow feature NAudio cannot expose. FFmpeg is appropriate for offline diagnostics/conversion, not as the ownership layer for two synchronized WASAPI clients.

- **STT:** whisper.cpp is the CPU/Vulkan escape hatch if CUDA deployment becomes the dominant problem. NVIDIA Parakeet/Canary are future benchmark candidates, not the Windows MVP default.

- **Summarization:** Ministral 3 14B is the first challenger; Qwen3 8B is the lower-memory option. Ollama is acceptable for development but not the packaged default because EchoForge should own process lifetime, model path, context, and logs.

### Hardware-dependent adjustments

- **NVIDIA GPU with a current CUDA-capable architecture:** use the approved faster-whisper CUDA and full-GPU llama.cpp profiles. VRAM capacity determines fit; memory bandwidth and GPU generation mostly determine speed.

- **AMD or Intel GPU reporting 16 GB:** the capacity is useful for llama.cpp through a validated Vulkan/HIP/SYCL backend, but it does not make faster-whisper's CUDA path available. Use CPU CTranslate2 for the MVP; qualify whisper.cpp Vulkan only if CPU STT is unacceptable.

- **Older NVIDIA GPU or driver:** CUDA 12/cuDNN 9 may fail despite 16 GB capacity. Keep the same models but use STT CPU fallback and, if supported by the pinned llama.cpp build, Vulkan or partial CPU offload for summary.

- **System RAM:** 32 GB is the practical recommendation for app, model mapping, CPU fallback, and operating-system headroom. With only 16 GB RAM, avoid large-model CPU fallback and minimize GPU spill; choose Qwen3 8B for summary if Gemma cannot remain fully on GPU. More RAM does not compensate for an unsupported GPU, but it makes partial offload tolerable.

- **CPU:** core count, AVX2/AVX-512 support, memory bandwidth, and thermals determine fallback time. A weak CPU changes the fallback experience, not the canonical formats or process architecture.

The first-run benchmark records these facts and chooses a profile; it never downloads a different model merely from the marketing name “16 GB GPU.”

### License posture

The primary path uses permissive components: NAudio, faster-whisper/Whisper, and llama.cpp are MIT-licensed; the selected Gemma 4 GGUF is Apache-2.0. Those licenses generally permit private and commercial use, modification, and redistribution subject to their copyright/notice and, for Apache-2.0, license/patent/NOTICE conditions. EchoForge must ship a generated third-party notice inventory and retain exact model/runtime licenses. Optional pyannote/NVIDIA model profiles marked CC-BY-4.0 require attribution. Inno Setup is free for non-commercial use but asks commercial users to purchase its commercial license. CUDA/cuDNN, Python wheels, PyAV/FFmpeg libraries, and every model artifact still require a release-time redistribution review; choosing not to ship a standalone FFmpeg executable reduces but does not eliminate transitive notice work. This is an engineering license assessment, not legal advice.

## B. Direct answers

| Question | Answer |
|---|---|
| Is OBS needed? | **No.** WASAPI loopback captures the selected playback endpoint without OBS, video capture, or a virtual cable. |
| Is video needed? | **No.** Video adds storage, consent, capture, and privacy complexity without helping the required workflow. |
| Can system and microphone audio be captured simultaneously? | **Yes.** Run one WASAPI loopback client and one microphone capture client concurrently, write separate bounded queues/files, and align them on a common QPC timeline. Phase 0 must prove ten minutes on the actual devices. |
| Will it work across arbitrary Windows meeting applications? | **Yes, when their audio is rendered to the selected endpoint.** Endpoint loopback is application-agnostic, so Zoom, browsers, Teams, Discord, Slack, and others require no integration. Protected content, exclusive-mode drivers, endpoint changes, and unusual vendor drivers remain edge cases. Microsoft documents loopback as capturing the audio engine's system mix ([WASAPI loopback](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording)). |
| Capture all playback audio or individual applications? | **All audio on the selected playback endpoint for the MVP.** It is the most reliable zero-setup behavior. Per-process loopback exists on Windows 10 build 20348 and later but adds native activation and process-tree/routing edge cases ([Microsoft application-loopback sample](https://learn.microsoft.com/en-us/samples/microsoft/windows-classic-samples/applicationloopbackaudio-sample/)). Revisit only after endpoint capture proves inadequate. |
| Keep sources separate? | **Yes. Always.** Separate tracks provide a deterministic You/Remote distinction, better STT, independent gain/VAD, and recoverability. A synchronized mix is a disposable playback/export derivative. |
| Is 16 GB VRAM enough? | **Yes for the recommended stages run sequentially**, assuming a supported GPU. Whisper large-v3-turbo and Gemma 4 12B Q4 each fit separately. Do not keep both loaded. A 16 GB capacity figure alone does not guarantee CUDA support or speed. |
| Can STT and summary models remain loaded together? | They might technically fit under a smaller-context/quantized configuration, but **EchoForge must unload one before loading the other**. The workflow is sequential, and reclaimed VRAM is more valuable as context/cache headroom and protection against fragmentation/OOM. |
| Best defaults? | **STT:** faster-whisper + large-v3-turbo. **Summary:** llama.cpp + official Gemma 4 12B Instruct QAT Q4, thinking off, 32K operational context, hierarchical evidence-backed pipeline. |
| Are local summaries good enough? | **Yes as reviewable meeting drafts, not as an unquestionable record.** A capable 12B instruct model can produce useful summaries and structured extraction when inputs are chunked, output is schema-constrained, and every important item must cite transcript evidence. Users must be able to open that evidence. |
| Is a reasoning model needed? | **No.** Meeting processing is mostly retrieval, compression, classification, light coreference, and synthesis. Long hidden reasoning increases latency/context use and can encourage unsupported reconciliation. Use a standard instruct model with thinking disabled. |
| When use ChatGPT or Claude? | For unusually noisy or multilingual meetings, ambiguous ownership, cross-cutting synthesis across very long material, polished external prose, or high-stakes review where a stronger hosted model is worth the privacy/cost trade. The MVP provides manual copy only. |
| Can upload limits be avoided? | **Yes.** Local recording has no artificial duration or upload cap. Chunking also avoids single-file RIFF size limits. |
| What practical limits remain? | Free disk, filesystem health, device/driver behavior, sleep, Bluetooth profile changes, GPU/CPU speed and memory, model context/quality, and recording-consent law. At the recommended encoding, source audio is about **1.04 GB per hour** before derivatives (engineering calculation: 48 kHz PCM16 stereo plus mono). |
| What is postponed? | Live transcription, automatic/hidden recording, per-application capture, remote diarization until Phase 5, voice identification, cloud API calls until Phase 7, calendar/bot/team/cloud-storage/mobile features, video, plugins, and advanced visual design. |

### Additional explicit decisions

- **Post-recording inference wins for the MVP.** It cannot starve the capture path, it can recover/retry cleanly, and it allows both complete tracks to be aligned before transcription. No reliability advantage justifies live transcription yet.

- **Notification sounds are included.** They are part of the selected endpoint mix. EchoForge should warn the user and suggest Windows Focus/Do Not Disturb; it must not pretend it can reliably distinguish a meeting from other endpoint audio.

- **Unknown means unknown.** If the transcript does not explicitly support an owner or date, the field remains `null` with status `unknown`. Any optional inference is marked `inferred` and visually separated. It must never be silently promoted to `explicit`.

- **Evidence is mandatory.** Every decision and action item contains source segment IDs and timestamps. The same rule should apply to risks, blockers, and open questions wherever possible.

## C. Focused comparison tables

### Speech-to-text

| Candidate | Meeting-relevant characteristics | Hardware / Windows | License | Status |
|---|---|---|---|---|
| [faster-whisper](https://github.com/SYSTRAN/faster-whisper) + [Whisper large-v3-turbo](https://huggingface.co/openai/whisper-large-v3-turbo) | Strong multilingual general speech, segment/word timestamps, batching, quantization, integrated Silero VAD. Turbo is 809M parameters versus 1.55B for large-v3. Names/acronyms still need glossary and correction UX; hallucinations around silence require VAD and validation. | Mature Windows Python path; NVIDIA CUDA 12/cuDNN 9 or CPU INT8. Official faster-whisper benchmarks show large-family operation within 8 GB GPUs, though EchoForge must benchmark its exact build. | MIT code/models; transitive binary notices still required. | **Default** |
| faster-whisper + Whisper large-v3 | Usually the best Whisper accuracy profile; multilingual and timestamped, but slower. “Maximum accuracy” must be demonstrated on real meetings rather than assumed. | Fits 16 GB in FP16; `int8_float16` lowers memory. CPU is possible but slow. | MIT | **Selectable accuracy option** |
| [Distil-Whisper distil-large-v3.5](https://huggingface.co/distil-whisper/distil-large-v3.5) | Current English-only 0.8B distilled model; its owner reports stronger AMI/GigaSpeech results than turbo on its published evaluation, while retaining long-form sequential decoding. It is not suitable as the universal multilingual default. | Good 16 GB GPU fit; current faster-whisper maps the `distil-large-v3.5` alias to the owner-published CT2 conversion. | MIT | **English low-resource option** |
| [whisper.cpp](https://github.com/ggml-org/whisper.cpp) | Mature quantized Whisper implementation, VAD support, timestamping, broad backends. More native packaging work and a second STT integration if used beside faster-whisper. | Excellent Windows CPU; CUDA/Vulkan options. | MIT | **CPU/Vulkan contingency** |
| [NVIDIA Parakeet-TDT 0.6B v3](https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3) / [Canary 1B v2](https://huggingface.co/nvidia/canary-1b-v2) | Fast, timestamp-capable, 25 European languages; Canary adds translation. Less language breadth and Windows packaging maturity for this app than Whisper. | NeMo's primary path is Linux/CUDA; Windows deployment is the risk. | CC-BY-4.0 model; attribution required. | **Benchmark later** |
| [Qwen3-ASR 1.7B](https://github.com/QwenLM/Qwen3-ASR) | New 2026 ASR/aligner stack, 30 languages plus Chinese dialects; could materially improve future accuracy. Timestamp aligner language support and production maturity are still narrower than Whisper. | CUDA-focused Python stack; Windows packaging must be proven. | Apache-2.0 | **One future alternative** |

**Estimate for a one-hour meeting:** on an unidentified modern NVIDIA 16 GB GPU, large-v3-turbo should take roughly **3–15 minutes**, and large-v3 roughly **6–30 minutes**, when conservative VAD skips silence and the two tracks are processed sequentially. CPU `small.en`/`small` INT8 may take roughly **20–90 minutes**. Older GPU architectures, slow CPUs, long overlap, batching, and thermals can move results outside these ranges. Publish only measured numbers from the actual machine.

When CUDA is absent or fails, the worker restarts the affected job using CTranslate2 CPU INT8 and the small profile after an explicit UI notice. Users may elect to continue a large model on CPU, but EchoForge must warn that it can take hours.

At the research date, faster-whisper's convenience alias for `large-v3-turbo` resolves to `mobiuslabsgmbh/faster-whisper-large-v3-turbo`, while `distil-large-v3.5` resolves to the Distil-Whisper owner's CT2 repository ([current model map](https://github.com/SYSTRAN/faster-whisper/blob/master/faster_whisper/utils.py)). Production setup must use explicit repository IDs, commits, file hashes, license provenance, and a transcription smoke test rather than trusting a mutable alias.

### Speaker and diarization approaches

| Approach | Result | Complexity / hardware | License / access | Status |
|---|---|---|---|---|
| Separate endpoint + microphone tracks | Deterministic **You** versus **Remote**. Does not distinguish remote people. | Low; no extra model. Works even when voices overlap across tracks, though duplicated local sidetone needs marking. | N/A | **MVP** |
| [pyannote speaker-diarization-community-1](https://huggingface.co/pyannote/speaker-diarization-community-1) on system track | Anonymous `Speaker 1`, `Speaker 2` labels; “exclusive” diarization can simplify alignment. Quality degrades with overlap, noise, and short turns. | Medium/high; another model, gated download/terms, GPU helpful. Runs offline after installation. | Model CC-BY-4.0; pyannote.audio code MIT; Hugging Face access acceptance required. | **Phase 5 default** |
| [WhisperX](https://github.com/m-bain/whisperX) pipeline | Word alignment plus pyannote diarization. | High; duplicates parts of the established STT/timestamp pipeline and adds failure modes. | BSD-2-Clause code plus component/model terms. | **Reference only** |
| [NVIDIA NeMo diarization](https://docs.nvidia.com/nemo-framework/user-guide/26.02/nemotoolkit/asr/speaker_diarization/models.html) | Sortformer/MSDD options, overlap-aware research paths. | High; CUDA/NeMo/Linux-oriented packaging. | Apache-2.0 code; model-specific terms. | **Not MVP** |

Track separation is sufficient for the MVP. It solves the highest-value attribution—local user versus everyone else—without pretending that diarization is identity. Speaker renaming is metadata only; no voiceprint is stored.

### Local summarization models for 16 GB VRAM

| Candidate | Context and estimated working fit | Summary/extraction assessment | License / Windows | Status |
|---|---|---|---|---|
| [Gemma 4 12B IT QAT Q4_0 GGUF](https://huggingface.co/google/gemma-4-12B-it-qat-q4_0-gguf) | 6.98 GB weights; up to 256K advertised. **Use 32K**; estimated 10–14 GB total text-only working set. | Best balance of dense-model capacity and VRAM headroom. Thinking can be disabled. Schema-constrained llama.cpp still needs external validation. | Apache-2.0; official GGUF; current llama.cpp Windows build. | **Default** |
| [Ministral 3 14B Instruct Q4_K_M](https://huggingface.co/mistralai/Ministral-3-14B-Instruct-2512-GGUF) | 8.24 GB weights; 256K advertised. 32K is likely tight but feasible with cache tuning. | Strong system-prompt/JSON claims and two billion more parameters; may give better prose or extraction, but less memory margin. | Apache-2.0; official GGUF; llama.cpp support must be pinned/tested. | **Required quality challenger** |
| [Qwen3 8B Q4_K_M](https://huggingface.co/Qwen/Qwen3-8B-GGUF) | About 5 GB weights; 32K native, longer via YaRN. Comfortable 16 GB fit. | Good multilingual structured extraction, but less capacity than the 12B default. Disable thinking. | Apache-2.0; Windows llama.cpp. | **Low-resource summary option** |
| [Qwen3.5 9B](https://huggingface.co/Qwen/Qwen3.5-9B) | 262K advertised; a suitable 4-bit build should fit comfortably. | Promising current instruct family. At the research date, the owner card's primary serving paths were Transformers/vLLM/SGLang; promote only after an owner-traceable GGUF and pinned llama.cpp compatibility are validated. | Apache-2.0; Windows path needs qualification. | **Watch list** |
| [gpt-oss-20b](https://openai.com/index/introducing-gpt-oss/) | OpenAI states it can run within 16 GB memory, but a useful context/cache and runtime buffers leave little margin on this exact constraint. | Strong structured/reasoning behavior, but reasoning/Harmony complexity is unnecessary for factual meeting extraction. | Apache-2.0 plus model usage policy; Windows runtime qualification required. | **Not default** |
| [Phi-4-reasoning-plus](https://huggingface.co/microsoft/Phi-4-reasoning-plus) / Mistral Small 24B | 14B reasoning at 32K or about 14–15 GB Q4 weights for 24B. | Reasoning model is misaligned; 24B consumes nearly all VRAM before useful context. | MIT / Apache-2.0 depending model. | **Reject for MVP** |

The 256K numbers are model limits, not a promise that a quantized runtime can hold a 256K cache in 16 GB or that summary quality remains strong at that length. EchoForge's 32K operational context plus hierarchy is the dependable design.

Llama 3.1 8B Instruct was screened out: it is a capable older 8B option, but Qwen3 8B is the stronger low-memory shortlist here and Apache-2.0 is simpler than the Llama Community License. Adding another model profile would not improve the primary decision.

### GUI frameworks

| Option | Windows/audio/packaging fit | Complexity | Status |
|---|---|---|---|
| C#/.NET 10 WPF | Direct .NET/COM interop, NAudio integration, mature controls/tray/process APIs, self-contained publish, excellent Windows debugging. | Low for this product; Windows-only by design. | **Choose** |
| C# WinUI 3 | Modern controls and Microsoft investment. Self-contained Windows App SDK deployment has additional packaging/runtime choices ([deployment docs](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)). | Medium; no material product benefit. | Alternative |
| C# Avalonia | Good desktop framework and native interop ([docs](https://docs.avaloniaui.net/docs/app-development/native-interop)). | Medium; cross-platform abstraction is unused. | Reject |
| Python PySide6 | Fast prototypes; official deployment tooling exists ([Qt docs](https://doc.qt.io/qtforpython-6.8/deployment/deployment-pyside6-deploy.html)). | High packaged surface: Qt + Python + ML + native audio. | Reject |
| Tauri | Small webview shell; Windows installers supported ([docs](https://v2.tauri.app/distribute/windows-installer/)). | High: Rust, web UI, WebView2, C#/COM or Rust audio bridge, IPC. | Reject |

### Fully local versus cloud-assisted summarization

| Mode | Privacy/offline | Quality and limits | Complexity/cost | Recommendation |
|---|---|---|---|---|
| Local transcription + local summary | Audio and text remain on device; fully offline after setup. | Good reviewable drafts; 12B-model limitations; evidence UI makes errors inspectable. | No per-use fee; model download, local compute. | **MVP default** |
| Local transcription + manual copy to ChatGPT/Claude | User explicitly chooses and can redact the text; no EchoForge API integration. | Hosted frontier models may improve ambiguity, very long synthesis, and prose. Consumer-product data controls differ from API terms. | Lowest implementation complexity; manual effort. | **Required MVP escape hatch** |
| Local transcription + optional API summary | Transcript text only, never audio; explicit preview/consent per run. Not offline. | Potentially best quality and structured output, provider-dependent. | API key/security, network, cost, retention disclosures, changing models. | **Phase 7 only** |

For future OpenAI API support, API data is not used for training by default, but abuse-monitoring logs are ordinarily retained up to 30 days and some endpoints retain application state unless configured; approved customers can pursue Zero Data Retention ([OpenAI data controls](https://developers.openai.com/api/docs/guides/your-data)). Structured Outputs can constrain JSON Schema ([official guide](https://developers.openai.com/api/docs/guides/structured-outputs)). Anthropic says commercial API inputs/outputs are not used for training by default and are normally deleted within 30 days, with eligible zero-retention arrangements ([training policy](https://privacy.claude.com/en/articles/7996868-is-my-data-used-for-model-training), [retention policy](https://privacy.claude.com/en/articles/7996866-how-long-do-you-store-my-organization-s-data)). Re-check these terms when Phase 7 is implemented.

## D. Architecture

### Component diagram

~~~mermaid
flowchart LR
    User[User] --> UI[WPF app]
    UI --> App[Application services]
    App --> Audio[NAudio / WASAPI capture]
    App --> Sessions[Session and recovery service]
    App --> Jobs[Worker supervisor]
    Audio --> Files[(Versioned session files)]
    Sessions --> Files
    Sessions --> Index[(Rebuildable SQLite index)]
    Jobs <-->|NDJSON stdio| Worker[Short-lived Python worker]
    Worker --> STT[faster-whisper / CTranslate2]
    Worker --> Llama[Ephemeral llama-server]
    STT --> Models[(Pinned local models)]
    Llama --> Models
    Worker --> Files
    UI --> Playback[NAudio synchronized playback]
    Playback --> Files
    OptionalCloud[Optional API adapter<br/>Phase 7] -. explicit transcript only .-> App
~~~

This is a modular monolith, not a collection of services. The WPF process owns application state, capture, files, and child-process lifetimes. The Python worker exists because the strongest STT ecosystem is Python/CUDA. llama.cpp remains a native child executable so the application does not embed a second C++ ABI or run a permanent local server.

### Data-flow diagram

~~~mermaid
flowchart TD
    A[Selected render endpoint] -->|WASAPI loopback| S[System PCM chunks]
    B[Selected microphone] -->|WASAPI capture| M[Microphone PCM chunks]
    C[QPC + audio clocks] --> S
    C --> M
    S --> DS[Aligned 16 kHz mono derivative]
    M --> DM[Aligned 16 kHz mono derivative]
    DS --> TS[Remote transcript segments]
    DM --> TY[You transcript segments]
    TS --> Merge[Canonical timeline merge]
    TY --> Merge
    Merge --> Chunk[Evidence-preserving transcript chunks]
    Chunk --> Extract[Per-chunk JSON extraction]
    Extract --> Validate[Schema + evidence validation]
    Validate --> Dedupe[Deterministic deduplication]
    Dedupe --> Synthesis[Hierarchical 32K synthesis]
    Synthesis --> Summary[Canonical summary JSON]
    Summary --> Views[GUI / search / exports]
    Merge --> Views
    S --> Playback[Timestamp playback]
    M --> Playback
~~~

### Audio decisions and edge cases

**Simultaneous clients.** Create the loopback and microphone clients during preflight, start them against the same QPC epoch, and place packet payloads into separate bounded queues capped at five seconds per track. Dedicated writer tasks perform conversion to PCM16 and chunk writes. Capture threads must never block on disk, hashing, UI, or logging. Queue overflow is a recorded discontinuity plus an immediate visible error, never a silent drop.

**Packet timestamping.** EchoForge does **not** use `WasapiCapture`/`WasapiLoopbackCapture` and their `DataAvailable` event as the production timestamp source. That event delivers bytes without the per-packet positions the timeline depends on, and callback arrival time is not the clock. Instead EchoForge drives its own capture loop:

- Initialize `AudioClient` in shared mode (loopback flag for the render endpoint), request event-driven notification with `SetEventHandle`, and wait on that handle from a dedicated capture thread.
- Drain with NAudio's lower-level public `AudioCaptureClient.GetBuffer` overload that returns the frame count, `AudioClientBufferFlags`, **device position**, and **QPC position** for each packet, then release exactly the frames read.
- Treat **`qpcPosition` as the canonical session-time anchor**. Every chunk boundary, gap, and derivative time map is derived from it, never from when managed code happened to observe the packet.
- Record `DataDiscontinuity`, `Silent`, and `TimestampError` flags per packet. A discontinuity is data, not an error to be smoothed over.
- Convert to PCM16 and enqueue; the capture thread does nothing else.

**Verified on real hardware (Phase 0).** Both the four-out-parameter `GetBuffer` overload and `AudioClient.SetEventHandle` are reachable on the pinned NAudio 2.3.0, so **no native COM shim is required**. A shim is still the fallback if a future gate measurement proves unreachable, but it is not on the critical path.

### The device-position rule

**Measured, not assumed.** A headset microphone under test delivered 480 mix-format frames per packet while its `devicePosition` advanced by 160. The endpoint captures natively at 16 kHz and the shared audio engine resamples to the 48 kHz mix format, so device position counts frames in the **endpoint's own clock domain**, not the mix format's. Building the timeline on it produced a threefold error and roughly 2,400,000 ms/hour of phantom drift.

The settled rule:

- **`qpcPosition` is the canonical session-time anchor.**
- **Delivered mix-format frame counts describe the audio written.** A chunk's frame count is what was actually placed on the timeline.
- **`devicePosition` is retained for diagnostics and discontinuity correlation.** It is recorded in metadata and used to corroborate engine-reported gaps.
- **Device position must never be treated directly as a mix-format frame counter** unless its rate has been explicitly identified and calibrated for that endpoint. EchoForge does not currently perform that calibration.
- **Missing time during endpoint silence or a stall is advanced from the shared QPC session clock.** A stalled endpoint sends no packets at all, so a writer driven only by packet arrival would end the track early; Phase 0 measured exactly that.
- **Packet arrival time is never used as a clock.**

Both tracks share **one epoch**, fixed before either endpoint opens, and **one stop instant**. Without that, t=0 means a different moment on each track and alignment is impossible by construction.

**Endpoint-wide capture.** WASAPI loopback captures the selected endpoint's shared audio-engine mix. It includes meeting audio, media, browser tabs, and Windows notification sounds. It can also omit protected DRM audio; this is acceptable because EchoForge is a meeting recorder, not a protection bypass ([Microsoft loopback documentation](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording)).

**Silence.** A render endpoint that is playing nothing produces no loopback packets ([NAudio loopback guide](https://github.com/naudio/NAudio/blob/release/2.x/Docs/WasapiLoopbackCapture.md)), and an endpoint that stalls stops producing them entirely. The writer must therefore build chunks against the **shared QPC session clock** and insert explicit silence for the frames that clock says are missing, both when a later packet reveals a gap and, while idle, on its own initiative. Packet arrival time is not the timeline; the packet's QPC position is.

**Clocking and drift.** Each endpoint owns a clock; nominally identical 48 kHz devices can drift. Anchor on the per-packet `qpcPosition` returned by `GetBuffer`, count delivered mix-format frames as the audio written, and record discontinuity/timestamp-error flags ([buffer flags](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/ne-audioclient-_audclnt_bufferflags)). Do not use `devicePosition` as a frame counter — see the device-position rule above. `IAudioClock::GetPosition` remains available as a corroborating sample but is not an anchor, because it is polled rather than tied to a specific packet ([IAudioClock](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclock-getposition)). Never time-stretch immutable source chunks. Create a corrected derivative by inserting gaps and applying a bounded resampling ratio between clock anchors. Store the derivative-to-session/source time map so transcript timestamps seek the immutable audio rather than an assumed sample offset.

**Track duration is not alignment.** Because both tracks are padded to a shared stop instant, two equal-length files prove nothing about whether their audio lines up. Alignment can only be qualified by a **signal-based measurement** — timed chirps played through the render endpoint and picked up acoustically — which also captures analogue latency that packet timestamps cannot see. Packet/QPC drift figures are labelled estimates everywhere they appear and never satisfy a gate.

**Format.** The preferred acquisition encoding is 48,000 Hz PCM16: stereo for system audio and mono for microphone. Reliability takes precedence over forcing a format. If the shared-mode client exposes 44.1 kHz, float input, or a Bluetooth 16 kHz mono microphone, convert the sample representation to PCM16 but retain the endpoint's native rate/channel layout in source chunks and metadata; normalize later.

**Bluetooth.** Microsoft documents the classic Bluetooth split: A2DP provides stereo output but no microphone, while HFP provides concurrent microphone/playback with mono 8 or 16 kHz audio ([Bluetooth Classic audio](https://learn.microsoft.com/en-us/windows-hardware/drivers/bluetooth/bluetooth-classic-audio)). Windows may switch profiles when the headset microphone opens. EchoForge must detect/report the resulting formats and recommend a USB/wired microphone or validated LE Audio device for quality. It cannot fix a radio/profile limitation.

**Device events.** Register `IMMNotificationClient` for add/remove/default/state/property notifications ([device events](https://learn.microsoft.com/en-us/windows/win32/coreaudio/device-events)). Pin device IDs at Start; never silently follow the default endpoint. On a disconnection, finalize the affected chunk, keep the healthy track recording, show a persistent degraded warning, and let the user explicitly reconnect into a new timeline epoch or stop.

**Sleep.** A suspend event finalizes both active chunks if possible. Resume begins a new epoch with an explicit gap; audio during sleep is unrecoverable. Do not fabricate continuity.

**Disk full.** Preflight requires the larger of 2 GB free or ten minutes of worst-case recording plus a reserve. Warn at 5 GB, initiate a controlled stop at 2 GB, and maintain a small preallocated recovery-reserve file that can be released to patch headers/journals. Catch late write failures independently per track and show them immediately.

**Echo/sidetone.** Headphones minimize acoustic echo, but meeting apps or headset sidetone can put “You” on the system track too. Preserve both. Mark time-overlapping, text-similar segments as possible duplicates; do not erase audio or assert identity automatically.

**FFmpeg.** FFmpeg's official device documentation lists DirectShow for Windows inputs, not a first-class WASAPI loopback owner ([FFmpeg devices](https://ffmpeg.org/ffmpeg-devices.html)). It should not own live capture. faster-whisper's PyAV dependency can decode processing inputs without a user-installed FFmpeg executable. If a later export needs FFmpeg, ship a pinned LGPL-compatible build and notices for offline conversion only.

### Recording lifecycle

~~~mermaid
sequenceDiagram
    participant U as User
    participant A as WPF app
    participant W as WASAPI clients
    participant F as Session store
    U->>A: Start
    A->>A: consent reminder + device/disk preflight
    A->>F: create session, journal, recovery reserve
    A->>W: open selected endpoint and microphone
    A->>W: start at common QPC epoch
    loop every callback
        W->>A: frames + clock/discontinuity metadata
        A->>F: enqueue to bounded per-track writer
    end
    loop every 60 seconds
        F->>F: close and flush .part WAV
        F->>F: validate, hash, atomic rename to .wav
        F->>F: append chunk-completed event
    end
    U->>A: Pause / Resume / Stop
    A->>F: finalize chunks and timeline epoch
    A->>W: stop/dispose clients
    A->>F: reconcile manifest and mark recorded
~~~

Pause closes the active chunks and records a gap. Resume opens a new epoch; it never appends to an old WAV. Stop is idempotent. The persistent red indicator, tray icon, duration, and disk display remain visible throughout `recording` and `degraded` states.

### Authority model

Four kinds of artefact, with different standing. Confusing them is how recovery quietly loses or
invents audio.

| Artefact | Standing |
|---|---|
| Finalized WAV **plus a validated finalized `.meta.json`** | **Canonical source chunk.** Neither half alone is sufficient. |
| `events.jsonl` | **Canonical lifecycle and event ledger.** Epochs, terminal outcome, and session history. |
| `session.json`, and later the SQLite index | **Rebuildable projections.** Replaceable at any time from the two above. |
| Active `.part.state.json` sidecars | **Recovery evidence only.** Never canonical; used to reconstruct a record when one is missing. |

**Metadata never overrides a contradictory fact.** Before reconciliation may treat a record as
canonical it is checked against the audio and the filesystem: supported schema, `finalized=true`,
track matching its directory, index matching its filename, a relative path that resolves to
exactly that file inside the session root, a usable format, epoch, start time, and discontinuity
list, a WAV that independently validates, a format and frame count that match the audio, a SHA-256
that matches the bytes, and no contradictory journal entry. On any disagreement everything is
preserved byte for byte, nothing is journalled as canonical, the session is marked
`NeedsAttention`, and a safe reason is reported.

**Chunk records.** Every chunk carries a durable metadata record beside the audio: track, index, epoch, format, frames, start offset within the epoch, and the epoch's QPC origin. The active chunk's record is rewritten on each flush; a finalized chunk's record is written immediately after the atomic rename and before anything else is told the chunk exists. This is what makes a crash between finalization and the journal append survivable — startup reconciles the chunk directories against the journal and adds any missing record rather than ignoring audio the journal never heard about. Journal appends are handed to a dedicated persistence thread so neither the capture thread nor the writer thread blocks on fsync.

The active `.part.wav` data stream and sidecar frame count are flushed to durable storage at most every two seconds and on pause/stop/device/power transitions. Closing patches the WAV header, flushes again, validates frame alignment and duration, atomically renames the file to `.wav`, and only then journals `chunk_completed`. The at-most-two-second flush cadence establishes the recovery-tail target; it does not change the 60-second chunk duration.

### Processing lifecycle

1. Reconcile and validate completed source chunks; never “repair” a completed immutable file in place.
2. Create a job record with input revision, model revision/hash, options, and output staging path.
3. Build 16 kHz mono aligned derivatives per track, streaming chunk by chunk. Source-chunk boundaries are not speech boundaries.
4. Form contiguous **ten-minute transcription windows** within each timeline epoch with a five-second audio overlap, then transcribe microphone and system windows independently with VAD and word timestamps. Rebase timestamps to the session and deduplicate only the known overlap before checkpointing.
5. Assign microphone segments to `You` and system segments to `Remote`; merge on the session timeline and compute overlaps.
6. Validate the canonical transcript, fsync a revisioned temporary output, and atomically activate it.
7. Unload and terminate the STT worker/model before summary inference.
8. Start the pinned llama.cpp child with one slot and a 32K context; run per-chunk extraction, validation, deduplication, and final synthesis.
9. Validate all JSON, IDs, timestamps, null/status invariants, and evidence links. Atomically activate the new summary revision.
10. Rebuild/update the SQLite search index. A failed index update does not invalidate canonical JSON.

Only one GPU-heavy job runs at a time. Recording always has priority; MVP processing is queued or suspended while a new recording is active.

### Session states

~~~mermaid
stateDiagram-v2
    [*] --> New
    New --> Recording: start succeeds
    Recording --> Paused: pause
    Paused --> Recording: resume / new epoch
    Recording --> Degraded: one track/device fails
    Degraded --> Recording: explicit reconnect / new epoch
    Recording --> Finalizing: stop
    Paused --> Finalizing: stop
    Degraded --> Finalizing: stop
    Finalizing --> Recorded: chunks reconciled
    New --> Failed: preflight/start failure
    Finalizing --> NeedsAttention: recovery incomplete
    Recording --> Recovering: process terminated
    Recovering --> Recorded: active parts repaired
    Recovering --> NeedsAttention: invalid parts retained
~~~

Transcription and summarization each have independent `not_requested / queued / running / succeeded / failed / cancelled` stage states and input/output revision IDs. Do not compress recording and processing into one fragile enum. A session is “Ready” only when the selected transcript and summary revisions both succeeded; audio remains playable in every post-recording state.

### Repository layout under `C:\EchoForge`

~~~text
C:\EchoForge\
├─ EchoForge.slnx
├─ global.json
├─ Directory.Build.props
├─ Directory.Packages.props
├─ README.md
├─ docs\
│  └─ ARCHITECTURE_AND_IMPLEMENTATION_PLAN.md
├─ artifacts\
│  └─ manifest.json                   # pinned models/runtimes; no mutable refs
├─ schemas\
│  ├─ session.schema.json
│  ├─ transcript.schema.json
│  ├─ summary.schema.json
│  ├─ artifact-manifest.schema.json
│  └─ worker-protocol.schema.json
├─ src\
│  ├─ EchoForge.App\                 # WPF views, view models, tray
│  ├─ EchoForge.Core\                # use cases, states, policies
│  ├─ EchoForge.Contracts\           # versioned DTOs/interfaces
│  ├─ EchoForge.Audio.Windows\       # NAudio/WASAPI and playback
│  └─ EchoForge.Infrastructure\      # files, journal, SQLite, workers
├─ worker\
│  ├─ pyproject.toml
│  ├─ uv.lock
│  └─ echoforge_worker\
│     ├─ main.py
│     ├─ protocol.py
│     ├─ audio.py
│     ├─ transcribe.py
│     ├─ summarize.py
│     ├─ chunking.py
│     ├─ evidence.py
│     └─ models.py
├─ poc\
│  └─ EchoForge.AudioCapture.Poc\
├─ tests\
│  ├─ EchoForge.UnitTests\
│  ├─ EchoForge.IntegrationTests\
│  ├─ EchoForge.AudioHardwareTests\
│  └─ worker_tests\
├─ packaging\
│  └─ inno\EchoForge.iss
├─ scripts\
│  ├─ bootstrap-dev.ps1
│  ├─ fetch-dev-models.ps1
│  ├─ verify-models.ps1
│  └─ package.ps1
└─ third_party\
   └─ NOTICE.md
~~~

Project references point inward: App → Core/Contracts; Infrastructure and Audio.Windows → Core/Contracts; Core → Contracts only. Neither Core nor Contracts references WPF, NAudio, SQLite, Python, or model runtimes.

### Runtime data layout

~~~text
%LOCALAPPDATA%\EchoForge\
├─ config\settings.json
├─ library\echoforge.db              # rebuildable index
├─ logs\echoforge-YYYYMMDD.log       # diagnostics only
├─ models\
│  ├─ stt\<model-id>\<revision>\...
│  └─ llm\<model-id>\<revision>\...
├─ runtime\                           # app-local Python/native binaries
└─ sessions\YYYY\MM\<session-id>\
   ├─ session.json                    # atomic snapshot
   ├─ events.jsonl                    # append-only recovery journal
   ├─ recovery.reserve
   ├─ tracks\
   │  ├─ system\chunks\000001.wav
   │  ├─ system\active\000002.part.wav
   │  ├─ system\active\000002.part.state.json
   │  ├─ microphone\chunks\000001.wav
   │  ├─ microphone\active\000002.part.wav
   │  └─ microphone\active\000002.part.state.json
   ├─ derived\
   │  ├─ audio\<revision>\...
   │  └─ playback\<revision>\...
   ├─ transcript\
   │  ├─ transcript.v1.json
   │  └─ transcript.v2.json
   ├─ summary\
   │  ├─ summary.v1.json
   │  └─ work\<job-id>\...
   ├─ jobs\<job-id>\job.json
   ├─ exports\
   └─ diagnostics\
~~~

Session titles are metadata, not folder names. IDs are random UUID/ULID values, so unusual titles cannot break paths and diagnostic paths do not reveal meeting text.

### Pinned artifact manifest

**No production inference artifact is downloaded before it exists in the manifest.** This is a gate on Phase 2, not a cleanup task. The manifest is version-controlled at `artifacts/manifest.json`, validated by `schemas/artifact-manifest.schema.json`, and is the only list the downloader reads.

Every entry records:

| Field | Meaning |
|---|---|
| `artifact_id` | EchoForge's stable internal name, e.g. `stt.large-v3-turbo`. |
| `repository` | Exact source repository or release URL. |
| `revision` | **Full immutable commit SHA or release tag.** Never `main`, `latest`, or a convenience alias. |
| `filename` | Exact file to fetch, not a directory or a glob. |
| `size_bytes` | Expected byte length, checked before hashing. |
| `sha256` | Expected digest of the complete file. |
| `license` | SPDX identifier or named license, plus the path of the retained license/NOTICE text. |
| `runtime_version` | The runtime build this artifact was verified against — llama.cpp release, CTranslate2/faster-whisper version, CUDA/cuDNN pairing. |
| `verified_utc` | Date the entry was last checked against its source. |

Rules:

- **A mutable reference is a build failure, not a warning.** `verify-models.ps1` rejects any entry whose `revision` is a branch name or whose `filename` is absent, and the packaging script runs it.
- Download to `.partial`, verify length then SHA-256, and only then atomically activate. A mismatch keeps the previously activated artifact in place and reports the expected and actual digests.
- faster-whisper's convenience aliases resolve to third-party conversions that can move. The manifest records the **resolved repository and commit**, and the alias is never used at runtime.
- Changing a model or runtime version is a manifest edit with a new `verified_utc` and a fresh smoke test, reviewed like any other change.
- The manifest is also the input to the third-party notice inventory, so license text is collected at pin time rather than reconstructed at release.

### Process boundaries and worker protocol

The WPF process launches one worker for one job and assigns it to a Windows Job Object so cancellation or parent exit terminates the complete child tree. NDJSON is preferable to named pipes here because there is only one parent/child relationship, no service discovery, and stdout is trivial to capture in tests. The protocol is versioned:

~~~json
{"protocol_version":1,"type":"start_job","job_id":"01J...","job_kind":"transcribe","input_manifest_path":"...","output_path":"...","options":{"profile":"large-v3-turbo"}}
{"protocol_version":1,"type":"progress","job_id":"01J...","stage":"transcribing_system","completed_units":7,"total_units":18}
{"protocol_version":1,"type":"checkpoint","job_id":"01J...","checkpoint_path":"..."}
{"protocol_version":1,"type":"result","job_id":"01J...","output_path":"...","sha256":"..."}
~~~

Commands are `start_job` and `cancel`; events are `started`, `progress`, `checkpoint`, `warning`, `result`, `error`, and `cancelled`. Messages never carry transcript/audio bodies. Unknown protocol versions fail clearly.

For summaries, the Python job starts a pinned `llama-server.exe` on loopback with a random port, one parallel slot, an ephemeral token where supported, and offline mode. It waits for readiness, sends schema-constrained requests, and always tears the process down. Model files are read-only. llama.cpp supports JSON-schema/grammar constraints, but only a subset of JSON Schema maps cleanly to grammars; keep the generation schema simple and enforce full invariants after generation ([grammar limitations](https://github.com/ggml-org/llama.cpp/blob/master/grammars/README.md)).

### Session manifest

`session.json` is a recoverable snapshot derived from the journal. It records stable endpoint IDs, every actual format, timeline epochs/gaps, chunk-relative and session-relative time, byte/frame counts, clock anchors, discontinuities, hashes, and selected processing revisions. A minimal outline is:

~~~json
{
  "schema_version": 1,
  "session_id": "01J...",
  "state": "recorded",
  "created_at_utc": "2026-08-04T18:32:00Z",
  "started_at_utc": "2026-08-04T18:32:04Z",
  "ended_at_utc": "2026-08-04T19:17:13Z",
  "timeline": {
    "qpc_frequency": 10000000,
    "epochs": [],
    "gaps": []
  },
  "tracks": [
    {
      "source_track": "system",
      "device_id": "<stable-endpoint-id>",
      "device_name": "Headphones",
      "preferred_format": {"sample_rate": 48000, "channels": 2, "bits_per_sample": 16},
      "chunks": [
        {
          "index": 1,
          "relative_path": "tracks/system/chunks/000001.wav",
          "start_seconds": 0.0,
          "end_seconds": 60.0,
          "sample_rate": 48000,
          "channels": 2,
          "sample_frames": 2880000,
          "sha256": "<sha256>",
          "discontinuities": []
        }
      ]
    }
  ],
  "selected_transcript_revision": null,
  "selected_summary_revision": null
}
~~~

Device names are useful session metadata but should not be copied into general logs. On startup, journal events are replayed and this snapshot is replaced atomically if reconciliation changes it.

### Canonical transcript schema

The JSON Schema files are authoritative. The top-level transcript records `schema_version`, `session_id`, `transcript_revision`, creation time, source manifest hash, model/runtime/revision/compute settings, languages, speakers, and ordered segments. A segment has the required shape:

~~~json
{
  "id": "segment-000123",
  "start_seconds": 742.12,
  "end_seconds": 748.91,
  "speaker_id": "speaker-01",
  "speaker_name": "Speaker 1",
  "source_track": "system",
  "text": "We should complete the deployment by Friday.",
  "confidence": null,
  "words": [],
  "language": "en",
  "overlaps_segment_ids": []
}
~~~

`source_track` is `system` or `microphone`. Microphone speaker ID is stable and name is `You`. Word entries contain text, start/end seconds, and an optional uncalibrated probability. Segment confidence stays `null` unless the runtime exposes a defined score; average log probability must not be mislabeled as calibrated confidence.

### Canonical summary schema

The top level contains:

~~~json
{
  "schema_version": 1,
  "session_id": "01J...",
  "transcript_revision": 2,
  "model": {
    "runtime": "llama.cpp",
    "model_id": "google/gemma-4-12B-it-qat-q4_0-gguf",
    "revision": "<pinned-commit>",
    "file_sha256": "<sha256>",
    "context_tokens": 32768,
    "thinking": false
  },
  "prompt_version": "meeting-summary-v1",
  "title": "Deployment planning",
  "overview": "...",
  "key_points": [],
  "decisions": [],
  "action_items": [],
  "open_questions": [],
  "risks": [],
  "blockers": []
}
~~~

All factual list items use an evidence envelope: stable ID, concise text, `certainty` (`explicit`, `inferred`, or `unknown` as applicable), optional heuristic `confidence`, `source_segment_ids`, and derived `source_timestamps`. An action item supports:

~~~json
{
  "id": "action-001",
  "task": "Prepare the deployment checklist.",
  "owner": "Alex",
  "owner_status": "explicit",
  "due_date": "2026-08-07",
  "due_date_status": "explicit",
  "confidence": 0.91,
  "evidence": [
    {
      "transcript_revision": 2,
      "segment_id": "segment-000431",
      "source_track": "system",
      "start_seconds": 2538.12,
      "end_seconds": 2544.9,
      "display_timestamp": "00:42:18"
    }
  ]
}
~~~

Post-validation enforces:

- `owner_status == "unknown"` implies `owner == null`.

- `due_date_status == "unknown"` implies `due_date == null`.

- An ISO date is emitted only when the transcript explicitly gives an unambiguous calendar date relative to a known meeting date. Keep the original phrase in `due_date_text`.

- Every evidence ID exists in the selected transcript revision; timestamps are generated by EchoForge from those segments rather than trusted from model text.

- `explicit` requires direct supporting text. `inferred` is visually marked and never converted to explicit by final synthesis.

- A model confidence number is not statistically calibrated. EchoForge labels it heuristic and derives final certainty chiefly from evidence validity and explicitness.

### Evidence identity and revision behaviour

This is settled **before** `transcript.schema.json` and `summary.schema.json` are frozen, because it determines their shape. Segment IDs are stable only inside one transcript revision, so a bare segment ID is not a durable reference.

- **Evidence identity is the pair `transcript_revision` + `segment_id`.** Neither half identifies a segment on its own. Every evidence entry carries both, and the summary's top-level `transcript_revision` records the revision the summary as a whole was generated from.

- **A historical summary always opens its exact source transcript revision.** Transcript revisions are immutable and are retained as long as any summary references them. Opening summary r1 shows the transcript it was actually written from, not the newest one. Retention is therefore a function of references, and deleting a transcript revision that still has dependents requires explicit confirmation.

- **Selecting a new transcript revision marks dependent summaries stale.** Stale is a visible state on the summary, not a silent condition. The stale summary remains fully readable and its evidence still resolves, because it continues to point at its own revision.

- **Regeneration creates a new summary revision.** Bringing a summary up to date is a new revision generated against the newly selected transcript, never an in-place edit of the old one. The previous summary revision survives until explicitly deleted.

- **Derived source times are preserved as audio-navigation fallback.** Each evidence entry stores `start_seconds`/`end_seconds` on the session timeline alongside the ID pair. If a segment ID cannot be resolved — a missing revision, a partially recovered session — the UI can still seek the immutable audio at the recorded time and say plainly that the transcript segment is unavailable. These times are derived by EchoForge from the cited segment; they are never taken from model output.

- **Historical evidence links are never silently rebased or rewritten.** EchoForge does not remap old summaries onto new segment IDs, and does not repair a broken link by guessing a nearby segment. A link either resolves against its own revision, or the UI shows it as unresolved with the audio fallback. Re-running the summary is the supported way to move to a newer transcript.

The practical consequence for Phase 5: optional diarization produces a **new transcript revision**, which marks derived summaries stale and offers regeneration. It does not migrate existing evidence.

### Long-meeting algorithm

1. Split at speaker/segment boundaries into **8K–12K transcript-token chunks**, normally 15–25 minutes, with only two or three boundary segments of overlap (at most about 60 seconds).
2. Ask for a concise chunk digest and typed factual candidates. Require evidence IDs from the supplied allow-list and prohibit filling missing owners/dates.
3. Reject or downgrade candidates whose cited segment does not lexically/semantically support the claim. This validator is conservative; it never invents replacement facts.
4. Deduplicate candidates only when normalized content and evidence overlap/adjacency support a merge. Keep ambiguous similar commitments separate.
5. Feed validated digests/candidates—not full raw chunks—to final synthesis within a 32K context. If they still exceed it, recursively summarize groups, preserving evidence sets.
6. Revalidate the final result against the transcript and status invariants. A final item cannot acquire new evidence or become more certain than its inputs.

This design avoids silent truncation and keeps a click path from every extracted commitment to the original transcript/audio.

### Failure and recovery strategy

| Failure | Required behavior |
|---|---|
| App/process crash | Completed `.wav` chunks remain immutable. On startup replay `events.jsonl`, reconcile `session.json`, repair only active `.part.wav` headers after trimming an incomplete sample frame, and mark the session recovered. |
| Interrupted recording | Close/repair each track independently, preserve timeline gaps and epochs, and offer Resume as a new epoch. |
| Sleep | Finalize on suspend when possible; resume into a new epoch and record an explicit gap. |
| Device disconnection | Finalize the affected active chunk; healthy track continues; persistent degraded UI; no silent default-device switch. |
| Disk full | Threshold warning/control stop, recovery reserve, independent writer error handling. Preserve every completed chunk and any partial bytes. |
| Invalid audio chunk | Never delete it. Move only the invalid active artifact to a `diagnostics/quarantine` reference, record the gap, and continue processing valid chunks. Completed files are not moved automatically. |
| Worker crash | Job becomes failed with exit code/stage; source is untouched. Restart from durable chunk/track checkpoints into a new output revision. |
| Missing dependency | Preflight names the exact component and repair action; capture remains usable if inference dependencies are absent. |
| Download failure | Resume `.partial`, verify expected length/hash, atomic rename. Keep the previous valid model active. |
| CUDA error / OOM | Terminate/unload, lower batch size, retry `int8_float16` for STT; then offer/perform CPU INT8 fallback. For summary, reduce operational context and re-chunk, then partial GPU offload/CPU if RAM allows. Never silently truncate. |
| Transcription/summarization failure | Mark only that stage/revision failed. Audio and all prior successful revisions remain available and re-runnable. |
| Malformed model JSON | Retain raw response in the private job workspace, validate, make one schema-constrained repair attempt, then fail visibly. Never activate malformed JSON. |
| User cancellation | Stop at a safe chunk/request boundary, send cancel, apply grace period, then kill the Job Object. Mark cancelled; source and completed checkpoints remain. |

Use temporary files in the destination directory, `Flush(true)`/equivalent at the defined durability boundary, then atomic rename. A manifest snapshot is replaceable; the journal is the recovery authority. Hash finalized chunks asynchronously after close without delaying audio callbacks.

Logs may contain timestamps, session/job IDs, versions, file hashes, byte/frame counts, HRESULTs, exception types/stacks, GPU/driver information, and relative artifact names. They must not contain raw audio, transcript text, summary text, prompts containing meeting text, API keys, authorization headers, user-supplied meeting titles, or full private paths.

## E. Phase-by-phase implementation plan

Each phase is a gate. Claude Code should finish its tests and completion criteria before creating features assigned to a later phase.

### Phase 0 — Proof of concept

| Item | Plan |
|---|---|
| Goal | Prove that the selected Windows playback endpoint and headset microphone can be recorded simultaneously into separate, valid, timeline-aligned files for at least ten minutes. Prove recovery from a killed process. |
| User-visible result | A console tool lists devices, accepts two IDs, displays levels/elapsed time/discontinuities, records 60-second chunks, and prints a machine-readable validation report. No production GUI. |
| Technical tasks | Create the solution/core contracts needed by the POC; enumerate stable endpoint IDs; **confirm on the pinned NAudio version that the lower-level `AudioCaptureClient.GetBuffer` overload exposing frame count, buffer flags, device position, and QPC position is reachable, along with `AudioClient.SetEventHandle`** (done: reachable on NAudio 2.3.0, no COM shim needed); implement the event-driven dual capture loop over `AudioClient`/`AudioCaptureClient` for the render endpoint (loopback) and the microphone; implement bounded queues, PCM16 chunk writer, **QPC-anchored timeline with silence advanced from the shared session clock**, chunk finalization, SHA-256, manifest/journal, and active-WAV repair; generate an aligned stereo/mono diagnostic mix without altering sources; record actual device formats, the device-position-to-delivered-frame ratio per endpoint, and memory/queue metrics. Build the signal-based chirp harness: play timed chirps through the selected endpoint and speak/tap near the microphone to measure end-to-end offset and drift rate. |
| Main files/modules | `poc/EchoForge.AudioCapture.Poc/*`; `src/EchoForge.Contracts/Audio/*`; `src/EchoForge.Audio.Windows/{AudioDeviceCatalog,WasapiPacketCapture,DualTrackRecorder,CaptureClock,BoundedAudioQueue,PcmChunkWriter,WavRepair}.cs`; `schemas/session.schema.json`; hardware-test scripts. |
| Dependencies | .NET 10 SDK; NAudio 2.3.x; `System.Text.Json`. No Python, model, SQLite, FFmpeg, installer, or WPF dependency. |
| Tests | Unit-test chunk boundaries, frame math, native-to-PCM16 conversion, **silence advanced from the QPC session clock**, jitter rejection, timestamp-overlap dropping, writer sealing after stop, bounded-queue overflow reporting, drift-rate estimation, alignment-gate evaluation, atomic manifest updates, and WAV repair. Hardware-test ten minutes of alternating/simultaneous playback and microphone; **at least one continuous 60-minute qualification run**; loopback silence; Windows notification inclusion; 44.1/48 kHz mismatch if available; process kill after at least two chunks; pause-like stop/restart; device unplug; sleep/resume; near-full-disk test on a quota-limited test volume where practical. Validate every finalized WAV with an independent reader. **Long-duration and physical-hardware tests are tracked in `docs/HARDENING_BACKLOG.md` and are not satisfied by automated tests.** |
| Completion criteria | Ten continuous minutes produce separate listenable tracks and at least nine finalized chunks each; no missing/duplicated chunk index; **post-correction alignment error is at most 100 ms after ten minutes, measured by the signal-based chirp harness**; **residual corrected drift is at most 50 ms per hour, demonstrated by at least one continuous 60-minute run**; every packet carries a QPC position, and no timeline value is derived from packet arrival time or from an uncalibrated device position; capture queue stays bounded; working memory is stable; killing the process preserves 100% of completed chunks and startup repairs or explicitly quarantines the active part; no capture thread performs blocking disk I/O. Results and raw metrics are saved under a POC report. **Track duration equality never satisfies an alignment criterion.** |
| Main risks | Endpoint-specific formats, reachability of the lower-level NAudio timestamp overload, no loopback packets during silence, independent-clock drift, Bluetooth profile switching, vendor drivers. Add the smallest Core Audio COM shim only if a gate measurement cannot be reached through NAudio's lower-level wrappers. |
| Explicitly excluded | WPF/product GUI, transcription, models, diarization, summarization, library, cloud, per-process capture, installer, video, live transcription. |

**Phase 0 is blocking for design, and its hardware qualification is deferred.** The capture and timeline design must be settled before frontend work, and it is: the packet-timestamp architecture is implemented and covered by automated tests. The long-duration and physical-hardware acceptance runs listed in `docs/HARDENING_BACKLOG.md` are **deliberately deferred to a hardening stage** by explicit product decision. Until those runs happen, Phase 0 is *implementation complete and automated-test green*, and **not production-qualified**. The thresholds are unchanged and must not be weakened; deferring a test is not passing it.

**Why the gates are expressed as a rate.** An absolute ten-minute offset alone does not predict a three-hour run. A device drifting at the old 100 ms-per-ten-minutes reading would accumulate roughly 1.8 s over three hours and fail the MVP limit sevenfold, while still passing Phase 0. The gates are therefore split: a **100 ms absolute ceiling at ten minutes** catches gross start-up and alignment faults, and a **50 ms per hour residual drift ceiling**, proven over a continuous 60-minute run, catches slow clock divergence. At 50 ms/hr a three-hour session lands near 150 ms, inside the 250 ms acceptance limit in Section G with margin for device variation. Both are measured **after** derivative correction; uncorrected drift may be larger and is reported separately as a diagnostic.

### Phase 1 — Recording application

| Item | Plan |
|---|---|
| Goal | Turn the proven recorder into a dependable manual Windows utility with recoverable session storage. |
| User-visible result | Standard WPF window and tray icon with playback-device and microphone selectors, separate level meters, Start/Pause/Resume/Stop, persistent red recording/degraded indicator, duration, estimated storage rate/free disk, and recovery notices. Sessions survive restart and are visible by ID/date. |
| Technical tasks | Create WPF/MVVM shell and application services; move the POC recorder behind `IAudioRecorder`; add consent reminder before Start; implement capture state machine, device notifications, power events, level sampling, tray/taskbar indicator, bounded UI updates, session repository, atomic JSON snapshots/JSONL journal, recovery scan, disk thresholds/reserve, settings, and single-instance behavior. Keep selected endpoint IDs fixed during recording. Continue the healthy track on a one-device failure and show degraded state. |
| Main files/modules | `src/EchoForge.App/{App.xaml,MainWindow.xaml,ViewModels,Views,Services}`; `EchoForge.Core/Recording/*` and `Sessions/*`; `EchoForge.Infrastructure/Storage/*` and `Recovery/*`; reused `EchoForge.Audio.Windows`; session schema. |
| Dependencies | Phase 0; WPF/.NET 10; NAudio; Microsoft.Extensions.DependencyInjection/Logging only if their use remains small; a minimal tray-icon library or a tiny WinForms NotifyIcon adapter. |
| Tests | View-model/state-transition unit tests; fake clock/device/disk/writer tests; start rollback if one endpoint fails; double-stop and rapid pause/resume; endpoint notification and sleep simulations; journal corruption/truncated-last-line recovery; process-kill integration tests; level-meter UI throttling; three-hour soak with generated/playback speech and memory/queue sampling. |
| Completion criteria | All required recording controls work; indicator cannot disappear while capture is active; every session is a recoverable folder outside the repo; completed chunks are never deleted by recovery; disk and device failures are visible; three-hour capture passes the bounded-memory criteria in Section G; app restart discovers and reconciles interrupted sessions. |
| Main risks | UI thread accidentally owns audio work, tray indicator disagrees with capture state, disk exceptions cascade, recovery mutates valid data. The recorder's authoritative state must live below the view model. |
| Explicitly excluded | Any ML/runtime downloads, transcription, summarization, remote diarization, rich results UI, automatic recording, per-app capture, cloud, live waveform editor. |

### Phase 2 — Local transcription

| Item | Plan |
|---|---|
| Goal | Produce a revisioned, timestamped canonical transcript locally from preserved chunks, with progress, cancellation, reprocessing, and CPU fallback. |
| User-visible result | “Transcribe” and “Transcribe again” actions, model/profile selector, progress by track/chunk, cancel, actionable errors, hardware summary, and basic JSON/TXT/SRT/VTT outputs. Microphone text is labeled You and system text Remote. |
| Technical tasks | Define NDJSON worker protocol and supervisor/Job Object; create app-local Python worker and locked environment; stream-decode/normalize chunks to aligned 16 kHz mono derivatives; form ten-minute per-epoch STT windows with five-second overlap so 60-second source chunks never cut recognition context; implement conservative Silero VAD, faster-whisper word timestamps, language selection/detection, optional glossary/initial prompt, per-window/track checkpoints and overlap dedupe, timeline merge, cross-track overlap IDs, transcript schema validation, atomic revision activation, SRT/VTT cue construction, model registry/download/hash verification, CUDA preflight, adaptive batch sizing, `int8_float16` retry, CPU INT8 fallback, progress and cancellation. Unload/exit at job end. |
| Main files/modules | `schemas/{transcript,worker-protocol}.schema.json`; `EchoForge.Contracts/Workers/*` and `Transcripts/*`; `EchoForge.Infrastructure/Workers/{WorkerSupervisor,WindowsJobObject}.cs`; `worker/echoforge_worker/{main,protocol,audio,transcribe,models}.py`; `tests/worker_tests`. |
| Dependencies | Phases 0–1; **`artifacts/manifest.json` populated and passing `verify-models.ps1` before any production download**; Python 3.12 app-local distribution; uv for development lock; faster-whisper, CTranslate2, PyAV and Silero VAD dependencies; pinned model snapshots referenced by full commit SHA. NVIDIA runtime components only for the CUDA profile. NVIDIA documents Windows pip installation for CUDA 12 cuDNN packages ([cuDNN Windows guide](https://docs.nvidia.com/deeplearning/cudnn/installation/latest/windows.html)); exact redistribution/download terms must be reviewed in Phase 6. |
| Tests | Protocol framing/unknown version; child crash/cancel/timeout; path with spaces/non-ASCII; golden transcript schema; segment ordering/overlap; You attribution; SRT/VTT validity; silent chunks; invalid WAV; language and technical-acronym samples; CUDA absent; injected CUDA error/OOM; model hash/download interruption; repeated transcription creates a new revision and leaves old/audio data intact. Benchmark turbo, large-v3, and CPU fallback on the same held-out meeting clips. |
| Completion criteria | A one-hour representative session produces a valid local transcript with navigable timestamps; 100% of microphone segments are labeled You; all segment times fall within session epochs; re-run needs no recording; cancel/failure preserves sources and previous outputs; simulated GPU failure reaches a documented CPU result; network-disabled transcription succeeds after installation. |
| Main risks | Windows CUDA/cuDNN wheel compatibility, model alias drift, Whisper hallucinations on silence/music, names/acronyms, time mapping across chunks. Pin revisions and treat transcript correction/re-run as normal. |
| Explicitly excluded | Summaries, action extraction, remote-speaker diarization, live transcription, forced cloud fallback, automatic model update, recording-time GPU work. |

### Phase 3 — Local summarization

| Item | Plan |
|---|---|
| Goal | Generate the best practical local meeting summary on 16 GB VRAM while making unsupported facts difficult to emit and easy to audit. |
| User-visible result | Local overview, key points, decisions, action items, owners/dates when supported, open questions, risks, blockers, and clickable evidence timestamps. Re-run supports model/prompt revisions and shows explicit/inferred/unknown statuses. |
| Technical tasks | Pin a compatible llama.cpp Windows release and the official Gemma 4 12B QAT Q4 GGUF/revision/hash; launch ephemeral text-only server with one slot, 32K context, Q8 KV cache initially, thinking off, fixed seed, offline mode; implement transcript tokenization/chunking, per-chunk extraction prompt, simple generation schema, full JSON Schema validation, evidence allow-list/resolution, owner/date invariants, conservative dedupe, recursive synthesis, prompt versioning, checkpointing, one malformed-JSON repair, cancellation, OOM re-chunk/context fallback, and atomic summary revisions. Render inferences separately and default owner/date inference to off. |
| Main files/modules | `schemas/summary.schema.json`; `EchoForge.Core/Summaries/*`; `worker/echoforge_worker/{summarize,chunking,evidence,models}.py`; `worker/prompts/{extract-v1,synthesize-v1,repair-v1}.txt`; `tests/fixtures/summary-benchmark/*`. |
| Dependencies | Phase 2 canonical transcript; pinned llama.cpp binary; Gemma model. Keep tokenizer/template coupled to the model revision. No Ollama service. |
| Tests | Schema fuzz/property tests; nonexistent evidence IDs; null/status conditionals; relative-date resolution from known meeting date; ambiguous Friday/no known date; model JSON truncation; duplicate actions at chunk overlap; contradictory chunks; very long synthetic transcript; cancelled/failed synthesis; GPU OOM and reduced-context retry; network blocked. Iterate the pipeline and prompts against the **3–5 meeting development corpus**; run the **10–20 meeting release corpus** only as the acceptance gate. Compare Gemma 4 12B Q4 with Ministral 3 14B Instruct Q4_K_M using identical transcript, schema, evidence rules, and token budget. |
| Completion criteria | Every activated decision/action has at least one resolvable segment ID and generated timestamp; unknown owners/dates remain null/unknown; explicit facts have direct evidence; malformed output never activates; a three-hour transcript completes without silent truncation; the default model fits the actual GPU at 32K or falls back through a documented, non-silent path. The model bake-off is recorded with quality, latency, peak VRAM, and failure rates. |
| Main risks | Hallucinated commitments, excessive deduplication, long-context degradation, quantized-model JSON quirks, new Gemma/llama.cpp integration maturity, VRAM estimates. Evidence verification and the real-meeting gate are release blockers. |
| Explicitly excluded | Remote diarization, cloud/API calls, autonomous follow-ups, emails/tasks, calendar updates, reasoning mode, model fine-tuning, retrieval databases, vector search, live summaries. |

**Summary quality gate.** Score action/decision factual precision and recall, exact owner/date precision, evidence validity, key-point coverage, contradiction handling, readability, latency, and peak VRAM. The recommended release target is at least 95% precision for emitted actions/decisions, at least 85% recall on the annotated set, 100% valid evidence references, and no unsupported explicit owner/date. These are product acceptance targets, not claimed model benchmarks. Change the default to Ministral only if it improves the preregistered composite by at least five percentage points with no material memory/failure regression.

**Two corpora, kept apart.** Annotating meetings is the most expensive work in Phase 3 and it sits on the critical path, so it is split by purpose:

| Corpus | Size | Purpose | Rules |
|---|---|---|---|
| **Development** | 3–5 meetings | Day-to-day pipeline, chunking, and prompt iteration. Fast enough to re-run constantly. | May be inspected freely. Prompts may be tuned against it. Never quoted as an acceptance result. |
| **Release** | 10–20 meetings | The Phase 3 acceptance gate and the Gemma-versus-Ministral decision. | Held out. Run only when a candidate is believed ready, with the scoring criteria preregistered before the run. No prompt tuning against its contents. |

Corpora do not overlap. If a release meeting has to be moved into development to debug a failure, it leaves the release set permanently and is replaced.

**Two measurements, kept separately reportable.** Speech-recognition quality and summary quality fail for different reasons and must not be averaged into one number:

- **STT / audio evaluation** runs on recorded audio and scores word and name accuracy, timestamp accuracy, You/Remote attribution, and hallucination on silence and music. It is what decides turbo versus large-v3 versus the CPU profile.
- **Summary evaluation** runs on a **fixed, human-corrected transcript**, so a summarizer is never penalised for upstream recognition errors. It scores factual precision and recall, owner/date precision, evidence validity, coverage, and readability. It is what decides Gemma versus Ministral.

Both are recorded per meeting so an end-to-end regression can be attributed to the stage that caused it.

**Estimate:** Gemma 4 12B Q4 should synthesize a one-hour meeting in roughly 1–10 minutes on a modern 16 GB GPU, depending on prompt count, transcript density, and GPU generation. This is not a published guarantee. Record time-to-first-token, prompt/decode tokens per second, total stage time, and peak dedicated/shared GPU memory.

### Phase 4 — Results and meeting library

| Item | Plan |
|---|---|
| Goal | Make recordings, transcripts, and summaries easy to review, search, correct at the speaker-label level, copy, export, delete, and reprocess. |
| User-visible result | Previous-meeting library; transcript/summary tabs; full-text search; selection/copy; evidence links; synchronized playback from timestamps; remote speaker rename; Markdown, text, JSON, SRT, and VTT exports; explicit deletion and re-run controls. |
| Technical tasks | Build library projection and rebuildable SQLite/FTS index; paginate/virtualize transcript view; implement search highlights; map evidence click to transcript and audio; build aligned two-track playback/mix derivative; speaker alias overlay without rewriting original transcript; export service with revision/model metadata; safe filename sanitization; explicit delete confirmation and Recycle Bin where supported; rebuild index; resolve evidence through the `transcript_revision` + `segment_id` pair, opening each summary against its own source revision; stale-summary marker and regenerate action when a different transcript revision is selected; unresolved-evidence presentation using the stored time fallback. |
| Main files/modules | `EchoForge.App/Views/{Library,Transcript,Summary,Playback}*`; `EchoForge.Core/{Library,Search,Exports,Playback}/*`; `EchoForge.Infrastructure/Index/*`; `EchoForge.Audio.Windows/Playback/*`. |
| Dependencies | Phases 1–3; Microsoft.Data.Sqlite or another pinned small SQLite provider; NAudio playback. JSON remains canonical. |
| Tests | Index rebuild from folders; corrupt/missing DB; large virtualized transcript; phrase search; evidence seek within 250 ms; pause/seek across chunk boundary; speaker alias persistence; every export parses; Markdown escaping; Unicode titles; deleting active/running session prohibited; Recycle Bin/cancel behavior; reprocess maintains revision history and source hash. |
| Completion criteria | Users can find and open any valid session without scanning files manually; search locates expected segments; evidence click seeks to the correct audio neighborhood; required formats export; renaming remote speakers changes presentation but not source evidence; deleting is explicit; transcript/summary can be rerun without rerecording. |
| Main risks | Audio seek across epochs/drift, UI memory on long transcripts, SQLite becoming accidental source of truth, ambiguous remote rename without diarization. |
| Explicitly excluded | Remote diarization engine, rich text editor, collaborative annotations, sync, semantic/vector search, calendar/task integrations, automatic deletion, advanced visual design. |

### Phase 5 — Optional remote-speaker diarization

| Item | Plan |
|---|---|
| Goal | Optionally distinguish anonymous remote speakers on the **system track only** after core recording/transcription/summary behavior is reliable. |
| User-visible result | “Separate remote speakers” reprocessing option; `Speaker 1`/`Speaker 2` labels with rename controls and a warning that labels are estimates. You remains deterministic from the microphone track. |
| Technical tasks | Add a separately downloaded pyannote community diarization profile; accept/show license and gated-access requirements; diarize 16 kHz system derivative; map turns to words/segments using exclusive diarization where appropriate; represent overlaps; stabilize anonymous labels within one session; create a new transcript revision and mark its derived summary stale; provide CPU/GPU progress/cancel. |
| Main files/modules | `worker/echoforge_worker/diarize.py`; `EchoForge.Core/Speakers/*`; model manifest; diarization fixtures/tests. |
| Dependencies | Phases 2–4; pyannote.audio and `speaker-diarization-community-1` model; compatible PyTorch/CUDA stack isolated from faster-whisper conflicts. |
| Tests | Two/three-speaker clean meeting, crosstalk, short turns, same-gender similar voices, music/noise, one-speaker case, GPU absent, model-access failure, repeat label stability. Human diarization-error review; no test treats a label as a person's identity. |
| Completion criteria | Feature is wholly optional; failure leaves the You/Remote transcript selected and intact; You is never relabeled; anonymous labels and uncertainty are clear; diarization produces a **new transcript revision** that marks dependent summaries stale and offers regeneration, and **no existing evidence link is rebased onto the new revision**. |
| Main risks | Diarization error and overlap, gated model setup, PyTorch dependency/VRAM conflict, user interpreting anonymous clusters as identity. This phase can be omitted without weakening the MVP. |
| Explicitly excluded | Biometric voiceprints, cross-meeting speaker recognition, auto-naming people, meeting participant roster matching, real-time diarization. |

### Phase 6 — Packaging and reliability

| Item | Plan |
|---|---|
| Goal | Deliver a clean, diagnosable, offline-capable Windows installation to a nontechnical user. |
| User-visible result | Signed per-user installer/uninstaller, first-run setup wizard, hardware/model recommendations, resumable verified downloads, microphone/loopback test, repair flow, diagnostics bundle, and operation with the network disabled after setup. |
| Technical tasks | Pin .NET SDK/runtime, Python distribution/wheels, llama.cpp, all model revisions/hashes, NAudio and transitive packages; produce self-contained x64 publish; create app-local Python runtime rather than require system Python; create Inno installer with `PrivilegesRequired=lowest` where feasible; implement hardware detection (GPU vendor/name/VRAM/driver, CUDA probe, CPU features, RAM, disk); model/dependency registry and license notices; resumable download/atomic activation; repair/uninstall that preserves user sessions by default; offline environment flags; code signing; versioned migration/recovery; diagnostics redaction; clean-VM and antivirus scanning. |
| Main files/modules | `packaging/inno/EchoForge.iss`; `scripts/{bootstrap-dev,fetch-dev-models,verify-models,package}.ps1`; `EchoForge.Infrastructure/Setup/*` and `Diagnostics/*`; `third_party/NOTICE.md`; CI definitions if a repository host is later chosen. |
| Dependencies | Stable Phases 0–4; Phase 5 optional. Authenticode certificate for distribution; current Inno Setup license appropriate to private/commercial status; official/pinned artifact hosts. |
| Tests | Fresh supported Windows 11 VM with no SDK/Python/CUDA toolkit; standard user install; path/non-ASCII user profile; NVIDIA CUDA and CUDA-unavailable machines; AMD/Intel CPU fallback; interrupted/corrupt downloads; upgrade/downgrade policy; uninstall preserving sessions; network physically/firewall blocked after setup; Windows sleep/reboot/crash; three-hour soak; common antivirus/SmartScreen checks; diagnostics content audit. |
| Completion criteria | Clean VM can install, download chosen dependencies/models, record, transcribe, summarize, export, restart, and repeat offline; missing/CUDA/OOM cases give safe fallbacks; no system Python or developer SDK is required; installer and binaries are signed for distribution; uninstall never removes sessions/models without a separate explicit choice; all third-party notices ship. |
| Main risks | CUDA/cuDNN/Python binary compatibility, very large first-run downloads, model-host changes, antivirus false positives, code-signing reputation, commercial installer/model license compliance. |
| Explicitly excluded | Docker, permanent local service, auto-update service, background telemetry, silent model upgrades, bundled cloud credentials, enterprise deployment tooling. |

### Phase 7 — Optional cloud summarization

| Item | Plan |
|---|---|
| Goal | First provide a zero-integration manual handoff to ChatGPT or Claude; only later, if wanted, add explicit transcript-text API summarization. |
| User-visible result | **MVP portion:** copy/export a redacted or selected transcript plus an EchoForge summary prompt/schema, with a clear “you are leaving local processing” notice. **Post-MVP optional portion:** provider selection, exact payload preview, estimated token/cost display, one-run consent, progress, and imported evidence-linked result. |
| Technical tasks | Manual-copy template first. For a later API adapter, define a small provider-neutral interface; store keys in Windows Credential Manager/DPAPI; transmit only the chosen transcript text and segment IDs; use provider schema-constrained output; set privacy-preserving storage options where available; show current retention links; redact logs; cancel/time out; validate returned JSON through the same evidence validator; record provider/model/config in the summary revision. No audio upload code exists. |
| Main files/modules | `EchoForge.Core/ManualCopy/*` for MVP; later `EchoForge.Cloud.Contracts/*` and explicitly chosen provider adapter; `EchoForge.App/Views/CloudConsent*`; provider-specific tests. |
| Dependencies | Phase 4 for manual copy; stable local workflow and a fresh provider/privacy review for any API. The OpenAI adapter should use official API documentation and SDK; Anthropic likewise. |
| Tests | Manual prompt includes only selected text; clipboard failure; API mock payload snapshot proving no audio/path leakage; explicit consent required every run; missing/revoked key; timeout/rate limit; malformed JSON; provider retention/config display; evidence IDs validated; network failure leaves local data/results intact. |
| Completion criteria | Manual copy works in the MVP without any network call. A later API request cannot execute without a user action, payload preview, and consent; packet/mock inspection confirms audio is never sent; unknown owners/dates and evidence rules remain identical to local mode. |
| Main risks | Privacy/retention changes, accidental over-sharing, cost, key leakage, provider/model churn, user assuming cloud output is authoritative. |
| Explicitly excluded | Automatic cloud fallback, background upload, audio upload, account system, cloud transcript storage, team sharing, API work before the local quality/reliability gates. |

### Cross-phase test data policy

Use synthetic audio for repeatability and consented real meeting audio for quality. Never check private raw meetings or their transcripts into source control. Store benchmark manifests with anonymous IDs and hashes; keep the actual corpus under an ignored local directory with access controls. Gold annotations distinguish explicit facts, acceptable inferences, and unknowns. At least one test meeting should include accents, jargon, names, overlapping speech, notification sounds, silence, a relative date, a deliberately ambiguous owner, and a statement that is discussed but not decided.

The Phase 3 corpus is split into a 3–5 meeting **development** set for iteration and a held-out 10–20 meeting **release** set for the acceptance gate; the two never overlap, and a meeting promoted out of the release set is replaced rather than reused. Each meeting carries two independent annotation layers — a corrected transcript for scoring speech recognition, and gold facts with evidence for scoring summarization against that corrected transcript — so the two stages stay separately measurable.

## F. Claude Code handoff

The following block is ready to paste into Claude Code:

~~~text
Project: EchoForge
Workspace: C:\EchoForge
Platform: Windows 11 x64

Objective
Build a small private desktop utility with exactly this workflow:
Record -> Transcribe locally -> Summarize locally -> Extract Actions.
Implement one phase at a time. Do not introduce a later-phase feature until the
current phase's tests and completion criteria in
docs\ARCHITECTURE_AND_IMPLEMENTATION_PLAN.md pass.

Approved stack
- C# 14 / .NET 10 LTS / WPF modular monolith.
- NAudio 2.3.x over WASAPI shared mode.
- Selected playback endpoint via loopback plus selected microphone, simultaneously,
  into separate immutable 60-second PCM16 WAV chunks.
- Own the capture loop over AudioClient/AudioCaptureClient. Do NOT use
  WasapiCapture.DataAvailable as a timestamp source. Drain with the lower-level
  NAudio GetBuffer overload exposing frame count, buffer flags, device position, and
  QPC position. Verified reachable on NAudio 2.3.0; no COM shim needed.
- qpcPosition is the canonical session-time anchor. Delivered mix-format frame counts
  describe the audio written. devicePosition is DIAGNOSTIC ONLY and must never be used
  as a mix-format frame counter unless that endpoint's rate has been calibrated: a
  16 kHz headset resampled to a 48 kHz mix advanced device position at one third of the
  delivered frame rate. Missing time during silence or a stall is advanced from the
  shared QPC session clock. Packet arrival time is never a clock.
- Both tracks share one epoch fixed before either endpoint opens, and one stop instant.
- 48 kHz system stereo + microphone mono when the device supports it; otherwise
  record native rate/layout and normalize derivatives.
- Canonical versioned JSON + append-only JSONL journal. SQLite is a rebuildable index.
- Short-lived Python 3.12 worker; NDJSON on stdin/stdout, artifact paths in messages.
- faster-whisper/CTranslate2: large-v3-turbo CUDA FP16 default, large-v3 accuracy
  option, small.en/small CPU INT8 fallback.
- Microphone speaker is always You; system track is Remote in the MVP.
- Short-lived pinned llama.cpp llama-server, localhost only, one slot, no permanent
  service. Official google/gemma-4-12B-it-qat-q4_0-gguf, 32K operational context,
  Q8 KV initially, text only, thinking disabled. Unload STT before loading the LLM.
- Hierarchical 8K-12K transcript chunk extraction, evidence validation, conservative
  deduplication, final synthesis. All decisions/actions cite transcript segment IDs.
- Self-contained win-x64 publish and signed Inno Setup per-user installer.
- Runtime data belongs under %LOCALAPPDATA%\EchoForge, never in the repository.

Repository
- src\EchoForge.App: WPF only
- src\EchoForge.Core: use cases, states, policies
- src\EchoForge.Contracts: versioned DTOs and interfaces
- src\EchoForge.Audio.Windows: NAudio/WASAPI and playback
- src\EchoForge.Infrastructure: storage, recovery, SQLite, process supervision
- worker\echoforge_worker: inference worker
- schemas: JSON Schemas
- poc\EchoForge.AudioCapture.Poc: Phase 0 console proof
- tests: unit, integration, hardware, and Python tests
- packaging\inno, scripts, third_party, docs

Dependency direction
App -> Core/Contracts.
Audio.Windows and Infrastructure -> Core/Contracts.
Core -> Contracts only.
Core/Contracts must not reference WPF, NAudio, SQLite, Python, or inference runtimes.

Development order
0. Dual-track audio proof and interruption recovery.
1. Recording WPF application and recoverable session store.
2. Local transcription and exports.
3. Evidence-backed local summarization and real-meeting model bake-off.
4. Results UI, search, playback, meeting library, exports, deletion, reprocessing.
5. Optional remote diarization.
6. Packaging/reliability/offline clean-machine verification.
7. Manual copy to ChatGPT/Claude; direct cloud APIs only as a later opt-in.

Important interfaces
- IAudioDeviceCatalog: enumerate render/capture devices with stable IDs and formats.
- IAudioRecorder: StartAsync, PauseAsync, ResumeAsync, StopAsync; state/events/levels.
- ISessionStore: create, append durable event, snapshot, reconcile, enumerate.
- IJobSupervisor: run/cancel child job, stream typed progress, enforce Job Object.
- ITranscriptRepository / ISummaryRepository: immutable revisions + selected revision.
- IModelRegistry: pinned ID/revision/hash/status/capability; no mutable aliases.
- IExportService, ISearchIndex, IPlaybackService.
- Worker protocol_version=1 with start_job/cancel commands and
  started/progress/checkpoint/warning/result/error/cancelled events.

Schema invariants
- Implement schemas\session.schema.json, transcript.schema.json, summary.schema.json,
  artifact-manifest.schema.json, and worker-protocol.schema.json before their producers.
- A microphone segment has source_track=microphone and speaker_name=You.
- Transcript segment IDs are stable inside a transcript revision, and ONLY inside it.
- Evidence identity is transcript_revision + segment_id. Neither half alone is a
  durable reference. Each evidence entry also stores derived start/end seconds as an
  audio-navigation fallback.
- A summary always opens its own source transcript revision. Selecting a different
  transcript revision marks dependent summaries stale; regeneration creates a NEW
  summary revision. Never silently rebase or rewrite historical evidence links.
- owner_status/due_date_status are explicit, inferred, or unknown.
- Unknown owner/date means the corresponding value is null.
- Every decision/action cites existing evidence; EchoForge derives timestamps from it.
  Never trust model-generated timestamps.
- Never promote inferred to explicit during synthesis.
- Activate output only after schema and semantic validation, fsync, and atomic rename.

Artifact pinning
- artifacts\manifest.json lists every model and inference runtime with repository,
  full revision SHA, exact filename, size, SHA-256, license, runtime version, and
  verification date. Nothing is downloaded that is not in it.
- Never fetch from an unpinned main branch or a mutable model alias. verify-models.ps1
  fails the build on a branch name or a missing filename.

Development commands after scaffolding
  dotnet restore EchoForge.slnx
  dotnet build EchoForge.slnx -c Debug -warnaserror
  dotnet test EchoForge.slnx -c Debug
  uv sync --project worker --locked
  uv run --project worker pytest worker_tests
  dotnet run --project poc\EchoForge.AudioCapture.Poc
  dotnet run --project src\EchoForge.App
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\verify-models.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package.ps1 -Configuration Release

Do not assume these commands work until the corresponding phase creates them. Keep
the build green at each commit. The package script should perform dotnet test,
Python tests, self-contained win-x64 publish, file/hash/notice verification, and
Inno compilation. Pin SDK/package/runtime/model versions; do not use floating aliases.

First files to create
1. global.json, Directory.Build.props, Directory.Packages.props, .gitignore, README.md.
2. EchoForge.slnx and the Contracts, Core, Audio.Windows, POC, and test projects.
3. schemas\session.schema.json and the minimal session/audio DTOs.
4. IAudioDeviceCatalog, IAudioRecorder, CaptureEpoch, AudioChunkMetadata.
5. POC device enumeration, dual capture, chunk writer, journal, validation report.

First proof of concept
No production GUI. List endpoints, explicitly select one render endpoint and one
microphone, capture both concurrently through the owned AudioClient loop, display
levels, write separate 60-second PCM16 WAV chunks, record per-packet device/QPC
positions, buffer flags, and frame counts, insert loopback silence derived from
device position, and produce an aligned diagnostic mix. Kill the process after
multiple completed chunks, restart, recover the .part chunk, and independently
validate every WAV. Measure offset and drift RATE with timed chirps.
Gates: <=100 ms post-correction alignment error at ten minutes, <=50 ms/hour
residual corrected drift proven by at least one continuous 60-minute run, 100% of
finalized chunks preserved across a kill. Do not begin WPF work unless Phase 0 passes.

Coding conventions
- Nullable reference types and warnings-as-errors; analyzers enabled.
- Small immutable records at boundaries; explicit Result/error codes for expected
  failures; exceptions retain technical context.
- Async I/O with CancellationToken; no async void except WPF event adapters.
- Bounded Channels for audio; audio callbacks never block, allocate large buffers,
  hash, update UI, or write to disk.
- Inject IClock, filesystem, process launcher, and device interfaces for tests.
- One composition root; no service locator, global mutable state, or speculative
  repository/generic-base abstractions.
- JSON uses snake_case on disk/wire and explicit schema_version.
- Python: typed public functions, ruff/format/type checks, pytest, no import-time
  model download or GPU initialization.
- Persist UTC timestamps plus QPC-relative session seconds; display local time only.
- Source audio is immutable after chunk finalization. Derivatives are disposable.

Logging/privacy conventions
- Structured rolling local logs with stage, session/job ID, versions, hashes,
  durations, byte/frame counts, exception/HRESULT, and hardware diagnostics.
- Never log audio, transcript/summary/prompt content, API keys/tokens, auth headers,
  meeting titles, clipboard text, or full private paths.
- No telemetry and no network call except an explicit first-run/model download or
  future user-consented cloud action. Automated tests should fail unexpected network.

MVP boundary
Include manual recording controls, visible indicator, dual tracks, recovery, local
STT, You/Remote handling, local evidence-backed summary/actions, search/playback,
required exports, library, deletion/reprocessing, hardware/fallback behavior,
offline operation, installer, and manual-copy workflow.

Do not add video, OBS, virtual cables, live transcription, automatic recording,
per-application capture, biometric identification, calendar/bot/team/cloud-storage
features, mobile, plugins, microservices, Docker, permanent local services, or direct
cloud APIs before the local workflow is complete. Remote diarization is Phase 5 only.

Architectural mistakes to avoid
- Merging tracks by callback arrival time or assumed equal sample clocks.
- Using WasapiCapture.DataAvailable, or any packet arrival time, as a clock.
- Treating devicePosition as a mix-format frame count without calibrating its rate.
- Reading equal track durations as evidence of alignment.
- Reporting a deferred hardware test as passed.
- Referencing a transcript segment by segment_id without its transcript_revision.
- Downloading a model or runtime that is not pinned in artifacts\manifest.json.
- Mixing tracks before transcription or overwriting source audio with resampled data.
- Writing one multi-hour WAV or accumulating a meeting in memory.
- Silently switching endpoints, truncating context, or swallowing dropped audio.
- Keeping STT and summary models loaded together.
- Letting the model invent timestamps, owners, dates, decisions, or certainty.
- Treating SQLite, a UI view model, or model output as the canonical record.
- Sending transcript bodies through logs/NDJSON, or starting an unmanaged service.
- Spending time on UI polish or diarization before dual-track recovery is proven.

At every phase: update tests and documentation, report measured versus estimated
behavior, preserve existing session formats, and stop if the phase gate fails.
~~~

## G. MVP acceptance criteria

The MVP is accepted only when all applicable rows pass on a clean Windows 11 machine. Thresholds are test targets and may be tightened after baseline measurement; they must not be weakened silently.

| Area | Measurable criterion |
|---|---|
| Three-hour recording | Capture both selected devices for three hours. After the first ten minutes, app private working-set growth attributable to capture is no more than 200 MB; per-track queues remain bounded to at most five seconds; no unreported drops; the UI remains responsive. OS file cache is reported separately. |
| Separate sources | Session contains distinct system and microphone chunk series; both are independently playable/decodable; no mixed file is treated as source. |
| Chunk durability | Killing the app after at least two finalized chunks preserves 100% of finalized chunks. Recovery either repairs the active part with no more than the configured flush interval (target ≤3 seconds) of lost tail or retains it with an explicit gap/error. No later-stage failure deletes audio. |
| Timeline quality | After derivative correction, known chirps/checkpoints on the two tracks remain within **250 ms over a three-hour run**. The Phase 0 gates that predict this are a **100 ms absolute ceiling at ten minutes** and **at most 50 ms per hour residual drift** proven over a continuous 60-minute run. All three are **signal-based measurements**; equal track durations never satisfy them, because both tracks are padded to a shared stop instant. Every timeline value derives from the per-packet QPC position, never packet arrival time and never an uncalibrated device position. Discontinuities and sleep/device gaps are represented, not hidden. |
| Controls/indicator | Start, Pause, Resume, and Stop are idempotent where appropriate. A persistent red window/tray indicator and duration remain visible while recording; a degraded track is unmistakable. |
| Devices/disk | Selected stable device IDs and actual formats are stored. Disconnect/sleep/disk-threshold tests produce a controlled state and preserve completed chunks. Free space and estimated usage are visible. |
| Local transcript | With networking disabled, a recorded representative meeting yields schema-valid, ordered, timestamped JSON. Microphone segments are You; system segments are Remote. SRT/VTT cues are monotonic and in range. |
| GPU fallback | An injected CUDA-unavailable and OOM condition produces a clear notice and a completed CPU INT8 transcript, or an explicit user cancellation. It never loses source or silently changes output profile. |
| Local summary | With networking disabled, a valid transcript yields overview, key points, decisions, action items, open questions, risks, and blockers in schema-valid JSON. |
| Evidence | 100% of activated decisions/action items have at least one existing source segment ID; displayed timestamps are derived from it and seek to the matching transcript/audio. |
| Unknowns | In the gold corpus, 100% of deliberately unknown/ambiguous owners and dates remain `null`/`unknown` by default. No unsupported value is labeled `explicit`. |
| Summary quality gate | On the held-out **10–20 meeting release corpus**, scored against human-corrected transcripts: target ≥95% precision and ≥85% recall for emitted actions/decisions, 100% evidence-reference validity, and no unsupported explicit owner/date. The 3–5 meeting development corpus is excluded from this measurement. STT accuracy and summary accuracy are reported separately, never as one combined score. |
| Long meetings | A three-hour transcript completes via hierarchy without silent truncation, missing terminal sections, lost evidence, or unbounded memory. |
| Reprocessing | Users can create new transcript and summary revisions without rerecording; all source hashes remain unchanged and earlier successful revisions remain available until explicit deletion. |
| Library/results | Sessions survive restart; search returns expected text; evidence playback seeks within 250 ms of the segment; Markdown, text, JSON, SRT, and VTT exports pass parsers/golden tests. |
| Offline | After first-run dependency/model installation, block all network access and complete record, transcribe, summarize, search, playback, export, re-run, restart, and recovery. |
| Privacy | A network-capture test shows no telemetry or upload. Manual copy requires a user action. No API code sends audio. Log-content tests find no transcript, summary, prompt, key, title, or clipboard content. |
| Clean install | On a current clean Windows 11 x64 VM with no SDK, system Python, Ollama, FFmpeg, or CUDA toolkit, a standard user installs EchoForge, completes setup, and runs the end-to-end default or documented CPU path. Uninstall preserves session data by default. |
| Consent UX | Before each new recording, the UI makes the user responsible for consent and shows that recording is active. There is no automatic or hidden recording path. |

## H. Genuine risks

Probability reflects this product/hardware, not a universal statistic.

| Risk | Probability | Impact | Mitigation | Blocking status | Responsible phase |
|---|---|---|---|---|---|
| Windows endpoint/driver edge cases | Medium | High | Shared-mode endpoint capture; stable IDs; enumerate actual formats; real-device matrix; narrow native COM shim only when measured. | **Phase 0 blocker** | 0, 1, 6 |
| Bluetooth profile behavior | High if headset mic is Bluetooth Classic | High quality impact | Detect/report HFP formats/profile change; recommend USB/wired mic; test LE Audio separately; do not promise stereo A2DP plus classic headset mic. | Blocker only if target hardware is unusable | 0, 1, 6 |
| Independent audio-clock drift | Medium | High | Per-packet `qpcPosition` as the clock anchor; delivered frame counts as the audio written; silence advanced from the shared session clock; derivative resampling; drift expressed as a rate and proven over a 60-minute signal-based run. | **Phase 0 blocker, hardware run deferred** | 0, 1, 4 |
| Endpoint clock domain differs from the mix format | **Confirmed on test hardware** | High if unhandled | Never treat `devicePosition` as a mix-format frame counter without calibrating that endpoint's rate; anchor on QPC; record the device-position-to-delivered-frame ratio per endpoint in diagnostics. | Resolved in Phase 0 design | 0, 1 |
| Endpoint stalls without signalling | **Confirmed on test hardware** | High | Advance the timeline from the shared QPC clock while idle and once at stop, so a stalled endpoint yields recorded silence rather than a short track. | Resolved in Phase 0 design | 0, 1 |
| Echo, sidetone, or local voice duplicated on system track | Medium | Medium | Headphones; overlap/text-similarity marker; retain both; allow review; no destructive suppression. | Non-blocking if visible | 2, 4 |
| Device disconnection/default change | Medium | High | IMM notifications; no silent switch; finalize affected track, continue healthy track, explicit reconnect epoch. | Recording release blocker | 0, 1, 6 |
| Sleep/hibernate | Medium | High | Power events, close/flush, new epoch/gap on resume, recovery scan. Audio during sleep cannot be recovered. | Recording release blocker | 0, 1, 6 |
| Overlapping remote/local speech | High | Medium | Separate tracks; independent STT; overlap IDs; play both around evidence; do not force one-speaker attribution. | Non-blocking limitation | 2, 4 |
| Remote diarization quality | High variability | Medium | Keep out of MVP core; optional pyannote system-track pass; anonymous labels; rename; quality warning. | Not blocking MVP | 5 |
| Hallucinated decisions/actions | Medium | Critical trust impact | Evidence allow-list, semantic validation, explicit/inferred/unknown, null unknowns, human gold gate, clickable audio. | **Phase 3 blocker** | 3, 4 |
| Long transcript degradation/truncation | Medium | High | Segment-aware chunking, hierarchy, token accounting, checkpointing, terminal-section tests, never silently truncate. | **Phase 3 blocker** | 3 |
| 16 GB model memory/OOM | Medium until exact GPU known | High | Official 6.98 GB Q4 default; 32K context; one slot; unload STT; Q8/Q4 cache benchmark; reduce context/rechunk; partial offload/CPU; Qwen3 8B option. | Summary release blocker | 2, 3, 6 |
| GPU vendor or unsupported CUDA generation | Medium | High speed impact | Hardware probe; CUDA path only when qualified; faster-whisper CPU INT8 fallback; consider whisper.cpp Vulkan after MVP if needed. | Not functional blocker; performance blocker possible | 2, 6 |
| CUDA/cuDNN/Python packaging | High | High | App-local pinned runtime/wheels; hashes; clean VMs; no system toolkit assumption; preserve CPU profile; license review. | **Phase 6 blocker** | 2, 6 |
| New Gemma 4 / llama.cpp compatibility regressions | Medium | High | Pin a tested llama.cpp release/model commit/hash/chat template; retain previous working runtime; model startup/schema smoke tests. | **Phase 3/6 blocker** | 3, 6 |
| Antivirus/SmartScreen false positive | Medium for unsigned/new binaries | Medium/High | Authenticode sign; conventional installer; avoid one-file self-extraction tricks; submit false positives; publish hashes. | Distribution blocker if severe | 6 |
| Disk usage | High over long retention | Medium/High | Show rate/free space; 60-second chunks; thresholds/reserve; per-session size; explicit Recycle Bin deletion; never automatic source cleanup. Around 3.11 GB source for three hours at preferred formats. | Recording blocker at threshold | 1, 4, 6 |
| Model/download host failure | Medium | Medium | Pinned URLs/revisions/hashes, resumable partials, retry/repair, previous model remains active, optional documented mirror policy. | Setup blocker for first install only | 2, 3, 6 |
| Private text in logs/diagnostics | Low if tested | Critical | Structured metadata-only logging, redaction tests, user preview before sharing diagnostics, no prompt bodies. | **Privacy release blocker** | 1–7 |
| Recording-consent obligations | High that rules vary by participant location | Critical legal/reputational | No hidden/automatic recording; persistent indicator; pre-start reminder; user responsibility; product documentation; obtain legal advice for actual jurisdictions. Federal law has a party-consent exception, while states such as California can require all-party consent for confidential communications ([18 U.S.C. `2511](https://www.law.cornell.edu/uscode/text/18/2511), [California Penal Code `632](https://leginfo.legislature.ca.gov/faces/codes_displaySection.xhtml?lawCode=PEN&sectionNum=632)). | **Release-policy blocker** | 1, 6 |
| Accidental cloud disclosure | Low in MVP / Medium after API | Critical | Manual copy first; no network default; payload preview and per-run consent; transcript only; credential store; retention links and packet tests. | Phase 7 blocker | 7 |

The consent entry is not legal advice. EchoForge cannot infer which jurisdiction governs a meeting; the user must obtain the required consent before recording.

## I. Final recommendation

### Exact MVP stack

Use **C# 14/.NET 10 WPF**, **NAudio 2.3.x with two shared-mode WASAPI clients**, immutable separate **60-second PCM16 WAV chunks**, QPC/audio-clock timeline metadata, JSON/JSONL canonical storage under `%LOCALAPPDATA%\EchoForge`, and a rebuildable SQLite search index. Run a short-lived **Python 3.12 faster-whisper worker** with **Whisper large-v3-turbo FP16** by default and CPU INT8 fallback. Label microphone segments **You** and system segments **Remote**.

For the best practical local summaries on a 16 GB GPU, use a pinned **llama.cpp** child with the owner-published **`google/gemma-4-12B-it-qat-q4_0-gguf`** (6.98 GB), **32K operational context, one slot, text only, thinking off**, after fully unloading STT. Use evidence-preserving hierarchical extraction/synthesis, not a giant single prompt. Ship a self-contained x64 build with a signed **Inno Setup 7.0.2** per-user installer and a resumable, hash-verified first-run model setup.

### Exact first proof of concept

Build a console program, not a GUI. Select a real headphone render endpoint and headset microphone; record both concurrently through EchoForge's own `AudioClient` capture loop for at least ten minutes into separate 60-second PCM16 WAV chunks; capture per-packet frame count, buffer flags, device position, and QPC position; anchor the timeline on QPC and advance silence from the shared session clock when packets stop; create an aligned diagnostic mix; measure chirp offset and drift rate with the signal-based harness; kill the process after multiple chunks; restart and recover the active part. Independently decode every finalized file.

The **100 ms post-correction alignment gate at ten minutes**, the **50 ms/hour residual drift gate over a continuous 60-minute run**, and the physical durability tests are the production-qualification bar. By explicit product decision these runs are **deferred to a hardening stage** and tracked in `docs/HARDENING_BACKLOG.md` with their thresholds intact. Phase 1 proceeds on the implemented, automated-test-green capture system; it does not proceed on a claim that these gates passed.

### First five implementation tasks

1. Scaffold the solution, pinned build settings, minimal contracts, JSON session schema, and unit-test projects.
2. Implement stable WASAPI render/capture device enumeration and a console selector/format report.
3. Implement concurrent loopback/microphone capture with bounded queues, common timeline metadata, PCM16 conversion, level reporting, and 60-second chunk rotation.
4. Implement journaled atomic finalization, WAV-part repair, independent validation, and process-kill/device/silence test harnesses.
5. Run and document the ten-minute real-device matrix; fix capture, drift, Bluetooth, and recovery issues before creating the WPF application.

### Most important benchmark using real meeting audio

Create a consented corpus representative of actual use, split into a **3–5 meeting development set** for iteration and a held-out **10–20 meeting release set** for the gate, with human-verified transcript segments, decisions, actions, owners, dates, unknowns, and evidence. Run the same recordings through turbo versus large-v3 to score speech recognition, and run the **human-corrected** transcripts through **Gemma 4 12B Q4 versus Ministral 3 14B Q4_K_M** to score summarization, so neither stage is blamed for the other's errors. Score word/name accuracy, factual precision/recall, unsupported explicit claims, evidence validity, owner/date precision, coverage/readability, latency, peak VRAM, and failures. This end-to-end benchmark—not a generic leaderboard—decides whether the defaults deliver the best actual summaries.

### Most likely architectural failure

The greatest danger is treating two packet streams as if arrival time or nominal sample rate were a shared clock. That produces slowly drifting transcripts and misleading evidence playback. Phase 0 found the sharper version of this trap on real hardware: a *documented* per-packet position can belong to a different clock domain than the audio it accompanies, and using it looked plausible while being threefold wrong.

The remedy is concrete and is why EchoForge owns its capture loop: session time comes from the packet's QPC position, delivered mix-format frames describe the audio, device position stays a diagnostic until calibrated, missing time is advanced from the shared session clock, drift is measured as a rate rather than a single offset, and correction happens only in derivatives. The common monotonic timeline, one shared epoch and stop instant, explicit silence and gaps, and immutable sources are foundational.

The second-greatest danger is believing a measurement that was never taken — reading equal file lengths as alignment, or a deferred test as a passed one.

### Feature most likely to cause overengineering

Remote-speaker diarization. It invites extra GPU stacks, alignment logic, identity expectations, and UI complexity before the deterministic You/Remote split is proven. Keep it optional in Phase 5.

### Features that must remain outside the MVP

Video/OBS, virtual audio cables, live transcription, automatic or hidden recording, per-application capture, biometric voice identification, calendar/meeting bots, task-system writebacks, user/team accounts, cloud storage/sync, mobile apps, plugins, microservices, Docker, permanent local services, advanced visual design, and direct cloud APIs. Manual copy is the only cloud-adjacent MVP function.

### Ready point

This plan is ready for Claude Code **now, beginning with Phase 0 only**. It becomes ready for production-GUI implementation when the target playback device/headset passes Phase 0's ten-minute alignment and crash-recovery report. The local-summary model is architecturally selected now; its release status becomes final after Phase 3's real-meeting Gemma-versus-Ministral quality/VRAM gate.
