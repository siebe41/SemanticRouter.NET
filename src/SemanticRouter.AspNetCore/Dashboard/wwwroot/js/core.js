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

export const PREFIX = document.querySelector('meta[name="router-prefix"]')?.content || '/router';
export const API = PREFIX + '/api';
export const CAPABILITY_HINTS = ['chat', 'code', 'tools', 'vision', 'reasoning'];
export const STRATEGIES = ['CheapestCapable', 'MostCapable', 'Priority', 'Fastest', 'Balanced'];
export const SCORING = ['MaxSimilarity', 'Centroid'];
// The router's own connector types, until api/status says which it has; installed packages add more (api/status).
const BUILT_IN_CONNECTOR_TYPES = ['OpenAICompatible', 'Anthropic', 'Responses', 'Cli'];
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
  reliability: null,     // GET api/reliability (Models page)
  reliabilityLoading: false,
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
  report: null,        // the Feedback form: kind, Activity entry, the text typed so far
  profileTester: { prompt: '', results: null, busy: false },
  checking: new Set(), // connectors whose endpoint check is running
  audit: { entries: null, loading: false, limit: 200, error: null }, // GET api/audit (Audit log)
  hostService: { data: null, loading: false, error: null, scope: null, port: null, waiting: null, stopped: null }, // GET api/service (Service)
  lineups: [],         // Discover models: one { connector, busy, error, server, loading, loadingNote, note, lineup } per connector asked
  issues: null,        // [{ path, message }] behind state.errors: each problem tied to its field (data-bind path)
  focusIssue: false,   // after Validate or Apply: focus the first problem on the page
  focusPath: null,     // a problem picked in the list: focus its field once its page is rendered
  focusAfter: null,    // selectors to focus after the next render, the first that exists (a new item's name, the next item)
  conflict: null,      // the configuration changed under the draft: { currentVersion, actor, at, source, persist, dismissed }
  saveApi: null,       // false once POST config/save answered 404 (an older router): no Save / Revert buttons
  jsonText: null,      // the JSON editor's text while it differs from the draft (null: it shows the draft)
  aliasRows: null,     // Gateway model aliases as rows while editing: { sig, rows: [{ key, value }] }
  limitRows: null,     // Gateway rate limits as rows: { sig, rows: [{ key, value }] }
  busy: new Set(),     // actions whose promise is still pending (actionKey), so a second click is ignored
  activityNew: 0,      // entries that arrived while the Activity table was held still
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
// Typed text outside the draft that a reload would lose: the JSON editor, the evaluation set.
export const jsonEdited = () => state.jsonText != null && state.jsonText !== JSON.stringify(state.config, null, 2);
export const evaluationEdited = () => state.evaluation.text != null && state.evaluation.savedText != null && state.evaluation.text !== state.evaluation.savedText;
// Where the running configuration came from, in words (the API keeps the enum value).
export const sourceLabel = (source) => ({ Memory: 'not saved', Store: 'saved', Configuration: 'app settings' }[source] || String(source || '').toLowerCase());
// The time, with the date when it was not today.
export const whenDate = (iso) => {
  const d = new Date(iso);
  if (isNaN(d)) return '';
  const time = d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
  return d.toDateString() === new Date().toDateString() ? time : d.toLocaleDateString(undefined, { day: 'numeric', month: 'short' }) + ' ' + time;
};
export const plural = (n, one, many) => `${n} ${n === 1 ? one : (many || one + 's')}`;
export const readOnly = () => state.session.role === 'ReadOnly' || state.session.role === 'Metrics';
// The metrics role sees figures only (no prompts, configuration or activity): the Overview's counts and the Usage page.
export const metricsOnly = () => state.session.role === 'Metrics';
export const METRICS_VIEWS = ['overview', 'usage'];
/*
 * The rail's sections (index.html) and their pages, in tab order, with each page's tab label. A page keeps its own
 * address (#/tiers, #/learned), so links from the tray, notices, webhooks and bookmarks need no section. Feedback is in
 * no section: it sits at the foot of the rail.
 */
