/* Semantic Router dashboard: Loading, the rail, tabs and top bar, themes, rendering and hash routing. */
import { loadTrends } from './charts.js';
import { $, $$, actionKey, api, banner, h, isDirty, METRICS_VIEWS, metricsOnly, plural, PREFIX, readOnly, scrollRegions, SECTIONS, sectionOf, sourceLabel, state, THEME_KEY, THEMES, toast, views, whenDate } from './core.js';
import { loadDigest } from './digest.js';
import { draftChanges, itemKeyForPath, rebase } from './draft.js';
import { endpointPill } from './endpoints.js';
import { loadTuning } from './learned.js';
import { activityHeld, scheduleLive, showNewEntries } from './live.js';
import { graph, mounts, readPalette, unmount } from './memory.js';
import { loadUsage } from './usage.js';
import { setupShowing } from './setup.js';
import { loadTemplateDefaults } from './template-defaults.js';

/* ---------------------------------------------------------------------------------------------
   Data loading
   --------------------------------------------------------------------------------------------- */
export async function loadAll() {
  try { state.session = await api('GET', '/session'); } catch { /* older router: no session endpoint */ }
  if (metricsOnly()) {
    // Figures only: no configuration, activity or learned changes to load.
    state.status = await api('GET', '/status');
    setConfig({ config: {} });
    await loadDigest();
    renderChrome();
    loadThemes();
    return;
  }
  const [status, cfg, activity] = await Promise.all([api('GET', '/status'), api('GET', '/config'), api('GET', '/activity?limit=200')]);
  state.status = status;
  setConfig(cfg);
  state.activity = activity.entries;
  state.feedback = activity.feedback || {};
  await Promise.all([loadTuning(), loadDigest(), loadTemplateDefaults()]);
  renderChrome();
  loadThemes();
}

export function setConfig(envelope) {
  state.config = envelope.config;
  state.original = JSON.stringify(envelope.config);
  state.meta = { version: envelope.version, instance: envelope.instance, source: envelope.source, store: envelope.store, secretStore: envelope.secretStore, warning: envelope.warning, createdAt: envelope.createdAt };
  state.errors = null;
  state.issues = null;
  state.conflict = null;
}

/*
 * The running configuration changed (an utterance added, a learned change, a save or revert): take it, and keep the
 * draft. Unapplied edits are moved onto the new configuration when they don't touch what changed; otherwise the draft
 * stays as it is, on its old version, and the conflict notice says so. Returns 'replaced', 'rebased' or 'kept'.
 */
export function adoptConfig(envelope) {
  if (!isDirty()) { setConfig(envelope); return 'replaced'; }
  const r = rebase(JSON.parse(state.original), state.config, envelope.config);
  if (r.ok) { setConfig(envelope); state.config = r.value; return 'rebased'; }
  noteConflict({ currentVersion: envelope.version, instance: envelope.instance, at: envelope.createdAt });
  return 'kept';
}

/*
 * The version counter starts again at 1 when the router restarts, so a version number alone can name another
 * configuration: the router also sends an id of the running process (instance). Same configuration = same version on
 * the same instance. A router that sends no instance is compared by version only.
 */
export const sameConfig = (version, instance, v2, i2) => version === v2 && (instance == null || i2 == null || instance === i2);

/* After signing in again: the draft from before goes back on top of what was loaded. */
export function keepDraft(draft) {
  if (sameConfig(state.meta.version, state.meta.instance, draft.version, draft.instance)) { state.config = draft.config; return; }
  const loaded = { version: state.meta.version, instance: state.meta.instance, createdAt: state.meta.createdAt };
  const r = rebase(JSON.parse(draft.original), draft.config, state.config);
  if (r.ok) { state.config = r.value; return; }
  state.config = draft.config; state.original = draft.original; state.meta.version = draft.version; state.meta.instance = draft.instance;
  noteConflict({ currentVersion: loaded.version, instance: loaded.instance, at: loaded.createdAt });
}

