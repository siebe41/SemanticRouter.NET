/* Semantic Router dashboard: Constants, state, helpers, the API client, toasts, form binding and sign-in. Imports nothing. */

/* ---------------------------------------------------------------------------------------------
   Themes. The built-in ones have a [data-theme] token block in app.css; the first is the default. More come from
   theme files in the themes directory (GET api/themes), loaded as their own stylesheet when picked.
   --------------------------------------------------------------------------------------------- */
export const THEMES = [
  { id: 'machine-dark', name: 'Machine Dark' },
  { id: 'machine-light', name: 'Machine Light' },
  { id: 'terminal', name: 'Terminal' },
];
export const THEME_KEY = 'semantic-router.theme';

export const PREFIX = window.ROUTER_PREFIX || '/router';
export const API = PREFIX + '/api';
export const CAPABILITY_HINTS = ['chat', 'code', 'tools', 'vision', 'reasoning'];
export const STRATEGIES = ['CheapestCapable', 'MostCapable', 'Priority'];
export const SCORING = ['MaxSimilarity', 'Centroid'];
export const CONNECTOR_TYPES = ['OpenAICompatible', 'Anthropic', 'Responses', 'Cli', 'Copilot'];
export const PRESETS = ['ClaudeCode', 'GitHubCopilot', 'Codex', 'Custom'];
export const MESSAGE_SELECTION = ['LastUserMessage', 'AllUserMessages', 'AllMessages'];

/* ---------------------------------------------------------------------------------------------
   State
   --------------------------------------------------------------------------------------------- */
export const state = {
  status: null,
  config: null,        // the editable draft
  original: null,      // JSON of the last loaded/applied config, for dirty checks
  meta: {},            // version, source, store, warning
  activity: [],
  open: new Set(),     // expanded item keys
  view: 'overview',
  errors: null,
  tester: { prompt: '', model: 'auto', result: null, reply: null, busy: false },
  activityFilter: 'all',
  tuning: null,          // GET api/tuning (Router learned)
  learnedFilter: 'active',
  autoRefresh: true,
  session: { role: null, tokenRequired: false, tokensConfigured: false },
  stream: null,          // the EventSource for api/events
  streamState: 'off',    // live | reconnecting | off
  live: { flash: null }, // the model that just answered, for the router graph
  memory: null,          // GET api/memory
  memoryLoading: false,
  memoryUi: { search: '', filter: 'all', colorBy: 'sharing', showMissing: true, selected: null },
  history: { versions: null, diff: null },
  trends: { window: '24h', data: null, loading: false },
  usage: { window: '7d', by: 'model', data: null, loading: false },
  evaluation: { text: null, location: null, count: 0, report: null, busy: false, health: null, compare: null, addFor: null, add: {}, form: { provider: 'OpenAICompatible', endpoint: '', model: '', apiKey: '', dimensions: 512 } },
  label: null,         // the Activity "label this prompt" form
  profileTester: { prompt: '', results: null, busy: false },
  lineups: [],         // Discover models: one { connector, busy, error, server, loading, loadingNote, note, lineup } per connector asked
};
export const lineupFor = (connector) => state.lineups.find((l) => l.connector === connector);

/* ---------------------------------------------------------------------------------------------
   Helpers
   --------------------------------------------------------------------------------------------- */
export const $ = (sel, root) => (root || document).querySelector(sel);
export const $$ = (sel, root) => Array.from((root || document).querySelectorAll(sel));
export const h = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
export const num = (v, d) => (v == null || v === '' ? '' : (typeof v === 'number' ? Number(v.toFixed(d == null ? 3 : d)) : v));
export const when = (iso) => { const d = new Date(iso); return isNaN(d) ? '' : d.toLocaleTimeString(); };
export const isDirty = () => state.config && JSON.stringify(state.config) !== state.original;
export const readOnly = () => state.session.role === 'ReadOnly';
export const isReference = (v) => typeof v === 'string' && /\$\{(env|secret):[^}]+\}/i.test(v);
export const tierName = (level) => {
  const t = (state.status && state.status.tiers || []).find((x) => x.level === level);
  return t ? `${level} · ${t.name}` : String(level);
};
export const tiers = () => (state.config && state.config.Tiers && state.config.Tiers.length ? state.config.Tiers : (state.status ? state.status.tiers.map((t) => ({ Level: t.level, Name: t.name, Description: t.description })) : []));
export const connectorNames = () => Object.keys((state.config && state.config.Connectors) || {});
export const lines = (arr) => (arr || []).join('\n');
export const fromLines = (text) => text.split('\n').map((s) => s.trim()).filter(Boolean);
export const csv = (arr) => (arr || []).join(', ');
export const fromCsv = (text) => text.split(',').map((s) => s.trim()).filter(Boolean);
export const kv = (obj) => Object.entries(obj || {}).map(([k, v]) => `${k}=${v}`).join('\n');
export const fromKv = (text) => { const o = {}; fromLines(text).forEach((l) => { const i = l.indexOf('='); if (i > 0) o[l.slice(0, i).trim()] = l.slice(i + 1).trim(); }); return o; };

