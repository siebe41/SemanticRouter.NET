// Re-frames a recorded video to 9:16 (1080×1920) for Shorts, Reels and LinkedIn: a few whole-line segments of it, each
// window cropped and scaled into the middle of the frame, a headline above and large subtitles below, in the stage's
// own styling. The narration is the source's, cut only between lines.
//
//   node vertical.js <cut> <source.mp4> <out.mp4>          e.g. node vertical.js intro out/semantic-router-demo.mp4 out/semantic-router-short.mp4
//
// A cut (in cuts below) lists segments of the source: where they start and end, which of its narration lines they
// carry (with the times the line is spoken in the source), which window to show, and the headline.
const fs = require('fs');
const os = require('os');
const path = require('path');
const { execFileSync } = require('child_process');
const { chromium } = require('playwright');

const W = 1080; const H = 1920;
const TOP = 440;                    // the headline band
const terminal = { crop: [24, 24, 1010, 892], y: TOP };          // the Claude Code window of the split layout
const card = { crop: [290, 230, 1340, 640], y: TOP + 150 };      // the middle of a title or end card

const cuts = {
  // Times are in the intro as recorded (narration/intro.json, voiced into out/voice-me); re-measure after a re-record.
  intro: {
    narration: 'narration/intro.json',
    segments: [
      { from: 0, to: 12.9, window: card, lines: [['title', 0.26, 12.48]], headline: 'Stop paying frontier prices for small prompts' },
      { from: 50.5, to: 57, window: terminal, lines: [['hello', 50.81, 56.33]], headline: 'A simple question', sub: ['→ a small local model · free', 55.6] },
      { from: 63.5, to: 73.5, window: terminal, lines: [['hard', 64.99, 70.31]], headline: 'A hard debugging question', sub: ['→ Claude Opus, on your subscription', 71.8] },
      { from: 74.5, to: 85.5, window: terminal, lines: [['limit', 75.73, 83.27]], headline: 'Claude hits its session limit', sub: ['→ the local coder takes over', 81.8] },
      { from: 99.1, to: 109.8, window: card, lines: [['end', 99.38, 109.14]], headline: '' },
    ],
  },
};

const [cutName, source, output] = process.argv.slice(2);
const cut = cuts[cutName];
if (!cut || !source || !output) {
  console.error(`node vertical.js <${Object.keys(cuts).join('|')}> <source.mp4> <out.mp4>`);
  process.exit(2);
}
const lines = JSON.parse(fs.readFileSync(path.join(__dirname, cut.narration), 'utf8')).lines;
const spoken = (text) => text.replace(/Semantic Router dot net/g, 'SemanticRouter.NET').replace(/dot net/g, '.NET');
const sentences = (text) => spoken(text).match(/.+?(?:[.!?:](?=\s|$)|$)/g).map((s) => s.trim()).filter(Boolean);
const ffmpeg = (args) => execFileSync('ffmpeg', ['-y', '-loglevel', 'error', ...args], { stdio: 'inherit' });

const page = (body) => `<!doctype html><html><head><meta charset="utf-8"><style>
  html, body { margin: 0; width: ${W}px; height: ${H}px; background: transparent; font-family: "Inter", system-ui, sans-serif; color: #e8edf0; }
  .bg { position: absolute; inset: 0; background: radial-gradient(900px 1200px at 50% 40%, #15222a 0%, #0c1013 75%); }
  .brand { position: absolute; top: 120px; width: 100%; text-align: center; font-size: 34px; font-weight: 600; letter-spacing: -0.01em; }
  .brand b { color: #2bb0cc; font-weight: 600; }
  .head { position: absolute; top: 200px; left: 70px; right: 70px; text-align: center; font-size: 58px; line-height: 1.12; font-weight: 700; letter-spacing: -0.02em; }
  .sub { position: absolute; top: 1415px; left: 60px; right: 60px; text-align: center; font-family: "DejaVu Sans Mono", monospace; font-size: 34px; color: #e3a24f; }
  .say { position: absolute; top: 1520px; left: 70px; right: 70px; text-align: center; font-size: 50px; line-height: 1.22; font-weight: 600; }
</style></head><body>${body}</body></html>`;