/* The configuration changed under the draft: show the notice, with who changed it when the history says. */
export function noteConflict(info) {
  const before = state.conflict && sameConfig(state.conflict.currentVersion, state.conflict.instance, info.currentVersion, info.instance) ? state.conflict : {};
  state.conflict = { ...before, ...Object.fromEntries(Object.entries(info).filter(([, v]) => v !== undefined)), dismissed: false };
  renderChrome();
  if (info.actor) return;
  api('GET', '/config/history?limit=1').then((r) => {
    const v = r && r.versions && r.versions[0];
    if (v && state.conflict && sameConfig(state.conflict.currentVersion, state.conflict.instance, info.currentVersion, info.instance) && !state.conflict.actor) {
      Object.assign(state.conflict, { actor: v.actor, at: v.timestamp, source: v.source });
      renderChrome();
    }
  }).catch(() => { /* the notice works without it */ });
}

/*
 * Someone else applied a configuration (another tab, the CLI, a learned change, a restart): with a clean draft, load it
 * quietly; with unapplied edits, move them onto it when they don't touch what changed, and otherwise say so rather
 * than letting Apply overwrite it unseen. Any other version counts (not only a higher one): after a restart the
 * counter starts again. Returns true when it reloaded.
 */
async function checkVersion() {
  const s = state.status;
  if (!s || metricsOnly() || s.version == null || state.meta.version == null || sameConfig(s.version, s.instance, state.meta.version, state.meta.instance)) return false;
  if (!isDirty()) { setConfig(await api('GET', '/config')); return true; }
  // Already said for this configuration: don't fetch and try again every refresh.
  if (state.conflict && sameConfig(state.conflict.currentVersion, state.conflict.instance, s.version, s.instance)) return false;
  const result = adoptConfig(await api('GET', '/config'));
  if (result === 'kept') return false;
  toast(`The configuration changed elsewhere (now version ${state.meta.version}).${isDirty() ? ' Your unapplied changes are kept on top of it.' : ''}`, 'ok');
  return true;
}

/* Is text selected on the page? A refresh then would drop the selection, so it waits. */
export const selecting = () => {
  const sel = window.getSelection && window.getSelection();
  return !!(sel && !sel.isCollapsed && sel.rangeCount && $('#view').contains(sel.anchorNode));
};

/* The status only (while there are unapplied edits): the top bar stays current, the page is left alone. */
export async function refreshChrome() {
  try {
    state.status = await api('GET', '/status');
    if (await checkVersion()) render(); else renderChrome();
  } catch { /* keep the last known state */ }
}

export async function refreshStatus(opts) {
  const force = !!(opts && opts.force);
  try {
    if (metricsOnly()) {
      state.status = await api('GET', '/status');
      if (state.view === 'overview') { await loadDigest(); state.trends.data = null; render(); } else { loadUsage(true); renderChrome(); }
      return;
    }
    const [status, activity] = await Promise.all([api('GET', '/status'), api('GET', '/activity?limit=200')]);
    const before = new Set(state.activity.map((e) => e.id));
    state.status = status;
    state.activity = activity.entries;
    state.feedback = activity.feedback || {};
    const reloaded = await checkVersion();
    if (state.view === 'overview' || state.view === 'learned') await loadTuning();
    if (state.view === 'overview') await loadDigest();
    renderChrome();
    // Fresh trends in the background: the card keeps its last figures meanwhile, rather than shrinking to "Loading…"
    // and back, which made the page jump for anyone scrolled down.
    if (state.view === 'overview') loadTrends(true);
    if (state.view === 'usage') { loadUsage(true); }
    // The Activity table holds still while a label is being written or text is selected; it counts what arrived.
    const held = state.view === 'activity' && !force && activityHeld();
    if (held) { state.activityNew += state.activity.filter((e) => !before.has(e.id)).length; showNewEntries(); }
    if (!force && selecting()) return;
    if (reloaded || force || state.view === 'overview' || state.view === 'learned' || (state.view === 'activity' && !held)) render();
    else if (state.view === 'live') scheduleLive();
  } catch (e) { /* keep the last known state */ }
}