export async function api(method, path, body) {
  const res = await fetch(API + path, {
    method,
    headers: body !== undefined ? { 'Content-Type': 'application/json' } : {},
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  let data = null;
  try { data = text ? JSON.parse(text) : null; } catch { data = { raw: text }; }
  if (res.status === 401 && path !== '/session') { showSignIn(); }
  if (!res.ok) {
    const err = new Error((data && (data.detail || data.title || (data.errors && data.errors.join('; ')))) || `HTTP ${res.status}`);
    err.data = data; err.status = res.status;
    throw err;
  }
  return data;
}

export let toastTimer = null;
export function toast(message, kind) {
  const el = $('#toast');
  el.textContent = message;
  el.className = 'toast ' + (kind || '');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.add('hidden'), 4500);
}

export function banner(message, kind) {
  const el = $('#banner');
  if (!message) { el.classList.add('hidden'); return; }
  el.innerHTML = message;
  el.className = 'banner ' + (kind || '');
}

/* Path-based binding: data-bind="Routes.0.Utterances" with data-kind="lines|csv|kv|number|float|bool|text|timespan". */
export function getPath(obj, path) {
  return path.split('.').reduce((o, k) => (o == null ? undefined : o[k]), obj);
}
export function setPath(obj, path, value) {
  const keys = path.split('.');
  let o = obj;
  for (let i = 0; i < keys.length - 1; i++) {
    if (o[keys[i]] == null) o[keys[i]] = /^\d+$/.test(keys[i + 1]) ? [] : {};
    o = o[keys[i]];
  }
  o[keys[keys.length - 1]] = value;
}
export function readInput(el) {
  const kind = el.dataset.kind || 'text';
  switch (kind) {
    case 'bool': return el.checked;
    case 'number': return el.value === '' ? (el.dataset.nullable ? null : 0) : parseInt(el.value, 10);
    case 'float': return el.value === '' ? (el.dataset.nullable ? null : 0) : parseFloat(el.value);
    case 'lines': return fromLines(el.value);
    case 'csv': return fromCsv(el.value);
    case 'kv': return fromKv(el.value);
    case 'nulltext': return el.value === '' ? null : el.value;
    default: return el.value;
  }
}

/* ---------------------------------------------------------------------------------------------
   Sign-in (remote access with a dashboard token)
   --------------------------------------------------------------------------------------------- */
export function showSignIn(message) {
  if (!message && $('#signin-form')) return; // already showing; background refreshes must not clear the input
  $('#view').innerHTML = `
    <div class="card section signin" style="max-width:460px">
      <h2>Sign in</h2>
      <p class="muted small">This router asks for its dashboard token (<code>SemanticRouter:Dashboard:AdminToken</code>, or the read-only token).</p>
      <form id="signin-form">
        <label class="field"><span class="name">Token</span><input type="password" id="signin-token" autocomplete="current-password" required></label>
        ${message ? `<p class="small" style="color:var(--danger)">${h(message)}</p>` : ''}
        <div class="inline" style="margin-top:10px"><button class="btn primary" type="submit">Sign in</button></div>
      </form>
    </div>`;
  $('#signin-form').addEventListener('submit', async (ev) => {
    ev.preventDefault();
    try {
      state.session = await api('POST', '/session', { token: $('#signin-token').value });
      const { loadAll, route } = await import('./shell.js'); // dynamic: core imports nothing statically
      await loadAll(); route();
    } catch (e) { showSignIn(e.message); }
  });
  const input = $('#signin-token'); if (input) input.focus();
}


/* ---------------------------------------------------------------------------------------------
   Views
   --------------------------------------------------------------------------------------------- */
export const views = {};