(async () => {
  const work = fs.mkdtempSync(path.join(os.tmpdir(), 'vertical-'));
  const browser = await chromium.launch();
  const tab = await browser.newPage({ viewport: { width: W, height: H } });
  const render = async (name, body, transparent = true) => {
    await tab.setContent(page(body));
    await tab.evaluate(() => document.fonts.ready);
    const file = path.join(work, `${name}.png`);
    await tab.screenshot({ path: file, omitBackground: transparent });
    return file;
  };

  const background = await render('bg', '<div class="bg"></div>', false);
  const parts = [];
  for (const [i, seg] of cut.segments.entries()) {
    const length = seg.to - seg.from;
    // Overlays, each shown over a window of the segment: the headline, its result line, one subtitle per sentence.
    const overlays = [[await render(`h${i}`, `<div class="brand">SemanticRouter<b>.NET</b></div>${seg.headline ? `<div class="head">${seg.headline}</div>` : ''}`), 0, length]];
    if (seg.sub) overlays.push([await render(`s${i}`, `<div class="sub">${seg.sub[0]}</div>`), seg.sub[1] - seg.from, length]);
    for (const [key, start, end] of seg.lines) {
      const said = sentences(lines[key]);
      const total = said.reduce((sum, s) => sum + s.length, 0);
      let at = start;
      for (const [j, s] of said.entries()) {
        const until = at + ((end - start) * s.length) / total;
        overlays.push([await render(`t${i}-${key}-${j}`, `<div class="say">${s}</div>`), at - seg.from, (j === said.length - 1 ? Math.min(seg.to, end + 0.4) : until) - seg.from]);
        at = until;
      }
    }

    const [cx, cy, cw, ch] = seg.window.crop;
    const scaledH = Math.round((W * ch) / cw / 2) * 2;
    // A card's own background differs a little from this one: feather the window's top and bottom edges into it.
    if (seg.window === card) {
      const y = seg.window.y; const f = 90;
      overlays.unshift([await render(`f${i}`, `<div class="bg" style="-webkit-mask-image: linear-gradient(to bottom, #000 ${y}px, transparent ${y + f}px, transparent ${y + scaledH - f}px, #000 ${y + scaledH}px)"></div>`), 0, length]);
    }
    const inputs = ['-loop', '1', '-t', length.toFixed(3), '-i', background, '-ss', String(seg.from), '-t', length.toFixed(3), '-i', source];
    overlays.forEach(([file]) => inputs.push('-loop', '1', '-t', length.toFixed(3), '-i', file));
    let graph = `[1:v]crop=${cw}:${ch}:${cx}:${cy},scale=${W}:${scaledH}:flags=lanczos,setsar=1[win];[0:v][win]overlay=0:${seg.window.y}[v0]`;
    overlays.forEach(([, from, to], k) => {
      graph += `;[v${k}][${k + 2}:v]overlay=0:0:enable='between(t,${Math.max(0, from).toFixed(3)},${to.toFixed(3)})'[v${k + 1}]`;
    });
    graph += `;[v${overlays.length}]fps=30,format=yuv420p[out];[1:a]afade=t=in:d=0.08,afade=t=out:st=${(length - 0.15).toFixed(3)}:d=0.15[aud]`;
    const part = path.join(work, `part${i}.mp4`);
    ffmpeg([...inputs, '-filter_complex', graph, '-map', '[out]', '-map', '[aud]', '-t', length.toFixed(3),
      '-c:v', 'libx264', '-preset', 'slow', '-crf', '19', '-c:a', 'aac', '-b:a', '160k', '-ar', '48000', part]);
    parts.push(part);
  }
  await browser.close();

  const list = path.join(work, 'parts.txt');
  fs.writeFileSync(list, parts.map((p) => `file '${p}'`).join('\n') + '\n');
  ffmpeg(['-f', 'concat', '-safe', '0', '-i', list, '-c', 'copy', '-movflags', '+faststart', output]);
  fs.rmSync(work, { recursive: true, force: true });
  console.log(output);
})().catch((err) => { console.error(err); process.exit(1); });