/* ---------------------------------------------------------------------------------------------
   Chrome (rail, tabs, top bar)
   --------------------------------------------------------------------------------------------- */

/*
 * Writes a chrome element only when what it shows changes: the status pill is a live region, and rewriting it every
 * refresh made screen readers read it out every five seconds. A change in a tooltip alone is patched in place.
 */
const shown = {};
function setHtml(el, html) {
  if (!el || shown[el.id] === html) return;
  const bare = (x) => x.replace(/ title="[^"]*"/g, '');
  const same = shown[el.id] != null && bare(shown[el.id]) === bare(html);
  shown[el.id] = html;
  if (same) {
    const t = document.createElement('template'); t.innerHTML = html;
    const now = el.querySelectorAll('*'); const next = t.content.querySelectorAll('*');
    if (now.length === next.length) {
      now.forEach((x, i) => { const v = next[i].getAttribute('title'); if (v == null) x.removeAttribute('title'); else x.setAttribute('title', v); });
      return;
    }
  }
  el.innerHTML = html;
}

/* Overall health for the top bar's dot: red when requests cannot be answered, amber when degraded or warming up. */
function health(s) {
  const enabled = s.models.filter((m) => m.enabled).length;
  const sv = s.service || {};
  if (!enabled) return { tone: 'danger', short: '0 models', label: 'No model is enabled: requests cannot be answered' };
  if (sv.state === 'failing') return { tone: 'danger', short: 'failing', label: sv.stateNote || 'Failing' };
  if (sv.state === 'starting' || (!s.warmedUp && !s.warmUpError)) return { tone: 'warn', short: 'warming up', label: sv.stateNote || 'Warming up: embedding the routes' };
  if (sv.state === 'degraded' || s.warmUpError) return { tone: 'warn', short: 'degraded', label: sv.stateNote || 'Degraded: the route index could not be embedded' };
  return { tone: 'ok', short: '', label: 'Running' };
}

export function updateSaveBar() {
  const show = isDirty() && !readOnly();
  $('#save-bar').classList.toggle('hidden', !show);
  if (!show) return;
  const text = plural(draftChanges(), 'unapplied change');
  const count = $('#change-count');
  if (count.textContent !== text) count.textContent = text;
}

/* "Running configuration not saved": applied with Apply only (or by the API) and not written to the store. */
function unsavedBar() {
  const s = state.status;
  const bar = $('#unsaved-bar');
  const on = !!(s && s.unsaved === true && !metricsOnly());
  bar.classList.toggle('hidden', !on);
  if (!on) { setHtml(bar, ''); return; }
  const can = !readOnly() && state.saveApi !== false;
  const store = s.store || state.meta.store;
  setHtml(bar, `<span class="unsaved-note" title="${h(`The running router uses changes that are not in ${store || 'the configuration store'}: a restart undoes them.`)}"><span class="dot warn" aria-hidden="true"></span> Running configuration not saved</span>` +
    (can ? `<button class="btn small" type="button" data-action="config-save" title="${h(`Write the running configuration to ${store || 'the configuration store'}`)}">Save</button><button class="btn ghost small" type="button" data-action="config-revert" title="Go back to the saved configuration">Revert to saved</button>` : ''));
}

