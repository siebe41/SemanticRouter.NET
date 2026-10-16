"""Speaks a narration file (NARRATION, default narration/intro.json) into <out>/<id>.wav plus <out>/durations.json,
with one of two open TTS models that run locally.

    # Chatterbox (natural cadence; clones a voice from a 10-30 s sample):
    pip install chatterbox-tts faster-whisper librosa
    python tts.py chatterbox <out-dir> [voice-sample.wav]

    # Kokoro (small and fast, a fixed set of voices):
    pip install kokoro-onnx soundfile   # model files: github.com/thewh1teagle/kokoro-onnx/releases/tag/model-files-v1.0
    python tts.py kokoro <out-dir> <kokoro-v1.0.onnx> <voices-v1.0.bin> [voice]

Chatterbox speaks each line in one pass, so the intonation carries from sentence to sentence, and makes
up to TTS_CANDIDATES takes (default 3), stopping at the first clean one. The take that is kept says every word
(checked by speech-to-text), has the fewest breath or click artifacts and no creaky start, and sounds most like the
voice sample. TTS_ONLY=key,key redoes lines.
"""
import json
import os
import re
import sys
import zlib

import numpy as np
import soundfile as sf

engine, out = sys.argv[1], sys.argv[2]
here = os.path.dirname(os.path.abspath(__file__))
# NARRATION: the video's narration file (default narration/intro.json).
with open(os.environ.get("NARRATION") or os.path.join(here, "narration", "intro.json"), encoding="utf-8") as f:
    narration = json.load(f)


def sentences(text):
    return [s.strip() for s in re.findall(r"[^.?!:]+[.?!:]?", text) if s.strip()]


# How the voice should say a word, where the model gets it wrong: "Claude" comes out as "Clahd" (like "clod"); spelled
# "Klaud" it gets the rounded "aw". Only what is spoken changes; the narration file, the checks and the subtitles keep
# the real word.
SAY = {"Claude": "Klaud"}


def spoken(text):
    for word, sound in SAY.items():
        text = re.sub(rf"\b{word}\b", sound, text)
    return text


def chunks(text, limit=280):
    """The line in as few pieces as possible: whole, or split at sentence ends when it is too long for one pass."""
    out, current = [], ""
    for s in sentences(text):
        if current and len(current) + len(s) + 1 > limit:
            out.append(current)
            current = s
        else:
            current = f"{current} {s}".strip()
    return out + [current] if current else out


