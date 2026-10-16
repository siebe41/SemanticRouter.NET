"""Times every word of a voiced narration, for captions that follow the voice word by word (the vertical cut shows them
a few words at a time). Writes <voice-dir>/words.json: {line: [[word, start, end], ...]} in seconds from the start of
the line's .wav, with the words as written in the narration file (speech-to-text only supplies the times).

    NARRATION=narration/short.json python words.py out/voice-short
"""
import difflib
import json
import os
import re
import sys

import librosa

voice = sys.argv[1]
here = os.path.dirname(os.path.abspath(__file__))
with open(os.environ.get("NARRATION") or os.path.join(here, "narration", "intro.json"), encoding="utf-8") as f:
    lines = json.load(f)["lines"]
with open(os.path.join(voice, "durations.json"), encoding="utf-8") as f:
    durations = json.load(f)

from faster_whisper import WhisperModel

model = WhisperModel("small.en", device="cpu", compute_type="int8")
norm = lambda w: re.sub(r"[^a-z0-9]", "", w.lower())
out = {}
for key, duration in durations.items():
    written = lines[key].split()
    audio, _ = librosa.load(os.path.join(voice, f"{key}.wav"), sr=16000, mono=True)
    segments, _ = model.transcribe(audio, language="en", word_timestamps=True, initial_prompt=lines[key])
    heard = [w for s in segments for w in s.words]
    times = [None] * len(written)
    matcher = difflib.SequenceMatcher(a=[norm(w) for w in written], b=[norm(w.word) for w in heard], autojunk=False)
    for a, b, size in matcher.get_matching_blocks():
        for i in range(size):
            times[a + i] = (heard[b + i].start, heard[b + i].end)
    # Words speech-to-text heard differently ("dot net" as ".NET"): spread them over the gap between their neighbours.
    i = 0
    while i < len(written):
        if times[i] is not None:
            i += 1
            continue
        j = i
        while j < len(written) and times[j] is None:
            j += 1
        start = times[i - 1][1] if i > 0 else 0.0
        end = times[j][0] if j < len(written) else duration
        step = (end - start) / (j - i)
        for n in range(i, j):
            times[n] = (start + (n - i) * step, start + (n - i + 1) * step)
        i = j
    out[key] = [[w, round(s, 3), round(e, 3)] for w, (s, e) in zip(written, times)]
    print(f"{key:12} {len(heard)} heard / {len(written)} written, {sum(t is not None for t in times)} timed")

with open(os.path.join(voice, "words.json"), "w", encoding="utf-8") as f:
    json.dump(out, f, indent=1)