/* The configuration changed under the draft: what happened and the three ways on. */
function conflictNotice() {
  const c = state.conflict;
  const el = $('#draft-notice');
  const on = !!(c && !c.dismissed && isDirty() && !readOnly());
  el.classList.toggle('hidden', !on);
  if (!on) { setHtml(el, ''); return; }
  const by = c.actor ? `by ${h(c.actor)}` : '';
  const at = c.at ? `at ${h(whenDate(c.at))}` : '';
  const via = c.source && !/^(Memory|Store|Configuration)$/.test(c.source) ? ` <span class="faint">(${h(c.source)})</span>` : '';
  const restarted = c.instance != null && state.meta.instance != null && c.instance !== state.meta.instance;
  setHtml(el, `<div><b>Configuration changed ${[by, at].filter(Boolean).join(' ')}</b>${via}. Your draft started from version ${h(state.meta.version)}${restarted ? ' before the router restarted' : ''}; the router now runs version ${h(c.currentVersion)}.</div>
    <div class="row-actions"><button class="btn small danger" type="button" data-action="conflict-reload">Reload (lose your changes)</button><button class="btn small" type="button" data-action="conflict-compare">Compare</button><button class="btn small primary" type="button" data-action="conflict-apply">Apply mine anyway</button><button class="btn ghost small" type="button" data-action="conflict-dismiss" aria-label="Hide; Apply asks again" title="Hide; Apply asks again">×</button></div>`);
}

export function renderChrome() {
  const s = state.status;
  if (s) {
    const sub = `v${s.version} · ${sourceLabel(s.source)}${s.machine ? ' · ' + s.machine : ''}`;
    if ($('#brand-sub').textContent !== sub) $('#brand-sub').textContent = sub;
    $('#brand-sub').title = s.machine ? `Running on ${s.machine}` : '';
    $('#brand').title = `Semantic Router ${sub}`;
    $('#lib-version').textContent = 'SemanticRouter ' + s.libraryVersion;
    const cooling = s.models.filter((m) => m.coolingDownUntil).length;
    const now = endpointPill();
    const hl = health(s);
    setHtml($('#status-pill'),
      `<span class="dot ${hl.tone}" role="img" aria-label="${h('Health: ' + hl.label)}" title="${h(hl.label)}"></span>${hl.short ? ` <span class="pill-short">${h(hl.short)}</span>` : ''} <span class="pill-detail">${h(s.gateway.mode)} mode · ${plural(s.models.filter((m) => m.enabled).length, 'model')}${now ? ' ·' : ''}</span>` +
      (now ? ' ' + now : '') +
      (cooling ? ` · <span class="badge warn">${cooling} cooling down</span>` : '') +
      (s.warmedUp ? '' : (s.warmUpError ? ` · <span class="badge danger" title="${h(s.warmUpError)}">embeddings failed</span>` : ' · <span class="badge info">warming up</span>')));
  }
  updateSaveBar();
  unsavedBar();
  conflictNotice();
  renderNav();
  const notes = [];
  const who = state.session.identity;
  if (metricsOnly()) notes.push(`${who && !/ token$|^this machine$/.test(who) ? `Signed in as <b>${h(who)}</b> with the <b>metrics</b> role` : 'Signed in with the <b>metrics</b> token'}: you see figures only, not prompts, activity or the configuration.`);
  else if (readOnly()) notes.push(`${who && !/ token$|^this machine$/.test(who) ? `Signed in as <b>${h(who)}</b> with the <b>read-only</b> role` : 'Signed in with the <b>read-only</b> token'}: you can look, but not change settings or run what spends embedding or API calls (classify, evaluate, check, discover).`);
  if (state.meta.warning) notes.push(h(state.meta.warning));
  // On the Overview, the setup card says "no model is enabled" (with what to do): the banner does not repeat it.
  const setupCard = state.view === 'overview' && setupShowing();
  ((s && s.warnings) || []).filter((w) => !(setupCard && /no model is enabled/i.test(w))).forEach((w) => notes.push(h(w)));
  ((s && s.budgets) || []).filter((b) => b.exceeded || b.warning).forEach((b) => notes.push(`Budget <b>${h(b.name)}</b> (${h(b.covers)}) is ${b.exceeded ? 'spent' : 'at ' + b.percent + '%'}. <a href="#/usage">Usage</a>`));
  if (notes.length) banner(notes.join('<br>'), 'warn'); else banner(null);
  const signOut = $('#sign-out');
  if (signOut) signOut.classList.toggle('hidden', !state.session.role || !state.session.tokenRequired);
}

