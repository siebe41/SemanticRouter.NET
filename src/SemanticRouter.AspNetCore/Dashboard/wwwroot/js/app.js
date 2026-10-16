/* Semantic Router dashboard: Entry point: event wiring and start-up. */
// Each page module registers its view; importing it is all that is needed.
import './overview.js';
import './config.js';
import './usage.js';
import './evaluate.js';
import './profiles.js';
import './learned.js';
import './audit.js';
import { actions } from './actions.js';
import { $, api, h, isDirty, metricsOnly, readInput, setPath, showSignIn, state, toast } from './core.js';
import { connectStream } from './live.js';
import { graph } from './memory.js';
import { initTheme, loadAll, refreshStatus, render, renderChrome, route } from './shell.js';

/* ---------------------------------------------------------------------------------------------
   Event wiring
   --------------------------------------------------------------------------------------------- */
document.addEventListener('click', (ev) => {
  const el = ev.target.closest('[data-action]');
  if (!el) return;
  // A control inside a clickable header (the model's "enabled" box) does not also toggle the header.
  const own = ev.target.closest('[data-no-action]');
  if (own && el.contains(own)) return;
  const action = actions[el.dataset.action];
  if (!action) return;
  if (el.tagName === 'INPUT' && el.type === 'checkbox') return;
  ev.preventDefault();
  Promise.resolve(action(el)).catch((e) => toast(e.message, 'danger'));
});

const bindState = (el) => { setPath(state, el.dataset.state, readInput(el)); };
document.addEventListener('input', (ev) => {
  const el = ev.target;
  if (el.dataset.state && el.tagName !== 'SELECT' && el.type !== 'checkbox') { bindState(el); return; }
  if (el.dataset.bind) {
    setPath(state.config, el.dataset.bind, readInput(el));
    if (el.dataset.kind === 'lines' && el.dataset.nullable && !el.value.trim()) setPath(state.config, el.dataset.bind, null);
    $('#save-bar').classList.toggle('hidden', !isDirty());
    return;
  }
  if (el.dataset.actionInput === 'alias-key' || el.dataset.actionInput === 'alias-value') {
    const entries = Object.entries(state.config.Middleware.ModelAliases || {});
    const i = +el.dataset.index;
    if (el.dataset.actionInput === 'alias-key') entries[i][0] = el.value; else entries[i][1] = el.value;
    state.config.Middleware.ModelAliases = Object.fromEntries(entries);
    $('#save-bar').classList.toggle('hidden', !isDirty());
  }
  if (el.dataset.actionInput && el.dataset.actionInput.startsWith('ratelimit-')) {
    const entries = Object.entries(state.config.Middleware.RateLimits || {});
    const i = +el.dataset.index;
    const value = el.value.trim() === '' ? null : Math.max(1, Math.round(+el.value));
    if (el.dataset.actionInput === 'ratelimit-key') entries[i][0] = el.value.trim();
    else if (el.dataset.actionInput === 'ratelimit-rpm') entries[i][1] = { ...entries[i][1], RequestsPerMinute: value };
    else entries[i][1] = { ...entries[i][1], TokensPerMinute: value };
    state.config.Middleware.RateLimits = Object.fromEntries(entries);
    $('#save-bar').classList.toggle('hidden', !isDirty());
  }
  if (el.id === 'tester-prompt') state.tester.prompt = el.value;
  if (el.id === 'memory-search') { state.memoryUi.search = el.value; if (graph) graph.redraw(); }
});

document.addEventListener('change', (ev) => {
  const el = ev.target;
  if (el.dataset.state) { bindState(el); if (el.tagName === 'SELECT' || el.type === 'checkbox') render(); return; }
  if (el.dataset.bind) {
    setPath(state.config, el.dataset.bind, readInput(el));
    // Structural selects (connector type, preset, embeddings provider, enabled) change what is shown.
    if (/\.(Type|Preset|Provider|Enabled)$/.test(el.dataset.bind) || el.dataset.bind === 'Embeddings.Provider') render();
    else $('#save-bar').classList.toggle('hidden', !isDirty());
  } else if (el.dataset.actionInput === 'connector-rename') {
    const from = el.dataset.name; const to = el.value.trim();
    if (!to || to === from || state.config.Connectors[to]) { el.value = from; return; }
    const entries = Object.entries(state.config.Connectors).map(([k, v]) => [k === from ? to : k, v]);
    state.config.Connectors = Object.fromEntries(entries);
    (state.config.Models || []).forEach((m) => { if ((m.Connector || '').toLowerCase() === from.toLowerCase()) m.Connector = to; });
    if (state.open.has('conn:' + from)) { state.open.delete('conn:' + from); state.open.add('conn:' + to); }
    render();
  } else if (el.dataset.actionInput === 'profile-rename') {
    const from = el.dataset.name; const to = el.value.trim();
    if (!to || to === from || state.config.Profiles[to] || /[@.]/.test(to)) { el.value = from; return; }
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
    if (!to || to === from || v[to] || /[@.]/.test(to)) { el.value = from; return; }
    state.config.Middleware.VirtualModels = Object.fromEntries(Object.entries(v).map(([k, x]) => [k === from ? to : k, x]));
    render();
  } else if (el.id === 'activity-filter') { state.activityFilter = el.value; render(); }
  else if (el.id === 'auto-refresh') { state.autoRefresh = el.checked; }
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
document.addEventListener('click', (ev) => {
  if (document.body.classList.contains('nav-open') && !ev.target.closest('#sidebar') && !ev.target.closest('#nav-toggle')) setNavOpen(false);
});
window.addEventListener('hashchange', () => {
  route();
  if (document.body.classList.contains('nav-open')) setNavOpen(false);
  $('#view').focus({ preventScroll: true });
});
// Keyboard: Enter or Space operates the expandable item headers (role="button"); Escape closes the drawer.
document.addEventListener('keydown', (ev) => {
  if (ev.key === 'Escape' && document.body.classList.contains('nav-open')) { setNavOpen(false); $('#nav-toggle').focus(); return; }
  const el = ev.target;
  if ((ev.key === 'Enter' || ev.key === ' ') && el.getAttribute && el.getAttribute('role') === 'button' && el.dataset.action && ev.target === ev.currentTarget.activeElement) {
    ev.preventDefault();
    el.click();
  }
});
window.addEventListener('beforeunload', (ev) => { if (isDirty()) { ev.preventDefault(); ev.returnValue = ''; } });

initTheme();
const signOutBtn = $('#sign-out');
if (signOutBtn) signOutBtn.addEventListener('click', async () => { await api('DELETE', '/session').catch(() => {}); location.reload(); });
loadAll().then(() => { route(); if (!metricsOnly()) connectStream(); }).catch((e) => {
  if (e.status === 401) { showSignIn(); return; }
  $('#view').innerHTML = `<div class="card"><h3>Could not reach the router API</h3><pre>${h(e.message)}</pre><p class="muted small">Is the dashboard mapped (<code>app.MapRouterDashboard()</code>) and are you browsing from the same machine?</p></div>`; });
setInterval(() => { if (state.autoRefresh && document.visibilityState === 'visible' && !isDirty()) refreshStatus(); else if (state.autoRefresh && document.visibilityState === 'visible') { api('GET', '/status').then((s) => { state.status = s; renderChrome(); }).catch(() => {}); } }, 5000);
