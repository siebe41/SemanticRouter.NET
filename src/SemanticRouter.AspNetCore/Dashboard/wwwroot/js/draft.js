/* Semantic Router dashboard: Keeping the draft safe: rebasing it onto a newer configuration, counting its changes, the
   alias and rate-limit tables as rows, and which page a configuration path lives on. Imports only core.js. */
import { state } from './core.js';

const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
const norm = (v) => (v === undefined ? null : v);
const same = (a, b) => JSON.stringify(norm(a)) === JSON.stringify(norm(b));
const primitive = (v) => v === null || typeof v !== 'object';
const named = (list) => list.every((x) => isObject(x) && typeof x.Name === 'string');
const nameKey = (x) => x.Name.trim().toLowerCase();

/*
 * Three-way merge of the draft onto a configuration that changed under it: base is what the draft started from, mine
 * the draft, theirs the new running configuration. Changes on different settings combine; the same setting changed
 * both ways is a conflict. Lists of names (utterances, capabilities) merge as sets; lists of named items (routes,
 * models, memory sources) by name. Returns { ok, value }.
 */
const CONFLICT = Symbol('conflict');
function merge(base, mine, theirs) {
  if (same(mine, base)) return theirs;
  if (same(theirs, base) || same(mine, theirs)) return mine;
  if (isObject(mine) && isObject(theirs)) {
    const b = isObject(base) ? base : {};
    const out = {};
    for (const k of [...new Set([...Object.keys(theirs), ...Object.keys(mine)])]) {
      const r = merge(b[k], mine[k], theirs[k]);
      if (r === CONFLICT) return CONFLICT;
      if (r !== undefined) out[k] = r;
    }
    return out;
  }
  if (Array.isArray(mine) && Array.isArray(theirs)) {
    const b = Array.isArray(base) ? base : [];
    if ([...b, ...mine, ...theirs].every(primitive)) {
      const removed = b.filter((x) => !mine.includes(x));
      const added = mine.filter((x) => !b.includes(x) && !theirs.includes(x));
      return theirs.filter((x) => !removed.includes(x)).concat(added);
    }
    if (named(b) && named(mine) && named(theirs)) return mergeNamed(b, mine, theirs);
  }
  return CONFLICT;
}
function mergeNamed(base, mine, theirs) {
  const index = (list) => { const m = new Map(); for (const x of list) { if (m.has(nameKey(x))) return null; m.set(nameKey(x), x); } return m; };
  const b = index(base); const m = index(mine); const t = index(theirs);
  if (!b || !m || !t) return CONFLICT;
  const out = [];
  for (const x of theirs) {
    const k = nameKey(x);
    if (!m.has(k)) {
      if (!b.has(k)) out.push(x);                    // they added it
      else if (!same(x, b.get(k))) return CONFLICT;  // I removed what they changed
      continue;                                      // I removed it
    }
    const r = merge(b.get(k), m.get(k), x);
    if (r === CONFLICT) return CONFLICT;
    out.push(r);
  }
  for (let i = 0; i < mine.length; i++) {
    const x = mine[i]; const k = nameKey(x);
    if (t.has(k)) continue;
    if (b.has(k)) { if (!same(x, b.get(k))) return CONFLICT; continue; } // they removed it: fine unless I changed it
    out.splice(Math.min(i, out.length), 0, x); // I added (or renamed) it: near where it was
  }
  return out;
}
export function rebase(base, mine, theirs) {
  const value = merge(base, mine, theirs);
  return value === CONFLICT ? { ok: false } : { ok: true, value: JSON.parse(JSON.stringify(value)) };
}

/*
 * How many settings the draft changes: a list of names counts once, a named item added or removed counts once, and
 * a renamed one (no match by name, but in the same place) counts as the settings changed in it.
 */
export function countChanges(base, draft) {
  const count = (b, m) => {
    if (same(b, m)) return 0;
    if (isObject(b) && isObject(m)) return [...new Set([...Object.keys(b), ...Object.keys(m)])].reduce((n, k) => n + count(b[k], m[k]), 0);
    const unique = (list) => new Set(list.map(nameKey)).size === list.length;
    if (Array.isArray(b) && Array.isArray(m) && !b.every(primitive) && named(b) && named(m) && unique(b) && unique(m)) {
      const inDraft = new Map(m.map((x) => [nameKey(x), x]));
      const inBase = new Set(b.map(nameKey));
      const gone = new Map();
      let n = 0;
      b.forEach((x, i) => { if (inDraft.has(nameKey(x))) n += count(x, inDraft.get(nameKey(x))); else gone.set(i, x); });
      m.forEach((y, i) => {
        if (inBase.has(nameKey(y))) return;
        if (gone.has(i)) { n += count(gone.get(i), y); gone.delete(i); } else n += 1;
      });
      return n + gone.size;
    }
    if (Array.isArray(b) && Array.isArray(m) && !b.every(primitive) && !m.every(primitive)) {
      let n = Math.abs(b.length - m.length);
      for (let i = 0; i < Math.min(b.length, m.length); i++) n += count(b[i], m[i]);
      return n;
    }
    return 1;
  };
  return count(base, draft);
}
export const draftChanges = () => (state.config && state.original ? Math.max(1, countChanges(JSON.parse(state.original), state.config)) : 0);