/*
 * The rail marks the current page's section and links each section to the page last open in it; the tab strip lists
 * the current section's pages. The strip is rebuilt only when what it shows changes: the status refresh calls this every
 * few seconds, and a rebuild would take the focus from a tab.
 */
const lastPage = {};
let tabsKey = null;
function renderNav() {
  const section = sectionOf(state.view);
  if (section) lastPage[section.id] = state.view;
  const visible = (page) => !metricsOnly() || METRICS_VIEWS.includes(page);
  $$('#nav a').forEach((a) => {
    const s = SECTIONS.find((x) => x.id === a.dataset.section);
    const pages = s.pages.map(([page]) => page).filter(visible);
    const on = s === section;
    a.classList.toggle('hidden', !pages.length);
    a.setAttribute('href', `#/${pages.includes(lastPage[s.id]) ? lastPage[s.id] : pages[0] || s.pages[0][0]}`);
    a.classList.toggle('active', on);
    if (on) a.setAttribute('aria-current', 'true'); else a.removeAttribute('aria-current');
  });
  const feedback = $('#sidebar a[data-view="feedback"]');
  feedback.classList.toggle('hidden', !visible('feedback'));
  feedback.classList.toggle('active', state.view === 'feedback');
  if (state.view === 'feedback') feedback.setAttribute('aria-current', 'page'); else feedback.removeAttribute('aria-current');
  const tabs = section ? section.pages.filter(([page]) => visible(page)) : [];
  const key = tabs.map(([page]) => page).join() + '|' + state.view;
  if (key === tabsKey) return;
  tabsKey = key;
  const strip = $('#tabs');
  strip.classList.toggle('hidden', !tabs.length);
  strip.innerHTML = tabs.map(([page, label]) => `<a href="#/${page}" data-view="${page}"${page === state.view ? ' class="active" aria-current="page"' : ''}>${h(label)}</a>`).join('');
  // On a narrow window the strip scrolls sideways: bring the current tab into view.
  const current = $('a.active', strip);
  if (current) strip.scrollLeft = Math.max(0, current.offsetLeft - strip.offsetLeft - (strip.clientWidth - current.offsetWidth) / 2);
}

