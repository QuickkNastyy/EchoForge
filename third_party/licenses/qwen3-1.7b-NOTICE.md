# Qwen3 1.7B tokenizer/configuration sidecars

- Official repository: https://huggingface.co/Qwen/Qwen3-1.7B
- Pinned revision: `70d244cc86ccca08cf5af4e1e306ecf908b1ad5e`
- License: Apache-2.0
- License text: https://www.apache.org/licenses/LICENSE-2.0

Canary-Qwen 2.5B's official configuration names Qwen/Qwen3-1.7B as its language-model
architecture and tokenizer source. EchoForge does not install Qwen3's base model weights for
Canary: the final NVIDIA safetensors checkpoint already carries the trained weights and NeMo
loads it with `pretrained_weights=false`. The manifest pins only the exact Qwen configuration,
generation configuration, tokenizer, vocabulary, and merge files needed to construct that final
checkpoint fully offline.
