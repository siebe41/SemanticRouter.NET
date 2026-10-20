/* Semantic Router dashboard: Entry point: event wiring and start-up. */
// Each page module registers its view; importing it is all that is needed.
import './overview.js';
import './config.js';
import './usage.js';
import './evaluate.js';
import './profiles.js';
import './learned.js';
import './audit.js';
import './desktop.js';
import { reportActions } from './feedback.js';
import { serviceActions } from './service.js';
import { actions } from './actions.js';
import { $, $$, actionKey, api, evaluationEdited, h, isDirty, jsonEdited, metricsOnly, readInput, readOnly, scrollRegions, setPath, showSignIn, state, toast } from './core.js';
import { aliasRows, limitRows, writeAliases, writeLimits } from './draft.js';
import { connectStream } from './live.js';
import { graph } from './memory.js';
import { initTheme, loadAll, refreshChrome, refreshStatus, render, route, setBusy, updateSaveBar } from './shell.js';

/* ---------------------------------------------------------------------------------------------
   Event wiring
   --------------------------------------------------------------------------------------------- */

/* "Couldn't apply: …": what failed, in the button's own words when they are short. */
function failed(el, e) {
  const label = (el.getAttribute('aria-label') || el.textContent || '').trim().replace(/…$/, '');
  const what = /^[A-Za-z][\w &'-]{0,28}$/.test(label) ? label.toLowerCase() : 'do that';
  const message = (e && e.message) || String(e);
  return /^(couldn.t|not )/i.test(message) ? message : `Couldn't ${what}: ${message}`;
}

/*
 * One handler for every data-action. While an action's promise is pending its button is disabled and marked busy
 * (and stays so across renders, by its key), so Apply, Save set, Discover and the like are not sent twice.
 */
document.addEventListener('click', (ev) => {
  const el = ev.target.closest('[data-action]');
  if (!el) return;
  // A control inside a clickable header (the model's "enabled" box) does not also toggle the header.
  const own = ev.target.closest('[data-no-action]');
  if (own && el.contains(own)) return;
  const action = actions[el.dataset.action] || reportActions[el.dataset.action] || serviceActions[el.dataset.action];
  if (!action) return;
  if (el.tagName === 'INPUT' && el.type === 'checkbox') return;
  ev.preventDefault();
  const key = actionKey(el);
  if (state.busy.has(key)) return;
  let result;
  try { result = action(el); } catch (e) { toast(failed(el, e), 'danger'); return; }
  if (!result || typeof result.then !== 'function') return;
  state.busy.add(key);
  setBusy(el, true);
  result.catch((e) => { if (e.status !== 401) toast(failed(el, e), 'danger'); }).finally(() => {
    state.busy.delete(key);
    setBusy(el, false);
    $$('[aria-busy="true"][data-action]').forEach((x) => { if (actionKey(x) === key) setBusy(x, false); });
  });
});

const bindState = (el) => { setPath(state, el.dataset.state, readInput(el)); };

/* Typing in a field the last Validate or Apply flagged: the flag goes, the problem stays listed until the next check. */
function clearIssue(el) {
  if (el.getAttribute('aria-invalid') !== 'true' || !el.dataset.issue) return;
  el.removeAttribute('aria-invalid');
  const id = (el.getAttribute('aria-describedby') || '').split(' ').find((x) => x.startsWith('issue-'));
  if (id) { const msg = document.getElementById(id); if (msg) msg.remove(); }
  state.issues = (state.issues || []).filter((x) => (x.path || x.selector) !== el.dataset.issue);
  delete el.dataset.issue;
}

document.addEventListener('input', (ev) => {
  const el = ev.target;
  clearIssue(el);
  if (el.dataset.state && el.tagName !== 'SELECT' && el.type !== 'checkbox') { bindState(el); return; }
  if (el.dataset.bind) {
    setPath(state.config, el.dataset.bind, readInput(el));
    if (el.dataset.kind === 'lines' && el.dataset.nullable && !el.value.trim()) setPath(state.config, el.dataset.bind, null);
    updateSaveBar();
    return;
  }
  // The alias and rate-limit tables edit rows; the configuration's map follows (draft.js).
  if (el.dataset.actionInput === 'alias-key' || el.dataset.actionInput === 'alias-value') {
    const row = aliasRows()[+el.dataset.index];
    if (el.dataset.actionInput === 'alias-key') row.key = el.value; else row.value = el.value;
    writeAliases();
    updateSaveBar();
  }
  if (el.dataset.actionInput && el.dataset.actionInput.startsWith('ratelimit-')) {
    const row = limitRows()[+el.dataset.index];
    const value = el.value.trim() === '' ? null : Math.max(1, Math.round(+el.value));
    if (el.dataset.actionInput === 'ratelimit-key') row.key = el.value;
    else if (el.dataset.actionInput === 'ratelimit-rpm') row.value = { ...row.value, RequestsPerMinute: value };
    else row.value = { ...row.value, TokensPerMinute: value };
    writeLimits();
    updateSaveBar();
  }
  if (el.id === 'tester-prompt') state.tester.prompt = el.value;
  if (el.id === 'tester-model') state.tester.model = el.value; // kept, so a refresh before Classify does not reset it
  if (el.id === 'memory-search') { state.memoryUi.search = el.value; if (graph) graph.redraw(); }
});

/* A rename the page refuses: the name goes back, and a toast says why. */
function refuse(el, from, why) {
  el.value = from;
  toast(why, 'danger');
}

document.addEventListener('change', (ev) => {
  const el = ev.target;
  if (el.dataset.state) { bindState(el); if (el.tagName === 'SELECT' || el.type === 'checkbox') render(); return; }
  if (el.dataset.bind) {
    setPath(state.config, el.dataset.bind, readInput(el));
    // Structural selects (connector type, preset, embeddings provider, enabled) change what is shown.
    if (/\.(Type|Preset|Provider|Enabled)$/.test(el.dataset.bind) || el.dataset.bind === 'Embeddings.Provider') render();
    else updateSaveBar();
  } else if (el.dataset.actionInput === 'alias-key' || el.dataset.actionInput === 'ratelimit-key') {
    render(); // a duplicate or empty name is flagged on the row
  } else if (el.dataset.actionInput === 'connector-rename') {
    const from = el.dataset.name; const to = el.value.trim();
    if (!to || to === from) { el.value = from; return; }
    if (state.config.Connectors[to]) { refuse(el, from, `There is already a connector named ${to}.`); return; }
    const entries = Object.entries(state.config.Connectors).map(([k, v]) => [k === from ? to : k, v]);
    state.config.Connectors = Object.fromEntries(entries);
    (state.config.Models || []).forEach((m) => { if ((m.Connector || '').toLowerCase() === from.toLowerCase()) m.Connector = to; });
    if (state.open.has('conn:' + from)) { state.open.delete('conn:' + from); state.open.add('conn:' + to); }
    render();
  } else if (el.dataset.actionInput === 'profile-rename') {
    const from = el.dataset.name; const to = el.value.trim();
    if (!to || to === from) { el.value = from; return; }
    if (state.config.Profiles[to]) { refuse(el, from, `There is already a profile named ${to}.`); return; }
    if (/[@.]/.test(to)) { refuse(el, from, 'A profile name cannot contain @ or a dot: clients ask for auto@profile.'); return; }
    state.config.Profiles = Object.fromEntries(Object.entries(state.config.Profiles).map(([k, v]) => [k === from ? to : k, v]));
    const m = state.config.Middleware;
    Object.keys(m.ClientProfiles || {}).forEach((k) => { if (m.ClientProfiles[k] === from) m.ClientProfiles[k] = to; });
    (m.ProfileRules || []).forEach((r) => { if (r.Profile === from) r.Profile = to; });
    Object.values(m.VirtualModels || {}).forEach((v) => { if (v.Profile === from) v.Profile = to; });
    ((state.config.Usage || {}).Budgets || []).forEach((b) => { if (b.Profile === from) b.Profile = to; });
    if (state.open.has('profile:' + from)) { state.open.delete('profile:' + from); state.open.add('profile:' + to); }
    render();
  } else if (el.dataset.actionInput === 'virtual-rename') {
    const from = el.dataset.name; const to = el.value.trim(); const v = state.config.Middleware.VirtualModels;
    if (!to || to === from) { el.value = from; return; }
    if (v[to]) { refuse(el, from, `There is already a virtual model named ${to}.`); return; }
    if (/[@.]/.test(to)) { refuse(el, from, 'A virtual model name cannot contain @ or a dot.'); return; }
    state.config.Middleware.VirtualModels = Object.fromEntries(Object.entries(v).map(([k, x]) => [k === from ? to : k, x]));
    render();
  } else if (el.id === 'activity-filter') { state.activityFilter = el.value; render(); }
  else if (el.id === 'auto-refresh') { state.autoRefresh = el.checked; if (el.checked && state.activityNew) render(); }
  else if (el.id === 'audit-limit') { state.audit.limit = +el.value; state.audit.entries = null; render(); }
  else if (el.id === 'tester-model') { state.tester.model = el.value; }
  else if (el.id === 'memory-filter') { state.memoryUi.filter = el.value; if (graph) graph.redraw(); }
  else if (el.id === 'memory-color') { state.memoryUi.colorBy = el.value; render(); }
  else if (el.id === 'memory-missing') { state.memoryUi.showMissing = el.checked; if (graph) graph.redraw(); }
});

// Chart tooltips: any element with data-tip.
const tip = document.createElement('div'); tip.className = 'chart-tip hidden'; tip.setAttribute('role', 'tooltip'); document.body.appendChild(tip);
document.addEventListener('mousemove', (ev) => {
  const el = ev.target.closest && ev.target.closest('[data-tip]');
  if (!el) { tip.classList.add('hidden'); return; }
  tip.textContent = el.getAttribute('data-tip').replace(/\\n/g, '\n');
  tip.classList.remove('hidden');
  tip.style.left = Math.min(window.innerWidth - tip.offsetWidth - 8, ev.clientX + 12) + 'px';
  tip.style.top = (ev.clientY + 14) + 'px';
});

// Navigation: a new page moves focus to its content (screen readers announce it) and closes the drawer.
function setNavOpen(open) {
  document.body.classList.toggle('nav-open', open);
  const button = $('#nav-toggle');
  button.setAttribute('aria-expanded', String(open));
  button.setAttribute('aria-label', open ? 'Hide navigation' : 'Show navigation');
  if (open) { const current = $('#nav a.active') || $('#nav a'); if (current) current.focus(); }
}
$('#nav-toggle').addEventListener('click', () => setNavOpen(!document.body.classList.contains('nav-open')));
// More, at the foot of the rail: the router's version, the theme picker and Sign out.
const moreOpen = () => !$('#more').classList.contains('hidden');
function setMoreOpen(open) {
  $('#more').classList.toggle('hidden', !open);
  $('#more-toggle').setAttribute('aria-expanded', String(open));
}
$('#more-toggle').addEventListener('click', () => setMoreOpen(!moreOpen()));
document.addEventListener('click', (ev) => {
  if (moreOpen() && !ev.target.closest('#more') && !ev.target.closest('#more-toggle')) setMoreOpen(false);
  if (document.body.classList.contains('nav-open') && !ev.target.closest('#sidebar') && !ev.target.closest('#nav-toggle')) setNavOpen(false);
});
window.addEventListener('hashchange', () => {
  route();
  setMoreOpen(false);
  if (document.body.classList.contains('nav-open')) setNavOpen(false);
  // Unless the page put the focus on something itself (a problem's field), it goes to the page's content.
  const view = $('#view');
  if (!view.contains(document.activeElement) || document.activeElement === view) view.focus({ preventScroll: true });
});
// Keyboard: Enter or Space operates the expandable item headers (role="button"); Escape closes More, then the drawer.
document.addEventListener('keydown', (ev) => {
  if (ev.key === 'Escape' && moreOpen()) { setMoreOpen(false); $('#more-toggle').focus(); return; }
  if (ev.key === 'Escape' && document.body.classList.contains('nav-open')) { setNavOpen(false); $('#nav-toggle').focus(); return; }
  const el = ev.target;
  if ((ev.key === 'Enter' || ev.key === ' ') && el.getAttribute && el.getAttribute('role') === 'button' && el.dataset.action && ev.target === ev.currentTarget.activeElement) {
    ev.preventDefault();
    el.click();
  }
});
// A table that fits the window may scroll once it is narrower, and the other way round.
let resized;
window.addEventListener('resize', () => { clearTimeout(resized); resized = setTimeout(() => scrollRegions($('#view')), 150); });
// Leaving with unapplied edits, or typed text the router does not have yet, asks first (not for the read-only role).
window.addEventListener('beforeunload', (ev) => { if (!readOnly() && (isDirty() || jsonEdited() || evaluationEdited())) { ev.preventDefault(); ev.returnValue = ''; } });

initTheme();
const signOutBtn = $('#sign-out');
if (signOutBtn) signOutBtn.addEventListener('click', async () => { await api('DELETE', '/session').catch(() => {}); location.reload(); });
loadAll().then(() => { route(); if (!metricsOnly()) connectStream(); }).catch((e) => {
  if (e.status === 401) { showSignIn(); return; }
  $('#view').innerHTML = `<div class="card"><h3>Could not reach the router API</h3><pre>${h(e.message)}</pre><p class="muted small">Is the dashboard mapped (<code>app.MapRouterDashboard()</code>) and are you browsing from the same machine?</p></div>`; });
// Every 5 s: with unapplied edits only the top bar (and the check that nobody changed the configuration meanwhile).
setInterval(() => {
  if (!state.autoRefresh || document.visibilityState !== 'visible' || $('#signin-form')) return;
  if (isDirty()) refreshChrome(); else refreshStatus();
}, 5000);