let themeList = THEMES.map((t) => ({ ...t, builtIn: true, missingTokens: [], contrastIssues: [] }));
const isThemeId = (id) => /^[a-z0-9][a-z0-9-]{0,63}$/.test(id || '');
function themeOptions(current) {
  const option = (t) => {
    const notes = [t.missingTokens.length ? `${t.missingTokens.length} tokens missing` : '', t.contrastIssues.length ? `${t.contrastIssues.length} contrast issues` : ''].filter(Boolean).join(', ');
    return `<option value="${h(t.id)}" ${t.id === current ? 'selected' : ''} title="${h(notes ? `${notes}: ${t.missingTokens.concat(t.contrastIssues.map((c) => `${c.foreground} on ${c.background} ${c.ratio}:1`)).join(', ')}` : '')}">${h(t.name)}${notes ? ' ⚠' : ''}</option>`;
  };
  const builtIn = themeList.filter((t) => t.builtIn);
  const files = themeList.filter((t) => !t.builtIn);
  return builtIn.map(option).join('') + (files.length ? `<optgroup label="Theme files">${files.map(option).join('')}</optgroup>` : '');
}
export function initTheme() {
  const select = $('#theme-select');
  let saved = null;
  try { saved = localStorage.getItem(THEME_KEY); } catch { /* storage unavailable */ }
  // A saved theme that is not built in is a theme file: load it right away, before the list arrives.
  const theme = isThemeId(saved) ? saved : THEMES[0].id;
  if (!themeList.some((t) => t.id === theme)) themeList.push({ id: theme, name: theme, builtIn: false, missingTokens: [], contrastIssues: [] });
  select.innerHTML = themeOptions(theme);
  applyTheme(theme);
  select.addEventListener('change', () => applyTheme(select.value));
}
/** Adds the theme files from the server to the picker (after sign-in, since the list is an API call). */
export async function loadThemes() {
  let data;
  try { data = await api('GET', '/themes'); } catch { return; }
  themeList = data.themes;
  const current = document.documentElement.getAttribute('data-theme');
  if (!themeList.some((t) => t.id === current)) applyTheme(THEMES[0].id);
  $('#theme-select').innerHTML = themeOptions(document.documentElement.getAttribute('data-theme'));
  themeList.filter((t) => t.problem || t.missingTokens.length).forEach((t) => console.info(`Theme ${t.id}: ${t.problem || 'missing ' + t.missingTokens.join(', ')}`));
}
export function applyTheme(id) {
  if (!isThemeId(id)) id = THEMES[0].id;
  const builtIn = THEMES.some((t) => t.id === id);
  let link = document.getElementById('theme-file');
  if (!builtIn) {
    if (!link) { link = document.createElement('link'); link.id = 'theme-file'; link.rel = 'stylesheet'; document.head.appendChild(link); }
    link.href = `${PREFIX}/themes/${id}.css`;
  } else if (link) {
    link.remove();
  }
  document.documentElement.setAttribute('data-theme', id);
  try { localStorage.setItem(THEME_KEY, id); } catch { /* ignore */ }
  if (typeof readPalette === 'function') { readPalette(); if (graph) graph.refreshColors(); }
}


/* ---------------------------------------------------------------------------------------------
   Rendering and routing
   --------------------------------------------------------------------------------------------- */

/* The problems Validate or Apply found, each a link to its field when the router said which one. */
function problemsCard() {
  if (!state.errors) return '';
  const issues = state.issues && state.issues.length ? state.issues : null;
  const items = issues
    ? issues.map((x, i) => `<li>${x.path || x.selector ? `<a href="#/${h(x.page || '')}" data-action="issue-go" data-index="${i}">${h(x.message)}</a>${x.path ? ` <span class="faint small mono">${h(x.path)}</span>` : ''}` : h(x.message)}</li>`)
    : state.errors.map((e) => `<li>${h(e)}</li>`);
  return `<div class="card section problems" id="problems"><h3 id="problems-title" tabindex="-1">Configuration problems</h3><ul class="errors">${items.join('')}</ul></div>`;
}

/* The input for a configuration path: data-bind, or data-path on the table rows and renamable names. */
export function fieldFor(root, path) {
  if (!path) return null;
  const q = CSS.escape(path);
  const direct = root.querySelector(`[data-bind="${q}"], [data-path="${q}"]`);
  if (direct) return direct;
  const [head, name, more] = path.split('.');
  if (!more && head === 'Connectors') return root.querySelector(`[data-action-input="connector-rename"][data-name="${CSS.escape(name)}"]`);
  if (!more && head === 'Profiles') return root.querySelector(`[data-action-input="profile-rename"][data-name="${CSS.escape(name)}"]`);
  return null;
}

/* Opens the expandable item that holds an element (or a path's item), keeping that in state.open. */
function openItem(root, el, path) {
  const item = el ? el.closest('.item') : (itemKeyForPath(path) ? root.querySelector(`.item[data-key="${CSS.escape(itemKeyForPath(path))}"]`) : null);
  if (!item || item.classList.contains('open')) return;
  state.open.add(item.dataset.key);
  item.classList.add('open');
  const head = item.querySelector('.item-head');
  if (head) head.setAttribute('aria-expanded', 'true');
}

