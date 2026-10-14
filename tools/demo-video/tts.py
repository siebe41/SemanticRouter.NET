"""Speaks narration.json with Kokoro (an open, offline TTS model) into <out>/<id>.wav plus <out>/durations.json.

    pip install kokoro-onnx soundfile
    # model files: https://github.com/thewh1teagle/kokoro-onnx/releases/tag/model-files-v1.0
    python tts.py <kokoro-v1.0.onnx> <voices-v1.0.bin> <out-dir> [voice]
"""
import json
import os
import sys

import soundfile as sf
from kokoro_onnx import Kokoro

model, voices, out = sys.argv[1], sys.argv[2], sys.argv[3]
here = os.path.dirname(os.path.abspath(__file__))
with open(os.path.join(here, "narration.json"), encoding="utf-8") as f:
    narration = json.load(f)
voice = sys.argv[4] if len(sys.argv) > 4 else narration["voice"]

os.makedirs(out, exist_ok=True)
kokoro = Kokoro(model, voices)
durations = {}
for key, text in narration["lines"].items():
    samples, rate = kokoro.create(text, voice=voice, speed=1.0, lang="en-us")
    sf.write(os.path.join(out, f"{key}.wav"), samples, rate)
    durations[key] = round(len(samples) / rate, 3)
    print(f"{key:10} {durations[key]:5.1f}s  {text}")

with open(os.path.join(out, "durations.json"), "w", encoding="utf-8") as f:
    json.dump(durations, f, indent=2)