export const SECTIONS = [
  { id: 'monitor', pages: [['overview', 'Overview'], ['live', 'Live'], ['activity', 'Activity'], ['usage', 'Usage'], ['memory', 'Memory']] },
  { id: 'routing', pages: [['tiers', 'Tiers'], ['routes', 'Routes'], ['routing', 'Rules'], ['profiles', 'Profiles'], ['evaluate', 'Evaluate'], ['learned', 'Router learned']] },
  { id: 'providers', pages: [['models', 'Models'], ['connectors', 'Connectors'], ['embeddings', 'Embeddings']] },
  { id: 'gateway', pages: [['gateway', 'Settings'], ['clients', 'Connect clients']] },
  { id: 'admin', pages: [['service', 'Service'], ['json', 'JSON'], ['audit', 'Audit log']] },
];
export const sectionOf = (view) => SECTIONS.find((s) => s.pages.some(([page]) => page === view));
// Attributes for a button that spends embedding or API calls: the read-only role sees it disabled.
export const adminOnly = (busy) => (readOnly() ? 'disabled title="Needs the admin token"' : busy ? 'disabled' : '');
export const isReference = (v) => typeof v === 'string' && /\$\{(env|secret):[^}]+\}/i.test(v);
export const tierName = (level) => {
  const t = (state.status && state.status.tiers || []).find((x) => x.level === level);
  return t ? `${level} · ${t.name}` : String(level);
};
export const tiers = () => (state.config && state.config.Tiers && state.config.Tiers.length ? state.config.Tiers : (state.status ? state.status.tiers.map((t) => ({ Level: t.level, Name: t.name, Description: t.description })) : []));
export const connectorNames = () => Object.keys((state.config && state.config.Connectors) || {});
const builtInConnectorTypes = () => (state.status && state.status.connectorTypes && state.status.connectorTypes.builtIn.length ? state.status.connectorTypes.builtIn : BUILT_IN_CONNECTOR_TYPES);
/** The connector types to offer: the built-in ones, then those installed packages provide. */
export const connectorTypes = () => [...builtInConnectorTypes(), ...((state.status && state.status.connectorTypes && state.status.connectorTypes.installed) || [])];
export const isBuiltInConnectorType = (type) => builtInConnectorTypes().some((t) => t.toLowerCase() === String(type || 'OpenAICompatible').toLowerCase());
export const isInstalledConnectorType = (type) => connectorTypes().some((t) => t.toLowerCase() === String(type || 'OpenAICompatible').toLowerCase());
/**
 * Whether a connector (its configuration) runs a program rather than calling an HTTP endpoint, as the router decides
 * (ConnectorOptions.RunsProgram): a Cli connector, or one of an installed type without a base address. Its models are
 * subscriptions: free per request, valued at the API price.
 */
export const runsProgram = (conn) => !!conn && ((conn.Type || '').toLowerCase() === 'cli' || (!isBuiltInConnectorType(conn.Type) && !conn.BaseAddress));
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
    // A proxy in front of the router answers in HTML or plain text: show what it said rather than only the status.
    const raw = data && data.raw ? data.raw.replace(/<(script|style)[\s\S]*?<\/\1>/gi, ' ').replace(/<[^>]*>/g, ' ').replace(/\s+/g, ' ').trim().slice(0, 300) : '';
    const err = new Error((data && (data.detail || data.title || (data.errors && data.errors.join('; ')))) || (raw ? `${raw} (HTTP ${res.status})` : `HTTP ${res.status}`));
    err.data = data; err.status = res.status;
    throw err;
  }
  return data;
}

/* ---------------------------------------------------------------------------------------------
   Toasts: a stack, so a new message never replaces one still being read. Each is spoken once through a live region
   (errors through role="alert"); one with an action (Undo) or marked sticky stays until it is used or dismissed.
   --------------------------------------------------------------------------------------------- */
function announce(message, kind) {
  const el = $(kind === 'danger' ? '#announce-alert' : '#announce-status');
  if (!el) return;
  el.textContent = '';
  // Cleared first and written on the next frame, so the same words twice are still a change a screen reader hears.
  requestAnimationFrame(() => { el.textContent = message; });
}
export function toast(message, kind, options) {
  const opts = options || {};
  const box = $('#toast');
  if (!box) return { dismiss() {} };
  const same = Array.from(box.children).find((t) => t.dataset.text === message && !t.querySelector('.toast-action'));
  if (same) same.remove();
  const el = document.createElement('div');
  const error = kind === 'danger';
  el.className = 'toast ' + (kind || '');
  el.dataset.text = message;
  el.innerHTML = `${error ? '<span class="toast-icon" aria-hidden="true">!</span>' : ''}<span class="toast-text"></span>`;
  el.querySelector('.toast-text').textContent = message;
  let timer = null;
  const dismiss = () => { clearTimeout(timer); el.remove(); box.classList.toggle('hidden', !box.children.length); };
  if (opts.action) {
    const b = document.createElement('button');
    b.type = 'button'; b.className = 'btn small toast-action'; b.textContent = opts.action.label;
    b.addEventListener('click', () => { dismiss(); opts.action.run(); });
    el.appendChild(b);
  }
  if (opts.sticky || opts.action || error) {
    const x = document.createElement('button');
    x.type = 'button'; x.className = 'btn ghost small toast-close'; x.setAttribute('aria-label', 'Dismiss'); x.textContent = '×';
    x.addEventListener('click', dismiss);
    el.appendChild(x);
  }
  box.appendChild(el);
  box.classList.remove('hidden');
  // At most four at a time: the oldest that is not waiting for a decision goes first.
  while (box.children.length > 4) (Array.from(box.children).find((t) => !t.querySelector('.toast-action')) || box.firstElementChild).remove();
  announce(error && !/^(couldn.t|not |no |error)/i.test(message) ? 'Error: ' + message : message, kind);
  const ms = opts.sticky ? 0 : opts.action ? 10000 : error ? 9000 : 4500;
  const start = () => { clearTimeout(timer); if (ms) timer = setTimeout(dismiss, ms); };
  // Kept while the pointer or the keyboard is on it, so an Undo can still be reached.
  el.addEventListener('mouseenter', () => clearTimeout(timer));
  el.addEventListener('mouseleave', start);
  el.addEventListener('focusin', () => clearTimeout(timer));
  el.addEventListener('focusout', start);
  start();
  return { dismiss };
}

