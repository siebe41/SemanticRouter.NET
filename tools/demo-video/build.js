// Turns a recording into the finished video: the frames from record.js as 1080p30 H.264, the narration from tts.py
// mixed in at the times record.js logged, and an .srt with the narration as subtitles.
//   node build.js <frames-dir> <voice-dir> <out.mp4> [narration file, default narration/intro.json]
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');

const [frames, voice, output, narrationFile = path.join(__dirname, 'narration', 'intro.json')] = process.argv.slice(2).map((p) => path.resolve(p));
const timeline = JSON.parse(fs.readFileSync(path.join(frames, 'timeline.json'), 'utf8'));
const lines = JSON.parse(fs.readFileSync(narrationFile, 'utf8')).lines;
const ffmpeg = (args) => execFileSync('ffmpeg', ['-y', '-loglevel', 'error', ...args], { stdio: 'inherit' });

// 1. Video.
const silent = output.replace(/\.mp4$/, '.silent.mp4');
ffmpeg(['-f', 'concat', '-safe', '0', '-i', path.join(frames, 'frames.txt'),
  '-vf', 'fps=30,scale=1920:1080:flags=lanczos,format=yuv420p', '-c:v', 'libx264', '-preset', 'slow', '-crf', '18', silent]);

// 2. Narration: each line delayed to its start, mixed, levelled.
const inputs = timeline.flatMap((n) => ['-i', path.join(voice, `${n.key}.wav`)]);
const delays = timeline.map((n, i) => `[${i + 1}:a]adelay=${Math.round(n.start * 1000)}:all=1[a${i}]`).join(';');
const mix = `${delays};${timeline.map((_, i) => `[a${i}]`).join('')}amix=inputs=${timeline.length}:normalize=0,loudnorm=I=-16:TP=-1.5:LRA=11[voice]`;
ffmpeg(['-i', silent, ...inputs, '-filter_complex', mix, '-map', '0:v', '-map', '[voice]',
  '-c:v', 'copy', '-c:a', 'aac', '-b:a', '160k', '-ar', '48000', '-movflags', '+faststart', output]);
fs.rmSync(silent);

// 3. Subtitles: one cue per sentence, timed by its share of the line's characters.
const stamp = (s) => {
  const ms = Math.round(s * 1000);
  const pad = (n, w = 2) => String(n).padStart(w, '0');
  return `${pad(Math.floor(ms / 3600000))}:${pad(Math.floor(ms / 60000) % 60)}:${pad(Math.floor(ms / 1000) % 60)},${pad(ms % 1000, 3)}`;
};
const spoken = (text) => text.replace(/Semantic Router dot net/g, 'SemanticRouter.NET').replace(/dot net/g, '.NET')
  .replace(/in-it/g, 'init').replace(/L M Studio/g, 'LM Studio').replace(/C L I/g, 'CLI');
const cues = [];
for (const n of timeline) {
  const sentences = spoken(lines[n.key]).match(/.+?(?:[.!?:](?=\s|$)|$)/g).map((s) => s.trim()).filter(Boolean);
  const total = sentences.reduce((sum, s) => sum + s.length, 0);
  let at = n.start;
  for (const s of sentences) {
    const length = (n.duration * s.length) / total;
    cues.push(`${cues.length + 1}\n${stamp(at)} --> ${stamp(at + length)}\n${s}\n`);
    at += length;
  }
}
fs.writeFileSync(output.replace(/\.mp4$/, '.srt'), cues.join('\n'));
console.log(`${output}\n${output.replace(/\.mp4$/, '.srt')}`);