if engine == "chatterbox":
    import librosa
    import torch
    from chatterbox.tts import ChatterboxTTS

    import check_narration

    torch.set_num_threads(os.cpu_count() or 4)
    model = ChatterboxTTS.from_pretrained(device="cuda" if torch.cuda.is_available() else "cpu")
    sample = sys.argv[3] if len(sys.argv) > 3 else None
    rate = model.sr
    candidates = int(os.environ.get("TTS_CANDIDATES", "3"))
    reference = None
    if sample:
        ref_audio, _ = librosa.load(sample, sr=16000)
        reference = model.ve.embeds_from_wavs([ref_audio], sample_rate=16000)[0]

    def generate(text, seed):
        torch.manual_seed(seed)
        # exaggeration: expressiveness (0.5 is neutral); cfg_weight: lower is slower and more deliberate.
        wav = model.generate(text, audio_prompt_path=sample, exaggeration=0.5, cfg_weight=0.45)
        return wav.squeeze(0).cpu().numpy()

    def voicing(audio):
        a16 = librosa.resample(audio, orig_sr=rate, target_sr=16000)
        f0, voiced, _ = librosa.pyin(a16, fmin=60, fmax=400, sr=16000, frame_length=1024, hop_length=160)
        loud = librosa.feature.rms(y=a16, frame_length=1024, hop_length=160)[0]
        return a16, f0, voiced, loud

    def trim(audio):
        """Cuts the breaths and clicks before the speech and after it, with short fades. Speech runs from the first
        voiced sound to the last, widened to the unvoiced consonants that touch it (the s of "tools", the t of "it"),
        which voicing alone would clip."""
        _, _, voiced, loud = voicing(audio)
        idx = np.where(voiced)[0]
        if not len(idx):
            return audio
        quiet = 0.1 * np.median(loud[idx])

        def extend(i, step):  # through sound louder than quiet, across gaps up to 80 ms (the hold before a t or k)
            gap = 0
            while 0 <= i + step < len(loud) and gap < 8:
                i += step
                gap = 0 if loud[i] > quiet else gap + 1
            return i - step * gap

        start = max(0, int((extend(idx[0], -1) * 0.01 - 0.05) * rate))
        end = min(len(audio), int((extend(idx[-1], 1) * 0.01 + 0.2) * rate))
        audio = audio[start:end].copy()
        fade = int(0.02 * rate)
        audio[:fade] *= np.linspace(0, 1, fade)
        audio[-fade:] *= np.linspace(1, 0, fade)
        return audio

    def score(audio, text):
        """Lower is better; a take that drops or repeats words is never chosen over one that doesn't."""
        a16, f0, voiced, loud = voicing(audio)
        ok, missing, doubled, extra = check_narration.check(text, check_narration.transcribe(a16.astype(np.float32)))
        bursts, run = 0, 0
        for v, l in zip(voiced, loud):  # loud, unvoiced stretches of 200 ms or more: breaths, clicks, hiss
            run = run + 1 if (not v and l > 0.06) else 0
            bursts += run == 20
        median = np.nanmedian(f0)
        creak = float(np.nanmean(f0[:100] < median * 0.88)) if np.isfinite(median) else 1.0
        similarity = 0.0
        if reference is not None:
            e = model.ve.embeds_from_wavs([a16], sample_rate=16000)[0]
            similarity = float(np.dot(reference, e) / np.linalg.norm(reference) / np.linalg.norm(e))
        total = (0 if ok else 100) + 3 * len(missing) + 2 * bursts + 15 * creak - 20 * similarity
        return total, dict(ok=ok, missing=missing, doubled=doubled, bursts=bursts, creak=round(creak, 2), similarity=round(similarity, 3))

    def speak(key, text):
        best = None
        for i in range(candidates):
            seed = zlib.crc32(key.encode()) % 100_000 * 10 + i + 1000 * int(os.environ.get("TTS_SEED", "0"))
            parts = [trim(generate(spoken(c), seed)) for c in chunks(text)]
            audio = np.concatenate([np.concatenate([p, np.zeros(int(0.35 * rate), dtype=np.float32)]) for p in parts])[: -int(0.35 * rate)]
            s, info = score(audio, text)
            print(f"    take {i + 1}: score {s:6.2f} {info}", flush=True)
            if best is None or s < best[0]:
                best = (s, audio)
            # A clean take needs no competition: every word, no artifacts, no creak, close to the sample.
            if info["ok"] and not info["missing"] and info["bursts"] == 0 and info["creak"] < 0.15 and (reference is None or info["similarity"] >= 0.93):
                break
        return best[1]
else:
    from kokoro_onnx import Kokoro

    kokoro = Kokoro(sys.argv[3], sys.argv[4])
    voice = sys.argv[5] if len(sys.argv) > 5 else narration.get("voice", "af_heart")
    rate = 24000

    def speak(key, text):
        parts = []
        for s in sentences(text):
            samples, _ = kokoro.create(spoken(s), voice=voice, speed=1.0, lang="en-us")
            parts += [samples, np.zeros(int(0.35 * rate), dtype=np.float32)]
        return np.concatenate(parts[:-1])


os.makedirs(out, exist_ok=True)
durations = {}
only = set(os.environ.get("TTS_ONLY", "").split(",")) - {""}
for key, text in narration["lines"].items():
    if only and key not in only:
        continue
    audio = speak(key, text).astype(np.float32)
    sf.write(os.path.join(out, f"{key}.wav"), audio, rate)
    durations[key] = round(len(audio) / rate, 3)
    print(f"{key:10} {durations[key]:5.1f}s  {text}", flush=True)

path = os.path.join(out, "durations.json")
if only and os.path.exists(path):
    with open(path, encoding="utf-8") as f:
        durations = {**json.load(f), **durations}
with open(path, "w", encoding="utf-8") as f:
    json.dump(durations, f, indent=2)