/*
 * A table or feed that scrolls must be reachable from the keyboard (WCAG 2.1.1): it takes the focus, so the arrow keys
 * scroll it, and is named after its table or its card's heading. One that fits is not an extra tab stop.
 */
export function scrollRegions(root) {
  $$('.table-wrap, .feed', root).forEach((el) => {
    const scrolls = el.scrollWidth > el.clientWidth + 1 || el.scrollHeight > el.clientHeight + 1;
    if (!scrolls) {
      if (el.dataset.scrollRegion) { ['tabindex', 'role', 'aria-label'].forEach((a) => el.removeAttribute(a)); delete el.dataset.scrollRegion; }
      return;
    }
    if (el.dataset.scrollRegion) return;
    const table = el.querySelector('table');
    const card = el.closest('.card');
    const heading = card && card.querySelector('h2, h3');
    const name = (table && (table.getAttribute('aria-label') || (table.caption && table.caption.textContent))) || (heading && heading.textContent) || 'Table';
    el.setAttribute('tabindex', '0');
    el.setAttribute('role', 'region');
    el.setAttribute('aria-label', name.trim());
    el.dataset.scrollRegion = '1';
  });
}

/* The chrome's banner: rewritten only when what it says changes, so a screen reader does not hear it again every refresh. */
export function banner(message, kind) {
  const el = $('#banner');
  const key = message ? (kind || '') + '|' + message : '';
  if (el.dataset.key === key) return;
  el.dataset.key = key;
  if (!message) { el.classList.add('hidden'); el.innerHTML = ''; return; }
  el.innerHTML = message;
  el.className = 'banner ' + (kind || '');
}

/*
 * An in-page question instead of the browser's confirm(): a modal <dialog> with the given buttons. Resolves to the
 * chosen button's value, or null when it is closed (Escape). The focus goes back where it was.
 */
export function ask({ title, body, actions, wide }) {
  return new Promise((resolve) => {
    let d = $('#ask');
    if (!d) { d = document.createElement('dialog'); d.id = 'ask'; d.className = 'ask'; d.setAttribute('aria-labelledby', 'ask-title'); document.body.appendChild(d); }
    const back = document.activeElement;
    d.classList.toggle('wide', !!wide);
    d.innerHTML = `<form method="dialog"><h2 id="ask-title">${h(title)}</h2><div class="ask-body">${body || ''}</div>
      <div class="ask-actions">${actions.map((a, i) => `<button class="btn ${a.kind || ''}" value="${i}">${h(a.label)}</button>`).join('')}</div></form>`;
    d.returnValue = '';
    d.onclose = () => {
      const v = d.returnValue;
      resolve(v === '' || actions[+v].value === undefined ? null : actions[+v].value);
      // After the action that asked has finished (its button is no longer disabled as busy).
      setTimeout(() => { if (back && back.isConnected && back.focus && !$('#ask').open) back.focus({ preventScroll: true }); });
    };
    d.showModal();
    const first = d.querySelectorAll('.ask-actions button')[Math.max(0, actions.findIndex((a) => a.focus))];
    if (first) first.focus();
  });
}

/* A key per action button, so a second click while the first is still running is ignored (and the button shown busy). */
export const actionKey = (el) => [el.dataset.action, el.dataset.id, el.dataset.index, el.dataset.name, el.dataset.connector, el.dataset.model].map((x) => x || '').join('|');

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
      <p class="muted small">This router asks for its dashboard token (<code>SemanticRouter:Dashboard:AdminToken</code>, or the read-only or metrics token).</p>
      <form id="signin-form">
        <label class="field"><span class="name">Token</span><input type="password" id="signin-token" autocomplete="current-password" required></label>
        ${message ? `<p class="small" style="color:var(--danger)">${h(message)}</p>` : ''}
        <div class="inline" style="margin-top:10px"><button class="btn primary" type="submit">Sign in</button></div>
      </form>
    </div>`;
  $('#signin-form').addEventListener('submit', async (ev) => {
    ev.preventDefault();
    // The draft and the JSON editor's text survive signing in again: loading would replace them.
    const draft = isDirty() ? { config: state.config, original: state.original, version: state.meta.version, instance: state.meta.instance } : null;
    const jsonText = state.jsonText;
    try {
      state.session = await api('POST', '/session', { token: $('#signin-token').value });
      const { keepDraft, loadAll, route } = await import('./shell.js'); // dynamic: core imports nothing statically
      await loadAll();
      if (draft) keepDraft(draft);
      state.jsonText = jsonText;
      route();
    } catch (e) { showSignIn(e.message); }
  });
  const input = $('#signin-token'); if (input) input.focus();
}


/* ---------------------------------------------------------------------------------------------
   Views
   --------------------------------------------------------------------------------------------- */
export const views = {};