/* Each problem next to its field: marked invalid, with the message linked by aria-describedby, its item opened. */
function markIssues(root) {
  (state.issues || []).forEach((x, i) => {
    const el = x.selector ? root.querySelector(x.selector) : fieldFor(root, x.path);
    if (!el) { if (x.path) openItem(root, null, x.path); return; }
    openItem(root, el);
    const marked = el.getAttribute('aria-invalid') === 'true'; // its page marked it already (a duplicate alias row)
    el.dataset.issue = x.path || x.selector;
    if (marked) return;
    const id = 'issue-' + i;
    el.setAttribute('aria-invalid', 'true');
    el.setAttribute('aria-describedby', `${el.getAttribute('aria-describedby') || ''} ${id}`.trim());
    const msg = document.createElement('span');
    msg.className = 'field-error'; msg.id = id; msg.textContent = x.message;
    (el.closest('.field, .check, td') || el.parentElement).appendChild(msg);
  });
}

/* Read-only role: forms are shown, not editable; add, remove and other changes are hidden; spending is disabled. */
const EDIT_ACTION = /(^|-)(add|remove|duplicate|customize|defaults|up|down|rollback|purge)$|^(alias-claude|secret-change|secret-clear|json-use|config-reset|config-save|config-revert|embeddings-threshold|eval-use-calibration|eval-save|reset-health|reliability-reset|memory-reset|sessions-revoke-all)$/;
const SPEND_ACTION = /^(tester-send|tester-classify|profiles-compare|eval-run|eval-compare|embeddings-test|endpoint-check|discover|lineup-discover)$/;
function readOnlyForms(root) {
  if (!readOnly()) return;
  $$('[data-bind], [data-action-input]', root).forEach((el) => { el.disabled = true; });
  const json = $('#json-editor', root); if (json) json.readOnly = true; // still selectable, for copying
  $$('[data-action]', root).forEach((el) => {
    const a = el.dataset.action;
    if (EDIT_ACTION.test(a)) el.classList.add('hidden');
    else if (SPEND_ACTION.test(a) && !el.disabled) { el.disabled = true; el.title = 'Needs the admin token'; }
  });
}

/* A button whose action is still running: disabled and marked busy, so it is not sent twice. */
export function setBusy(el, on) {
  if (on) {
    el.setAttribute('aria-busy', 'true');
    if ('disabled' in el && !el.disabled) { el.disabled = true; el.dataset.busyDisabled = '1'; }
  } else {
    el.removeAttribute('aria-busy');
    if (el.dataset.busyDisabled) { el.disabled = false; delete el.dataset.busyDisabled; }
  }
}

/* After a render: the focus a step asked for (a problem, a new item's name, the next item after a removal). */
function placeFocus(root) {
  const go = (el, select) => {
    if (!el) return false;
    el.focus({ preventScroll: true });
    el.scrollIntoView({ block: 'center' });
    if (select && el.select && /^(text|search|url)$/.test(el.type)) el.select();
    return true;
  };
  if (state.focusPath) {
    const issue = (state.issues || []).find((x) => (x.path || x.selector) === state.focusPath);
    const el = issue && issue.selector ? root.querySelector(issue.selector) : fieldFor(root, state.focusPath);
    state.focusPath = null;
    if (go(el)) return true;
  }
  if (state.focusIssue) {
    state.focusIssue = false;
    const first = $$('[aria-invalid="true"]', root).find((el) => el.getClientRects().length);
    if (go(first || $('#problems-title', root))) return true;
  }
  if (state.focusAfter) {
    const selectors = state.focusAfter;
    state.focusAfter = null;
    for (const sel of selectors) {
      let el = null;
      try { el = root.querySelector(sel); } catch { /* not a selector */ }
      if (el) {
        if (el.matches('h1')) el.setAttribute('tabindex', '-1');
        openItem(root, el.closest('.item-body') ? el : null);
        return go(el, true);
      }
    }
  }
  return false;
}