/*
 * The page a configuration path is edited on (a problem in the list links there). Profile selection lives with the
 * profiles; Middleware is otherwise the Gateway page.
 */
const PROFILE_KEYS = ['ProfileRules', 'VirtualModels', 'ClientProfiles', 'ProfileHeader', 'ProjectHeader', 'LocalOnlyWhen'];
export function pageForPath(path) {
  const [head, second] = String(path || '').split('.');
  if (head === 'Middleware') return PROFILE_KEYS.includes(second) ? 'profiles' : 'gateway';
  return { Routes: 'routes', Models: 'models', Connectors: 'connectors', Profiles: 'profiles', Embeddings: 'embeddings', Tiers: 'tiers', Memory: 'memory', Usage: 'usage', State: 'gateway' }[head] || 'routing';
}
/* The expandable item (state.open key) a path is inside, so a problem in a closed item can open it. */
export function itemKeyForPath(path) {
  const p = String(path || '').split('.');
  if (p[0] === 'Routes' && /^\d+$/.test(p[1])) return 'route:' + p[1];
  if (p[0] === 'Models' && /^\d+$/.test(p[1])) return 'model:' + p[1];
  if (p[0] === 'Connectors' && p[1]) return 'conn:' + p[1];
  if (p[0] === 'Profiles' && p[1]) return 'profile:' + p[1];
  if (p[0] === 'Memory' && p[1] === 'Sources' && /^\d+$/.test(p[2])) return 'mem:' + p[2];
  return null;
}

/*
 * Model aliases and rate limits are maps in the configuration, but edited as rows: two rows may have the same name
 * for a moment (they are flagged, not merged into one), and alias order matters (the first matching glob wins). The
 * rows are rebuilt from the map whenever the map changed some other way (Discard, a reload, the JSON page).
 */
function rows(slot, map) {
  const sig = JSON.stringify(map || {});
  if (!state[slot] || state[slot].sig !== sig) state[slot] = { sig, rows: Object.entries(map || {}).map(([key, value]) => ({ key, value })) };
  return state[slot].rows;
}
function write(slot, set) {
  const map = {};
  const seen = new Set();
  for (const r of state[slot].rows) {
    const k = r.key.trim();
    if (!k || seen.has(k.toLowerCase())) continue; // a duplicate counts once (flagged on the page) until it is renamed or removed
    seen.add(k.toLowerCase());
    map[k] = r.value;
  }
  set(map);
  state[slot].sig = JSON.stringify(map);
}
const middleware = () => (state.config.Middleware = state.config.Middleware || {});
export const aliasRows = () => rows('aliasRows', (state.config.Middleware || {}).ModelAliases);
export const limitRows = () => rows('limitRows', (state.config.Middleware || {}).RateLimits);
export const writeAliases = () => write('aliasRows', (map) => { middleware().ModelAliases = map; });
export const writeLimits = () => write('limitRows', (map) => { middleware().RateLimits = map; });

/* Rows whose name another row above already has, or that are empty: index → what is wrong. */
export function rowProblems(list, what) {
  const out = new Map();
  const first = new Map();
  list.forEach((r, i) => {
    const k = r.key.trim().toLowerCase();
    if (!k) { out.set(i, `Give this ${what} a name, or remove the row.`); return; }
    if (first.has(k)) out.set(i, `Row ${first.get(k) + 1} has the same name; only that one counts. Rename or remove one.`);
    else first.set(k, i);
  });
  return out;
}

/* Problems the page finds before asking the router: duplicate or empty rows in the gateway tables. */
export function localProblems() {
  if (!state.config || !state.config.Middleware) return [];
  const out = [];
  const check = (list, what, label, prefix) => rowProblems(list, what).forEach((message, i) => out.push({ path: null, selector: `[data-row="${prefix}-${i}"]`, page: 'gateway', message: `${label} row ${i + 1}: ${message}` }));
  check(aliasRows(), 'alias', 'Model aliases', 'alias');
  check(limitRows(), 'limit', 'Rate limits', 'limit');
  return out;
}
