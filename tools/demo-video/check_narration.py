"""Listens to the narration tts.py made and flags lines that don't say what the narration file says: missing words,
and words or phrases said twice (a known slip of generative TTS). Regenerate a flagged line with TTS_ONLY=<key>.
tts.py also uses these checks to choose the best of several takes.

    pip install faster-whisper soundfile
    NARRATION=narration/<video>.json python check_narration.py <voice-dir>
"""
import json
import os
import re
import subprocess
import sys
import tempfile

import soundfile as sf

HINT = "Semantic Router, LM Studio, Claude Code, Anthropic, OpenAI, init."
DIGITS = "zero one two three four five six seven eight nine".split()
_model = None


def words(text):
    text = text.lower().replace("semanticrouter.net", "semantic router dot net").replace(".net", " dot net")
    # Numbers come back as digits (".87", "tier 4"): read them out the way the narration spells them.
    text = re.sub(r"(?<=\d)\.(?=\d)|(?<![\w])\.(?=\d)", " point ", text)
    text = re.sub(r"\d", lambda m: f" {DIGITS[int(m.group())]} ", text).replace("labeled", "labelled")
    # The voice says Claude from the spelling "Klaud" (tts.py), which speech to text writes as it hears it.
    said = re.sub(r"[^a-z' ]", " ", text).replace(" in to ", " into ").split()
    return ["claude" if w in ("klaud", "claud", "clod", "clawed") else w for w in said]


def repeats(said, max_len=6):
    """Phrases of 3+ words spoken twice in a row."""
    found = []
    for n in range(3, max_len + 1):
        for i in range(len(said) - 2 * n + 1):
            if said[i:i + n] == said[i + n:i + 2 * n]:
                found.append(" ".join(said[i:i + n]))
    return found


def transcribe(audio16k):
    """Speech to text for 16 kHz mono float32 audio."""
    global _model
    if _model is None:
        from faster_whisper import WhisperModel
        _model = WhisperModel("small.en", device="cpu", compute_type="int8")
    return " ".join(s.text.strip() for s in _model.transcribe(audio16k, initial_prompt=HINT)[0])


def check(text, heard):
    """(ok, missing words, doubled phrases, extra word count) for a line and what was heard."""
    want, said = words(text), words(heard)
    missing = [w for w in want if w not in said]
    doubled = repeats(said)
    extra = len(said) - len(want)
    return len(missing) <= 1 and not doubled and extra <= 2, missing, doubled, extra


def load16k(path):
    with tempfile.NamedTemporaryFile(suffix=".wav") as tmp:
        subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-i", path, "-ac", "1", "-ar", "16000", tmp.name], check=True)
        audio, _ = sf.read(tmp.name, dtype="float32")
    return audio


if __name__ == "__main__":
    voice = sys.argv[1]
    here = os.path.dirname(os.path.abspath(__file__))
    with open(os.environ.get("NARRATION") or os.path.join(here, "narration", "intro.json"), encoding="utf-8") as f:
        lines = json.load(f)["lines"]
    failed = 0
    for key, text in lines.items():
        heard = transcribe(load16k(os.path.join(voice, f"{key}.wav")))
        ok, missing, doubled, extra = check(text, heard)
        failed += not ok
        print(f"{'ok   ' if ok else 'CHECK'} {key:10} missing={missing} doubled={doubled} extra={extra}\n      heard: {heard}")
    sys.exit(1 if failed else 0)
