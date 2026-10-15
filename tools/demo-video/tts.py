"""Speaks a narration file (NARRATION, default narration/intro.json) into <out>/<id>.wav plus <out>/durations.json, with one of two open TTS models that run locally.

    # Chatterbox (natural cadence; clones a voice from a 10-30 s sample):
    pip install chatterbox-tts
    python tts.py chatterbox <out-dir> [voice-sample.wav]

    # Kokoro (small and fast, a fixed set of voices):
    pip install kokoro-onnx soundfile   # model files: github.com/thewh1teagle/kokoro-onnx/releases/tag/model-files-v1.0
    python tts.py kokoro <out-dir> <kokoro-v1.0.onnx> <voices-v1.0.bin> [voice]

Each line is spoken sentence by sentence and joined with short pauses, which keeps the pacing even.
"""
import json
import os
import re
import sys

import numpy as np
import soundfile as sf

engine, out = sys.argv[1], sys.argv[2]
here = os.path.dirname(os.path.abspath(__file__))
# NARRATION: the video's narration file (default narration/intro.json).
with open(os.environ.get("NARRATION") or os.path.join(here, "narration", "intro.json"), encoding="utf-8") as f:
    narration = json.load(f)

PAUSE_AFTER = {".": 0.38, "?": 0.42, "!": 0.38, ":": 0.3}


def sentences(text):
    return [s.strip() for s in re.findall(r"[^.?!:]+[.?!:]?", text) if s.strip()]


if engine == "chatterbox":
    import torch
    from chatterbox.tts import ChatterboxTTS

    torch.set_num_threads(os.cpu_count() or 4)
    model = ChatterboxTTS.from_pretrained(device="cuda" if torch.cuda.is_available() else "cpu")
    sample = sys.argv[3] if len(sys.argv) > 3 else None
    rate = model.sr

    def speak(sentence):
        # exaggeration: expressiveness (0.5 is neutral); cfg_weight: lower is slower and more deliberate.
        wav = model.generate(sentence, audio_prompt_path=sample, exaggeration=0.55, cfg_weight=0.4)
        return wav.squeeze(0).cpu().numpy()
else:
    from kokoro_onnx import Kokoro

    kokoro = Kokoro(sys.argv[3], sys.argv[4])
    voice = sys.argv[5] if len(sys.argv) > 5 else narration.get("voice", "af_heart")
    rate = 24000

    def speak(sentence):
        samples, _ = kokoro.create(sentence, voice=voice, speed=1.0, lang="en-us")
        return samples


def trim(audio, threshold=0.01):
    """Drops leading and trailing near-silence, so the pauses between sentences are the ones chosen here."""
    loud = np.where(np.abs(audio) > threshold)[0]
    return audio[max(0, loud[0] - int(0.03 * rate)): loud[-1] + int(0.08 * rate)] if len(loud) else audio


os.makedirs(out, exist_ok=True)
durations = {}
only = set(os.environ.get("TTS_ONLY", "").split(",")) - {""}
for key, text in narration["lines"].items():
    if only and key not in only:
        continue
    parts = []
    for sentence in sentences(text):
        parts.append(trim(speak(sentence)))
        parts.append(np.zeros(int(PAUSE_AFTER.get(sentence[-1], 0.3) * rate), dtype=np.float32))
    audio = np.concatenate(parts[:-1]).astype(np.float32)
    sf.write(os.path.join(out, f"{key}.wav"), audio, rate)
    durations[key] = round(len(audio) / rate, 3)
    print(f"{key:10} {durations[key]:5.1f}s  {text}", flush=True)

path = os.path.join(out, "durations.json")
if only and os.path.exists(path):
    with open(path, encoding="utf-8") as f:
        durations = {**json.load(f), **durations}
with open(path, "w", encoding="utf-8") as f:
    json.dump(durations, f, indent=2)
