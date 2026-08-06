---
license: mit
---

# Whisper large-v3 turbo model for CTranslate2

This repository contains the conversion of [openai/whisper-large-v3-turbo](https://huggingface.co/openai/whisper-large-v3-turbo) to the [CTranslate2](https://github.com/OpenNMT/CTranslate2) model format.

This model can be used in CTranslate2 or projects based on CTranslate2 models such as [faster-whisper](https://github.com/systran/faster-whisper). It is called automatically for [Mobius Labs fork of faster-whisper](https://github.com/mobiusml/faster-whisper).

## Example


```python
from faster_whisper import WhisperModel

model = WhisperModel("dropbox-dash/faster-whisper-large-v3-turbo")

segments, info = model.transcribe("audio.mp3")
for segment in segments:
    print("[%.2fs -> %.2fs] %s" % (segment.start, segment.end, segment.text))
```
Note that the model weights are saved in FP16. This type can be changed when the model is loaded using the [compute_type option in CTranslate2](https://opennmt.net/CTranslate2/quantization.html).


## Conversion details
The openAI model was converted with the following command:
```
ct2-transformers-converter --model openai/whisper-large-v3-turbo --output_dir faster-whisper-large-v3-turbo \
    --copy_files tokenizer.json preprocessor_config.json --quantization float16
```

### More Information

For more information about the original model, see its [model card](https://huggingface.co/openai/whisper-large-v3-turbo).
