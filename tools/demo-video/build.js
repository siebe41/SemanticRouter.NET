// Turns a recording into the finished video: the frames from record.js as 30 fps H.264 at the stage's size (1920×1080,
// or 1080×1920 for the vertical stage), the narration from tts.py mixed in at the times record.js logged, and an .srt
// with the narration as subtitles (captions.js).
//   node build.js <frames-dir> <voice-dir> <out.mp4> [narration file, default narration/intro.json]
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');
const captions = require('./captions');

const [frames, voice, output, narrationFile = path.join(__dirname, 'narration', 'intro.json')] = process.argv.slice(2).map((p) => path.resolve(p));
const timeline = JSON.parse(fs.readFileSync(path.join(frames, 'timeline.json'), 'utf8'));
const lines = JSON.parse(fs.readFileSync(narrationFile, 'utf8')).lines;
const size = fs.existsSync(path.join(frames, 'stage.json')) ? JSON.parse(fs.readFileSync(path.join(frames, 'stage.json'), 'utf8')) : { width: 1920, height: 1080 };
const ffmpeg = (args) => execFileSync('ffmpeg', ['-y', '-loglevel', 'error', ...args], { stdio: 'inherit' });

// 1. Video.
const silent = output.replace(/\.mp4$/, '.silent.mp4');
// The browser sends frames only when the screen changes, so a still end card can stop short of the last line: hold the
// final frame until the narration is over.
const framesText = fs.readFileSync(path.join(frames, 'frames.txt'), 'utf8');
const shown = [...framesText.matchAll(/^duration ([\d.]+)/gm)].reduce((sum, m) => sum + Number(m[1]), 0);
const narrationEnd = Math.max(...timeline.map((n) => n.start + n.duration)) + 0.8;
const hold = Math.max(0, narrationEnd - shown).toFixed(2);
ffmpeg(['-f', 'concat', '-safe', '0', '-i', path.join(frames, 'frames.txt'),
  '-vf', `fps=30,scale=${size.width}:${size.height}:flags=lanczos,format=yuv420p,tpad=stop_mode=clone:stop_duration=${hold}`, '-c:v', 'libx264', '-preset', 'slow', '-crf', '18', silent]);

// 2. Narration: each line delayed to its start, mixed, levelled.
const inputs = timeline.flatMap((n) => ['-i', path.join(voice, `${n.key}.wav`)]);
const delays = timeline.map((n, i) => `[${i + 1}:a]adelay=${Math.round(n.start * 1000)}:all=1[a${i}]`).join(';');
const mix = `${delays};${timeline.map((_, i) => `[a${i}]`).join('')}amix=inputs=${timeline.length}:normalize=0,loudnorm=I=-16:TP=-1.5:LRA=11[voice]`;
ffmpeg(['-i', silent, ...inputs, '-filter_complex', mix, '-map', '0:v', '-map', '[voice]',
  '-c:v', 'copy', '-c:a', 'aac', '-b:a', '160k', '-ar', '48000', '-movflags', '+faststart', output]);
fs.rmSync(silent);

// 3. Subtitles: one cue per sentence, timed by the words' measured times when words.py has written them for this voice,
// otherwise by the sentence's share of the line's characters.
const wordsFile = path.join(voice, 'words.json');
const words = fs.existsSync(wordsFile) ? JSON.parse(fs.readFileSync(wordsFile, 'utf8')) : {};
fs.writeFileSync(output.replace(/\.mp4$/, '.srt'), captions.srt(captions.cues(timeline, lines, words)));
console.log(`${output}\n${output.replace(/\.mp4$/, '.srt')}`);