/* The <details> open on the page, so a refresh does not fold them away ("Show as a table" on the Overview). */
let lastView = null;
const detailKeys = (root) => {
  const seen = {};
  return $$('details', root).map((d) => { const s = (d.querySelector('summary') || d).textContent.trim(); seen[s] = (seen[s] || 0) + 1; return s + '#' + seen[s]; });
};

export function render() {
  const staleTip = $('.chart-tip'); if (staleTip) staleTip.classList.add('hidden');
  const view = views[state.view] || views.overview;
  const root = $('#view');
  const typing = focusedField(root);
  const openDetails = lastView === state.view ? new Set(detailKeys(root).filter((k, i) => $$('details', root)[i].open)) : null;
  unmount();
  if (!state.config || !state.status) { root.innerHTML = '<div class="muted">Loading…</div>'; return; }
  if (state.view === 'activity') state.activityNew = 0;
  try {
    root.innerHTML = problemsCard() + view();
    if (mounts[state.view]) mounts[state.view]();
    if (openDetails && openDetails.size) { const all = $$('details', root); detailKeys(root).forEach((k, i) => { if (openDetails.has(k)) all[i].open = true; }); }
    markIssues(root);
    readOnlyForms(root);
    scrollRegions(root);
    $$('[data-action]', root).forEach((el) => { if (state.busy.has(actionKey(el))) setBusy(el, true); });
    if (!placeFocus(root)) refocus(root, typing);
  } catch (e) {
    root.innerHTML = `<div class="card"><h3>Could not render</h3><pre>${h(e.stack || e)}</pre></div>`;
  }
  lastView = state.view;
  root.classList.toggle('wide', state.view === 'memory' || state.view === 'live');
  root.classList.toggle('read-only', readOnly());
  renderChrome();
}

/*
 * A render replaces every element, and the status refresh renders the Overview every few seconds: without this, a
 * refresh took the focus from the field being typed in (or the button just used), and the rest of what was typed
 * went nowhere.
 */
const ACTION_KEYS = ['id', 'index', 'name', 'key', 'connector', 'model', 'window', 'value', 'type'];
function focusedField(root) {
  const el = document.activeElement;
  if (!el || el === root || !root.contains(el)) return null;
  const attr = (name, value) => `[${name}="${CSS.escape(value)}"]`;
  let key = null;
  if (el.id) key = '#' + CSS.escape(el.id);
  else if (el.dataset.bind) key = attr('data-bind', el.dataset.bind);
  else if (el.dataset.state) key = attr('data-state', el.dataset.state);
  else if (el.dataset.actionInput) key = attr('data-action-input', el.dataset.actionInput) + ['index', 'name'].filter((k) => el.dataset[k] != null).map((k) => attr('data-' + k, el.dataset[k])).join('');
  else if (el.dataset.action) key = attr('data-action', el.dataset.action) + ACTION_KEYS.filter((k) => el.dataset[k] != null).map((k) => attr('data-' + k, el.dataset[k])).join('');
  if (!key) return null;
  let start = null; let end = null;
  try { start = el.selectionStart; end = el.selectionEnd; } catch { /* not a text field */ }
  return { key, start, end, scrollTop: el.scrollTop };
}
function refocus(root, field) {
  let el = null;
  try { el = field && root.querySelector(field.key); } catch { /* a key that is no longer a valid selector */ }
  if (!el) return;
  el.focus({ preventScroll: true });
  try { if (field.start != null) el.setSelectionRange(field.start, field.end); } catch { /* not a text field */ }
  el.scrollTop = field.scrollTop;
}

export function route() {
  const hash = location.hash.replace(/^#\/?/, '') || 'overview';
  state.view = views[hash] && (!metricsOnly() || METRICS_VIEWS.includes(hash)) ? hash : 'overview';
  if (state.view === 'models') state.reliability = null; // fresh figures each visit
  if (state.view === 'audit') state.audit.entries = null;
  render();
  if (!$('#view').contains(document.activeElement) || document.activeElement === $('#view')) window.scrollTo(0, 0);
}
