/* Semantic Router dashboard: Loading, the sidebar and top bar, themes, rendering and hash routing. */
import { $, $$, api, banner, h, isDirty, PREFIX, readOnly, state, THEME_KEY, THEMES, views } from './core.js';
import { loadDigest } from './digest.js';
import { endpointPill } from './endpoints.js';
import { loadTuning } from './learned.js';
import { scheduleLive } from './live.js';
import { graph, mounts, readPalette, unmount } from './memory.js';
import { loadUsage } from './usage.js';

/* ---------------------------------------------------------------------------------------------
   Data loading
   --------------------------------------------------------------------------------------------- */
export async function loadAll() {
  try { state.session = await api('GET', '/session'); } catch { /* older router: no session endpoint */ }
  const [status, cfg, activity] = await Promise.all([api('GET', '/status'), api('GET', '/config'), api('GET', '/activity?limit=200')]);
  state.status = status;
  setConfig(cfg);
  state.activity = activity.entries;
  state.feedback = activity.feedback || {};
  await Promise.all([loadTuning(), loadDigest()]);
  renderChrome();
  loadThemes();
}

export function setConfig(envelope) {
  state.config = envelope.config;
  state.original = JSON.stringify(envelope.config);
  state.meta = { version: envelope.version, source: envelope.source, store: envelope.store, secretStore: envelope.secretStore, warning: envelope.warning, createdAt: envelope.createdAt };
  state.errors = null;
}

export async function refreshStatus() {
  try {
    const [status, activity] = await Promise.all([api('GET', '/status'), api('GET', '/activity?limit=200')]);
    state.status = status;
    state.activity = activity.entries;
    state.feedback = activity.feedback || {};
    if (state.view === 'overview' || state.view === 'learned') await loadTuning();
    if (state.view === 'overview') await loadDigest();
    renderChrome();
    if (state.view === 'overview') { state.trends.data = null; }
    if (state.view === 'usage') { loadUsage(); }
    if (state.view === 'overview' || state.view === 'learned' || (state.view === 'activity' && !state.label)) render();
    else if (state.view === 'live') scheduleLive();
  } catch (e) { /* keep the last known state */ }
}

/* ---------------------------------------------------------------------------------------------
   Chrome (sidebar, top bar)
   --------------------------------------------------------------------------------------------- */
export function renderChrome() {
  const s = state.status;
  if (s) {
    $('#brand-sub').textContent = `v${s.version} · ${s.source.toLowerCase()}${s.machine ? ' · ' + s.machine : ''}`;
    $('#brand-sub').title = s.machine ? `Running on ${s.machine}` : '';
    $('#lib-version').textContent = 'SemanticRouter ' + s.libraryVersion;
    const cooling = s.models.filter((m) => m.coolingDownUntil).length;
    const warm = s.warmedUp ? 'ok' : (s.warmUpError ? 'danger' : 'warn');
    const now = endpointPill();
    $('#status-pill').innerHTML =
      `<span class="dot ${warm}"></span> <span class="pill-detail">${h(s.gateway.mode)} mode · ${s.models.filter((m) => m.enabled).length} models${now ? ' ·' : ''}</span>` +
      (now ? ' ' + now : '') +
      (cooling ? ` · <span class="badge warn">${cooling} cooling down</span>` : '') +
      (s.warmedUp ? '' : (s.warmUpError ? ` · <span class="badge danger" title="${h(s.warmUpError)}">embeddings failed</span>` : ' · <span class="badge info">warming up</span>'));
  }
  $('#save-bar').classList.toggle('hidden', !isDirty() || readOnly());
  $$('#nav a').forEach((a) => { const on = a.dataset.view === state.view; a.classList.toggle('active', on); if (on) a.setAttribute('aria-current', 'page'); else a.removeAttribute('aria-current'); });
  const notes = [];
  if (readOnly()) notes.push('Signed in with the <b>read-only</b> token: you can look, not change.');
  if (state.meta.warning) notes.push(h(state.meta.warning));
  ((s && s.warnings) || []).forEach((w) => notes.push(h(w)));
  ((s && s.budgets) || []).filter((b) => b.exceeded || b.warning).forEach((b) => notes.push(`Budget <b>${h(b.name)}</b> (${h(b.covers)}) is ${b.exceeded ? 'spent' : 'at ' + b.percent + '%'}. <a href="#/usage">Usage</a>`));
  if (notes.length) banner(notes.join('<br>'), 'warn'); else banner(null);
  const signOut = $('#sign-out');
  if (signOut) signOut.classList.toggle('hidden', !state.session.role || !state.session.tokenRequired);
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
export function render() {
  const staleTip = $('.chart-tip'); if (staleTip) staleTip.classList.add('hidden');
  const view = views[state.view] || views.overview;
  const root = $('#view');
  unmount();
  if (!state.config || !state.status) { root.innerHTML = '<div class="muted">Loading…</div>'; return; }
  try {
    root.innerHTML = (state.errors ? `<div class="card section" style="border-color:var(--danger)"><h3>Configuration problems</h3><ul class="errors">${state.errors.map((e) => `<li>${h(e)}</li>`).join('')}</ul></div>` : '') + view();
    if (mounts[state.view]) mounts[state.view]();
  } catch (e) {
    root.innerHTML = `<div class="card"><h3>Could not render</h3><pre>${h(e.stack || e)}</pre></div>`;
  }
  root.classList.toggle('wide', state.view === 'memory' || state.view === 'live');
  renderChrome();
}

export function route() {
  const hash = location.hash.replace(/^#\/?/, '') || 'overview';
  state.view = views[hash] ? hash : 'overview';
  if (state.view === 'models') state.reliability = null; // fresh figures each visit
  render();
  window.scrollTo(0, 0);
}
