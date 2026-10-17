// The narration as text: the subtitles build.js writes, and the transcripts on the documentation site's videos page
// (site/videos.data.js). No dependencies, so the site can load it too.

/**
 * A narration line as written. The narration files spell some words the way the voice should say them ("Semantic Router
 * dot net", "L M Studio"); this puts them back.
 */
const written = (text) => text.replace(/Semantic Router dot net/g, 'SemanticRouter.NET').replace(/dot net/g, '.NET')
  .replace(/in-it/g, 'init').replace(/L M Studio/g, 'LM Studio').replace(/C L I/g, 'CLI');

/** A line's sentences, each ending at . ! ? or : before a space or the end. */
const sentences = (text) => text.match(/.+?(?:[.!?:](?=\s|$)|$)/g).map((s) => s.trim()).filter(Boolean);

/**
 * One cue per sentence: [start, end, text] in seconds from the start of the video.
 *   timeline  [{ key, start, duration }] from record.js: when each narration line starts, and how long it is
 *   lines     the narration file's lines, by key
 *   words     optional: words.py's words.json, { key: [[word, start, end], ...] } in seconds from the start of the line
 * A line with measured word times gets each sentence from its first word's start to the next sentence's start (or its
 * own last word's end); any other line is split by each sentence's share of its characters.
 */
function cues(timeline, lines, words = {}) {
  const result = [];
  for (const n of timeline) {
    const measured = words[n.key];
    const said = lines[n.key].split(/\s+/).filter(Boolean);
    // Times for a line whose text has changed since words.py ran would put the wrong words on screen: estimate those.
    if (measured && measured.length === said.length && measured.every(([w], i) => w === said[i])) {
      const groups = [];
      let group = [];
      for (const word of measured) {
        group.push(word);
        if (/[.!?:]$/.test(word[0])) {
          groups.push(group);
          group = [];
        }
      }
      if (group.length) groups.push(group);
      groups.forEach((g, i) => {
        const end = i + 1 < groups.length ? groups[i + 1][0][1] : g[g.length - 1][2];
        result.push([n.start + g[0][1], n.start + end, written(g.map(([w]) => w).join(' '))]);
      });
      continue;
    }
    const parts = sentences(written(lines[n.key]));
    const total = parts.reduce((sum, s) => sum + s.length, 0);
    let at = n.start;
    for (const s of parts) {
      const length = (n.duration * s.length) / total;
      result.push([at, at + length, s]);
      at += length;
    }
  }
  return result;
}

/** Seconds as an .srt time, 00:01:02,345. */
function stamp(s) {
  const ms = Math.round(s * 1000);
  const pad = (n, w = 2) => String(n).padStart(w, '0');
  return `${pad(Math.floor(ms / 3600000))}:${pad(Math.floor(ms / 60000) % 60)}:${pad(Math.floor(ms / 1000) % 60)},${pad(ms % 1000, 3)}`;
}

/** Cues as an .srt file. */
const srt = (list) => list.map(([start, end, text], i) => `${i + 1}\n${stamp(start)} --> ${stamp(end)}\n${text}\n`).join('\n');

module.exports = { written, sentences, cues, stamp, srt };
