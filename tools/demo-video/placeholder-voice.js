// A stand-in for tts.py: a silent .wav per narration line, as long as the line takes to say at an ordinary pace,
// durations.json, and words.json with the words spread evenly over each line (for the vertical cut). With it a take
// runs without generating speech, to check a script against the current product and to take stills of every beat
// (stills.js) before the real narration is made.
//   node placeholder-voice.js <narration file> <voice-dir>     e.g. node placeholder-voice.js narration/intro.json out/voice-draft
const fs = require('fs');
const path = require('path');

const RATE = 24000; // what tts.py writes
const WORDS_PER_SECOND = 2.6; // about 155 words a minute, the pace of the recorded narration

/** Seconds a line takes to say: its words at the narration's pace, plus a little for each sentence's pause. */
const seconds = (text) => {
  const words = text.split(/\s+/).filter(Boolean).length;
  const pauses = (text.match(/[.!?:](?=\s|$)/g) || []).length;
  return +(words / WORDS_PER_SECOND + pauses * 0.25).toFixed(2);
};

/** [[word, start, end], …] as words.py writes them, but spread evenly over the line. */
const evenWords = (text, duration) => {
  const words = text.split(/\s+/).filter(Boolean);
  const each = duration / words.length;
  return words.map((w, i) => [w, +(i * each).toFixed(2), +((i + 1) * each).toFixed(2)]);
};

/** A silent 16-bit mono WAV of that length. */
function silence(duration) {
  const samples = Math.round(duration * RATE);
  const header = Buffer.alloc(44);
  header.write('RIFF', 0); header.writeUInt32LE(36 + samples * 2, 4); header.write('WAVE', 8);
  header.write('fmt ', 12); header.writeUInt32LE(16, 16); header.writeUInt16LE(1, 20); header.writeUInt16LE(1, 22);
  header.writeUInt32LE(RATE, 24); header.writeUInt32LE(RATE * 2, 28); header.writeUInt16LE(2, 32); header.writeUInt16LE(16, 34);
  header.write('data', 36); header.writeUInt32LE(samples * 2, 40);
  return Buffer.concat([header, Buffer.alloc(samples * 2)]);
}

if (require.main === module) {
  const [narrationFile, voice] = process.argv.slice(2);
  if (!narrationFile || !voice) {
    console.error('node placeholder-voice.js <narration file> <voice-dir>');
    process.exit(2);
  }
  const { lines } = JSON.parse(fs.readFileSync(narrationFile, 'utf8'));
  fs.mkdirSync(voice, { recursive: true });
  const durations = {};
  const words = {};
  for (const [key, text] of Object.entries(lines)) {
    durations[key] = seconds(text);
    words[key] = evenWords(text, durations[key]);
    fs.writeFileSync(path.join(voice, `${key}.wav`), silence(durations[key]));
  }
  fs.writeFileSync(path.join(voice, 'durations.json'), JSON.stringify(durations, null, 2));
  fs.writeFileSync(path.join(voice, 'words.json'), JSON.stringify(words));
  const total = Object.values(durations).reduce((a, b) => a + b, 0);
  console.log(`${Object.keys(durations).length} placeholder lines in ${voice}, ${total.toFixed(0)} s of narration`);
}

module.exports = { seconds, evenWords, silence };
