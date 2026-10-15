"""Listens to the narration tts.py made and flags lines that don't say what narration.json says: missing words, and
words or phrases said twice (a known slip of generative TTS). Regenerate a flagged line with TTS_ONLY=<key>.

    pip install faster-whisper soundfile
    python check_narration.py <voice-dir>
"""
import json
import os
import re
import subprocess
import sys
import tempfile

import soundfile as sf
from faster_whisper import WhisperModel

voice = sys.argv[1]
here = os.path.dirname(os.path.abspath(__file__))
with open(os.path.join(here, "narration.json"), encoding="utf-8") as f:
    lines = json.load(f)["lines"]

model = WhisperModel("small.en", device="cpu", compute_type="int8")
hint = "Semantic Router, LM Studio, Claude Code, Anthropic, OpenAI, init."


def words(text):
    text = text.lower().replace("semanticrouter.net", "semantic router dot net").replace(".net", " dot net")
    return re.sub(r"[^a-z' ]", " ", text).replace(" in to ", " into ").split()


def repeats(said, max_len=6):
    """Phrases of 3+ words spoken twice in a row."""
    found = []
    for n in range(3, max_len + 1):
        for i in range(len(said) - 2 * n + 1):
            if said[i:i + n] == said[i + n:i + 2 * n]:
                found.append(" ".join(said[i:i + n]))
    return found


failed = 0
for key, text in lines.items():
    with tempfile.NamedTemporaryFile(suffix=".wav") as tmp:
        subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-i", os.path.join(voice, f"{key}.wav"),
                        "-ac", "1", "-ar", "16000", tmp.name], check=True)
        audio, _ = sf.read(tmp.name, dtype="float32")
    heard = " ".join(s.text.strip() for s in model.transcribe(audio, initial_prompt=hint)[0])
    want, said = words(text), words(heard)
    missing = [w for w in want if w not in said]
    doubled = repeats(said)
    extra = len(said) - len(want)
    ok = len(missing) <= 1 and not doubled and extra <= 2
    failed += not ok
    print(f"{'ok   ' if ok else 'CHECK'} {key:10} missing={missing} doubled={doubled} extra={extra}\n      heard: {heard}")

sys.exit(1 if failed else 0)
