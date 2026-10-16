// Prompt caching for the stand-in models, for the savings video (DEMO_FULL_ANSWERS): like the real APIs, the part of a
// request that repeats the start of a recent one (the system prompt, the tools, the conversation so far) is read from
// the cache, and the rest is new. Agentic coding resends the whole conversation on every step, so most of its input is
// cache reads, billed at a fraction of the input price.
const fs = require('fs');
const os = require('os');
const path = require('path');

const KEEP = 12;

function common(a, b) {
  const n = Math.min(a.length, b.length);
  let i = 0;
  while (i < n && a.charCodeAt(i) === b.charCodeAt(i)) i++;
  return i;
}

/** Characters at the start of text already seen at the start of one of the recent requests in store (an array). */
function cachedPrefix(store, text) {
  const hit = store.reduce((best, seen) => Math.max(best, common(seen, text)), 0);
  store.unshift(text);
  store.length = Math.min(store.length, KEEP);
  return hit;
}

/** The same, for processes that live for one request (the fake claude CLI): the recent requests are kept on disk. */
function cachedPrefixOnDisk(text) {
  const dir = path.join(os.tmpdir(), 'semantic-router-demo-cache');
  fs.mkdirSync(dir, { recursive: true });
  const files = fs.readdirSync(dir).sort().reverse();
  const store = files.map((f) => fs.readFileSync(path.join(dir, f), 'utf8'));
  const hit = cachedPrefix(store, text);
  fs.writeFileSync(path.join(dir, `${Date.now()}-${process.pid}.txt`), text);
  for (const f of files.slice(KEEP - 1)) fs.rmSync(path.join(dir, f), { force: true });
  return hit;
}

module.exports = { cachedPrefix, cachedPrefixOnDisk };
