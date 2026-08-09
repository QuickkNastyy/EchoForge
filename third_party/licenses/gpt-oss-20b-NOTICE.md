# OpenAI gpt-oss-20b / ggml-org GGUF conversion

- Source model: https://huggingface.co/openai/gpt-oss-20b
- GGUF repository: https://huggingface.co/ggml-org/gpt-oss-20b-GGUF
- Pinned GGUF revision: `ef9b12f2ff56c69cf32153a02784e7a3c88bf524`
- License: Apache-2.0

OpenAI released gpt-oss-20b under Apache-2.0 with natively MXFP4 weights and the Harmony chat
format. The pinned GGUF is ggml-org's automated conversion of those official weights, not an
unknown community quantization. EchoForge records the conversion revision and exact MXFP4 GGUF
SHA-256 in its manifest and suppresses reasoning content from user-visible summary output.

