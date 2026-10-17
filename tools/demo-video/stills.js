// Stills of a take: for every narration line, the frame on screen as its beat ends (just before the next line, which
// the script starts once the beat's clicks and typing are done, or as the last line ends), plus
// index.html, a contact sheet with each still next to its line. Use it to see a script against the current product
// before re-recording, or to update the screenshots in the docs, without building the video.
//   node stills.js <frames-dir> <out-dir> [narration file]     e.g. node stills.js out/frames out/stills narration/intro.json
const fs = require('fs');
const path = require('path');
const { written } = require('./captions');

/** Frames as frames.txt lists them: [{ file, start }] in seconds from the first frame. */
function frameTimes(framesText) {
  const result = [];
  let t = 0;
  for (const [, file, duration] of framesText.matchAll(/^file '([^']+)'\nduration ([\d.]+)/gm)) {
    result.push({ file, start: t });
    t += Number(duration);
  }
  return result;
}

/** The frame on screen at time t: the last one that started at or before it. */
const frameAt = (frames, t) => frames.findLast((f) => f.start <= t) || frames[0];

/** For each narration line in the timeline, the frame shown just before its beat ends: [{ key, file, at }]. */
const pick = (frames, timeline, before = 0.3) => timeline.map(({ key, start, duration }, i) => {
  const end = Math.max(start + duration, timeline[i + 1]?.start ?? 0);
  const at = +Math.max(start, end - before).toFixed(3);
  return { key, at, file: frameAt(frames, at).file };
});

if (require.main === module) {
  const [framesDir, out, narrationFile] = process.argv.slice(2).map((p) => p && path.resolve(p));
  if (!framesDir || !out) {
    console.error('node stills.js <frames-dir> <out-dir> [narration file]');
    process.exit(2);
  }
  const frames = frameTimes(fs.readFileSync(path.join(framesDir, 'frames.txt'), 'utf8'));
  const timeline = JSON.parse(fs.readFileSync(path.join(framesDir, 'timeline.json'), 'utf8'));
  const lines = narrationFile ? JSON.parse(fs.readFileSync(narrationFile, 'utf8')).lines : {};
  fs.rmSync(out, { recursive: true, force: true });
  fs.mkdirSync(out, { recursive: true });
  const esc = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;');
  const rows = pick(frames, timeline).map(({ key, at, file }, i) => {
    const name = `${String(i + 1).padStart(2, '0')}-${key}${path.extname(file)}`;
    fs.copyFileSync(path.join(framesDir, file), path.join(out, name));
    const time = `${Math.floor(at / 60)}:${String(Math.floor(at % 60)).padStart(2, '0')}`;
    return `<figure><img src="${name}" alt="${esc(key)}" loading="lazy"><figcaption><b>${time} · ${esc(key)}</b> ${lines[key] ? esc(written(lines[key])) : ''}</figcaption></figure>`;
  });
  fs.writeFileSync(path.join(out, 'index.html'), `<!doctype html><meta charset="utf-8"><title>Stills</title>
<style>body{font:14px system-ui,sans-serif;margin:16px;background:#fff;color:#111}figure{margin:0 0 28px}img{max-width:100%;border:1px solid #ccc}figcaption{margin-top:6px;max-width:100ch}</style>
${rows.join('\n')}\n`);
  console.log(`${rows.length} stills in ${out}`);
}

module.exports = { frameTimes, frameAt, pick };
