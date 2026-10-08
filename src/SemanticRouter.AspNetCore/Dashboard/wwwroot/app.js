/* Semantic Router dashboard. Plain JavaScript, no build step. Served by MapRouterDashboard(). */
(function () {
  'use strict';

  /* ---------------------------------------------------------------------------------------------
     Themes. Each entry needs a matching [data-theme] token block in app.css. The first is default.
     --------------------------------------------------------------------------------------------- */
  const THEMES = [
    { id: 'machine-dark', name: 'Machine Dark' },
    { id: 'machine-light', name: 'Machine Light' },
    { id: 'terminal', name: 'Terminal' },
  ];
  const THEME_KEY = 'semantic-router.theme';

  const PREFIX = window.ROUTER_PREFIX || '/router';
  const API = PREFIX + '/api';
  const CAPABILITY_HINTS = ['chat', 'code', 'tools', 'vision', 'reasoning'];
  const STRATEGIES = ['CheapestCapable', 'MostCapable', 'Priority'];
  const SCORING = ['MaxSimilarity', 'Centroid'];
  const CONNECTOR_TYPES = ['OpenAICompatible', 'Anthropic', 'Cli'];
  const PRESETS = ['ClaudeCode', 'GitHubCopilot', 'Codex', 'Custom'];
  const MESSAGE_SELECTION = ['LastUserMessage', 'AllUserMessages', 'AllMessages'];

  /* ---------------------------------------------------------------------------------------------
     State
     --------------------------------------------------------------------------------------------- */
  const state = {
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
    autoRefresh: true,
    session: { role: null, tokenRequired: false, tokensConfigured: false },
  };

  /* ---------------------------------------------------------------------------------------------
     Helpers
     --------------------------------------------------------------------------------------------- */
  const $ = (sel, root) => (root || document).querySelector(sel);
  const $$ = (sel, root) => Array.from((root || document).querySelectorAll(sel));
  const h = (s) => String(s == null ? '' : s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const num = (v, d) => (v == null || v === '' ? '' : (typeof v === 'number' ? Number(v.toFixed(d == null ? 3 : d)) : v));
  const when = (iso) => { const d = new Date(iso); return isNaN(d) ? '' : d.toLocaleTimeString(); };
  const isDirty = () => state.config && JSON.stringify(state.config) !== state.original;
  const readOnly = () => state.session.role === 'ReadOnly';
  const isReference = (v) => typeof v === 'string' && /\$\{(env|secret):[^}]+\}/i.test(v);
  const tierName = (level) => {
    const t = (state.status && state.status.tiers || []).find((x) => x.level === level);
    return t ? `${level} · ${t.name}` : String(level);
  };
  const tiers = () => (state.config && state.config.Tiers && state.config.Tiers.length ? state.config.Tiers : (state.status ? state.status.tiers.map((t) => ({ Level: t.level, Name: t.name, Description: t.description })) : []));
  const connectorNames = () => Object.keys((state.config && state.config.Connectors) || {});
  const lines = (arr) => (arr || []).join('\n');
  const fromLines = (text) => text.split('\n').map((s) => s.trim()).filter(Boolean);
  const csv = (arr) => (arr || []).join(', ');
  const fromCsv = (text) => text.split(',').map((s) => s.trim()).filter(Boolean);
  const kv = (obj) => Object.entries(obj || {}).map(([k, v]) => `${k}=${v}`).join('\n');
  const fromKv = (text) => { const o = {}; fromLines(text).forEach((l) => { const i = l.indexOf('='); if (i > 0) o[l.slice(0, i).trim()] = l.slice(i + 1).trim(); }); return o; };

  async function api(method, path, body) {
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

  let toastTimer = null;
  function toast(message, kind) {
    const el = $('#toast');
    el.textContent = message;
    el.className = 'toast ' + (kind || '');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => el.classList.add('hidden'), 4500);
  }

  function banner(message, kind) {
    const el = $('#banner');
    if (!message) { el.classList.add('hidden'); return; }
    el.innerHTML = message;
    el.className = 'banner ' + (kind || '');
  }

  /* Path-based binding: data-bind="Routes.0.Utterances" with data-kind="lines|csv|kv|number|float|bool|text|timespan". */
  function getPath(obj, path) {
    return path.split('.').reduce((o, k) => (o == null ? undefined : o[k]), obj);
  }
  function setPath(obj, path, value) {
    const keys = path.split('.');
    let o = obj;
    for (let i = 0; i < keys.length - 1; i++) {
      if (o[keys[i]] == null) o[keys[i]] = /^\d+$/.test(keys[i + 1]) ? [] : {};
      o = o[keys[i]];
    }
    o[keys[keys.length - 1]] = value;
  }
  function readInput(el) {
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
  function showSignIn(message) {
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
        await loadAll(); route();
      } catch (e) { showSignIn(e.message); }
    });
    const input = $('#signin-token'); if (input) input.focus();
  }

  /* ---------------------------------------------------------------------------------------------
     Data loading
     --------------------------------------------------------------------------------------------- */
  async function loadAll() {
    try { state.session = await api('GET', '/session'); } catch { /* older router: no session endpoint */ }
    const [status, cfg, activity] = await Promise.all([api('GET', '/status'), api('GET', '/config'), api('GET', '/activity?limit=200')]);
    state.status = status;
    setConfig(cfg);
    state.activity = activity.entries;
    renderChrome();
  }

  function setConfig(envelope) {
    state.config = envelope.config;
    state.original = JSON.stringify(envelope.config);
    state.meta = { version: envelope.version, source: envelope.source, store: envelope.store, secretStore: envelope.secretStore, warning: envelope.warning, createdAt: envelope.createdAt };
    state.errors = null;
  }

  async function refreshStatus() {
    try {
      const [status, activity] = await Promise.all([api('GET', '/status'), api('GET', '/activity?limit=200')]);
      state.status = status;
      state.activity = activity.entries;
      renderChrome();
      if (state.view === 'overview' || state.view === 'activity') render();
    } catch (e) { /* keep the last known state */ }
  }

  /* ---------------------------------------------------------------------------------------------
     Chrome (sidebar, top bar)
     --------------------------------------------------------------------------------------------- */
  function renderChrome() {
    const s = state.status;
    if (s) {
      $('#brand-sub').textContent = `v${s.version} · ${s.source.toLowerCase()}`;
      $('#lib-version').textContent = 'SemanticRouter ' + s.libraryVersion;
      const cooling = s.models.filter((m) => m.coolingDownUntil).length;
      const warm = s.warmedUp ? 'ok' : (s.warmUpError ? 'danger' : 'warn');
      $('#status-pill').innerHTML =
        `<span class="dot ${warm}"></span> ${h(s.gateway.mode)} mode · ${s.models.filter((m) => m.enabled).length} models` +
        (cooling ? ` · <span class="badge warn">${cooling} cooling down</span>` : '') +
        (s.warmedUp ? '' : (s.warmUpError ? ` · <span class="badge danger" title="${h(s.warmUpError)}">embeddings failed</span>` : ' · <span class="badge info">warming up</span>'));
    }
    $('#save-bar').classList.toggle('hidden', !isDirty() || readOnly());
    $$('#nav a').forEach((a) => a.classList.toggle('active', a.dataset.view === state.view));
    const notes = [];
    if (readOnly()) notes.push('Signed in with the <b>read-only</b> token: you can look, not change.');
    if (state.meta.warning) notes.push(h(state.meta.warning));
    ((s && s.warnings) || []).forEach((w) => notes.push(h(w)));
    if (notes.length) banner(notes.join('<br>'), 'warn'); else banner(null);
    const signOut = $('#sign-out');
    if (signOut) signOut.classList.toggle('hidden', !state.session.role || !state.session.tokenRequired);
  }

  function initTheme() {
    const select = $('#theme-select');
    select.innerHTML = THEMES.map((t) => `<option value="${t.id}">${h(t.name)}</option>`).join('');
    let saved = null;
    try { saved = localStorage.getItem(THEME_KEY); } catch { /* storage unavailable */ }
    const theme = THEMES.some((t) => t.id === saved) ? saved : THEMES[0].id;
    applyTheme(theme);
    select.value = theme;
    select.addEventListener('change', () => applyTheme(select.value));
  }
  function applyTheme(id) {
    document.documentElement.setAttribute('data-theme', id);
    try { localStorage.setItem(THEME_KEY, id); } catch { /* ignore */ }
  }

  /* ---------------------------------------------------------------------------------------------
     Views
     --------------------------------------------------------------------------------------------- */
  const views = {};

  views.overview = () => {
    const s = state.status;
    const a = s.activity;
    const byModel = Object.entries(a.byModel).sort((x, y) => y[1] - x[1]);
    const byTier = Object.entries(a.byTier);
    const max = Math.max(1, ...byModel.map((x) => x[1]), ...byTier.map((x) => x[1]));
    const bars = (rows, label, alt) => rows.length ? `<div class="bars">${rows.map(([k, v]) => `<div class="bar-row"><span class="nowrap" title="${h(k)}">${h(label ? label(k) : k)}</span><div class="bar${alt ? ' alt' : ''}"><i style="width:${Math.round(v / max * 100)}%"></i></div><span class="num">${v}</span></div>`).join('')}</div>` : '<div class="muted small">No requests yet.</div>';
    const t = state.tester;

    return `
      <div class="section-head"><h1>Overview</h1><span class="muted">What the router is doing right now.</span></div>
      <div class="grid cols-4 section">
        <div class="card stat"><span class="value">${a.total}</span><span class="label">routed (last ${a.total >= 500 ? '500' : a.total})</span></div>
        <div class="card stat"><span class="value">${a.served}</span><span class="label">served · ${a.failed} failed · ${a.degraded} degraded</span></div>
        <div class="card stat"><span class="value">${s.models.filter((m) => m.enabled).length}<span class="muted" style="font-size:14px"> / ${s.models.length}</span></span><span class="label">models enabled</span></div>
        <div class="card stat"><span class="value">${s.routes.length}</span><span class="label">routes · ${s.routes.reduce((n, r) => n + r.utterances, 0)} utterances</span></div>
      </div>

      <div class="grid cols-2 section">
        <div class="card">
          <div class="card-head"><h2>Models</h2><span class="spacer"></span>${s.models.some((m) => m.coolingDownUntil) ? '<button class="btn small" data-action="reset-health">Clear cooldowns</button>' : ''}</div>
          <table>
            <tr><th>Model</th><th>Tier</th><th>Connector</th><th>State</th></tr>
            ${s.models.map((m) => `<tr>
              <td><span class="${m.enabled ? '' : 'muted'}">${h(m.name)}</span>${m.isLocal ? ' <span class="badge info">local</span>' : ''}<div class="small faint mono">${h(m.model || '')}</div></td>
              <td><span class="tier" title="${h(tierName(m.tier))}">T${m.tier}</span></td>
              <td class="small">${h(m.connector)} <span class="faint">${h(m.connectorType || '')}</span></td>
              <td>${!m.enabled ? '<span class="badge">disabled</span>' : (m.coolingDownUntil ? `<span class="badge warn" title="until ${h(new Date(m.coolingDownUntil).toLocaleTimeString())}">cooling down</span>` : '<span class="badge ok">ready</span>')}</td>
            </tr>`).join('')}
          </table>
        </div>
        <div class="card">
          <div class="card-head"><h2>Runtime</h2></div>
          <table>
            <tr><th>Configuration</th><td>v${s.version} from <b>${h(s.source)}</b> · ${h(when(s.createdAt))}</td></tr>
            <tr><th>Store</th><td class="mono small">${s.store ? h(s.store) : '<span class="muted">none (changes apply in memory only)</span>'}</td></tr>
            <tr><th>Embeddings</th><td>${h(s.embeddings.provider)} ${s.embeddings.model ? '· <span class="mono">' + h(s.embeddings.model) + '</span>' : ''} ${s.embeddings.endpoint ? '<span class="faint small">' + h(s.embeddings.endpoint) + '</span>' : ''}
              ${s.warmedUp ? '<span class="badge ok">index ready</span>' : (s.warmUpError ? `<span class="badge danger" title="${h(s.warmUpError)}">warm-up failed</span>` : '<span class="badge info">warming up</span>')}</td></tr>
            <tr><th>Gateway</th><td>${h(s.gateway.mode)} · OpenAI on <span class="mono">${h(s.gateway.pathPrefixes.join(', ') || '(all JSON POSTs)')}</span> · Anthropic on <span class="mono">${h(s.gateway.anthropicPathPrefixes.join(', '))}</span></td></tr>
            <tr><th>Strategy</th><td>${h(s.gateway.strategy)}</td></tr>
            <tr><th>Connectors</th><td>${s.connectors.map((c) => `<span class="badge" title="${h(c.target || '')}">${h(c.name)} <span class="faint">${h(c.type)}${c.preset ? '/' + h(c.preset) : ''}</span></span>`).join(' ')}</td></tr>
          </table>
        </div>
      </div>

      <div class="grid cols-2 section">
        <div class="card"><div class="card-head"><h2>Served by</h2></div>${bars(byModel)}</div>
        <div class="card"><div class="card-head"><h2>Required tier</h2></div>${bars(byTier, (k) => tierName(parseInt(k, 10)), true)}</div>
      </div>

      <div class="card section tester">
        <div class="card-head"><h2>Try a prompt</h2><span class="muted small">Classification calls no model. “Send” goes through the gateway for real${s.gateway.mode !== 'Dispatch' ? ' (Annotate mode: your endpoint answers)' : ''}.</span></div>
        <textarea id="tester-prompt" placeholder="Paste a prompt…">${h(t.prompt)}</textarea>
        <div class="inline" style="margin-top:8px">
          <select id="tester-model" style="width:220px">
            ${['auto', 'auto:cheapest', 'auto:best', 'auto:priority'].concat(s.models.map((m) => m.name)).map((m) => `<option ${m === t.model ? 'selected' : ''}>${h(m)}</option>`).join('')}
          </select>
          <button class="btn primary" data-action="tester-classify" ${t.busy ? 'disabled' : ''}>Classify</button>
          <button class="btn" data-action="tester-send" ${t.busy ? 'disabled' : ''}>Send</button>
          ${t.busy ? '<span class="muted small">working…</span>' : ''}
        </div>
        ${t.result ? renderClassification(t.result) : ''}
        ${t.reply ? `<div class="result-line">${t.reply.headers.map(([k, v]) => `<span class="badge">${h(k.replace('X-Prompt-Route', 'route'))}: ${h(v)}</span>`).join('')}<span class="muted small">${t.reply.ms} ms</span></div><div class="reply">${h(t.reply.text)}</div>` : ''}
      </div>

      <div class="card section">
        <div class="card-head"><h2>Recent activity</h2><span class="spacer"></span><a href="#/activity" class="small">all →</a></div>
        ${renderActivityTable(state.activity.slice(0, 8))}
      </div>`;
  };

  function renderClassification(r) {
    return `
      <div class="result-line">
        <span class="badge signal">tier ${h(tierName(r.requiredTier))}</span>
        <span class="badge">${h(r.reason)}</span>
        ${r.route ? `<span class="badge">route ${h(r.route)} · ${num(r.similarity, 3)}</span>` : ''}
        <span class="badge">complexity ${num(r.complexity.score, 3)}</span>
        ${r.localOnly ? '<span class="badge warn">local only</span>' : ''}
        ${(r.requiredCapabilities || []).map((c) => `<span class="badge info">${h(c)}</span>`).join('')}
        <span class="muted small">${num(r.elapsedMilliseconds, 1)} ms</span>
      </div>
      <div class="grid cols-2">
        <div><h3>Models, in order</h3><ul class="candidates">${r.models.map((m, i) => `<li><span class="faint mono">${i + 1}.</span> <b>${h(m.model)}</b> <span class="tier">T${m.tier}</span> <span class="muted small">${h(m.connector)} · $${num(m.estimatedCostUsd, 5)}</span>${m.meetsRequiredTier ? '' : '<span class="badge warn">below tier</span>'}</li>`).join('') || '<li class="muted">no eligible model</li>'}</ul></div>
        <div><h3>Routes</h3><ul class="candidates">${r.routes.map((x) => `<li><b>${h(x.route)}</b> <span class="tier">T${x.tier}</span> <span class="muted small">${num(x.similarity, 3)}</span></li>`).join('') || '<li class="muted">no routes</li>'}</ul>
          <div class="small muted" style="margin-top:6px">signals: length ${num(r.complexity.lengthSignal, 2)} · code ${num(r.complexity.codeSignal, 2)} · structure ${num(r.complexity.structureSignal, 2)} · reasoning ${num(r.complexity.reasoningSignal, 2)} · ~${r.complexity.estimatedTokens} tokens</div></div>
      </div>`;
  }

  function renderActivityTable(entries) {
    if (!entries.length) return '<div class="muted small">No requests routed yet. Point a client at the gateway or use “Try a prompt”.</div>';
    return `<div class="table-wrap"><table>
      <tr><th>Time</th><th>Prompt</th><th>Tier</th><th>Reason</th><th>Route</th><th>Served by</th><th class="num">ms</th></tr>
      ${entries.map((e) => `<tr title="${h(e.detail || '')}">
        <td class="nowrap small">${h(when(e.timestamp))}</td>
        <td><span class="small">${h(e.promptPreview)}</span><div class="faint small">${e.client ? '<span class="badge info">' + h(e.client) + '</span> ' : ''}${h(e.protocol)} ${h(e.requestedModel || '')}${e.hasTools ? ' · tools' : ''}${e.toolContinuation ? ' · tool result' : ''}${e.localOnly ? ' · local only' : ''} · ~${e.estimatedInputTokens} tok</div></td>
        <td><span class="tier">T${e.tier}</span></td>
        <td class="small">${h(e.reason)}</td>
        <td class="small">${h(e.route || '—')}${e.similarity != null ? ` <span class="faint">${num(e.similarity, 2)}</span>` : ''}</td>
        <td class="small">${e.outcome === 'failed' ? '<span class="badge danger">failed</span>' : e.servedBy ? `<b>${h(e.servedBy)}</b>${e.attempt > 1 ? ` <span class="badge warn">try ${e.attempt}</span>` : ''}${e.degraded ? ' <span class="badge warn">degraded</span>' : ''}` : `<span class="muted">${h(e.plannedModel || '—')}</span> <span class="faint">planned</span>`}</td>
        <td class="num small">${e.totalMilliseconds != null ? num(e.totalMilliseconds, 0) : num(e.classificationMilliseconds, 1)}</td>
      </tr>`).join('')}
    </table></div>`;
  }

  views.activity = () => {
    const f = state.activityFilter;
    const entries = state.activity.filter((e) => f === 'all' || (f === 'failed' ? e.outcome === 'failed' : f === 'degraded' ? e.degraded : f === 'tools' ? e.hasTools : true));
    return `
      <div class="section-head"><h1>Activity</h1><span class="muted">The last ${state.activity.length} routed requests (newest first).</span><span class="spacer"></span>
        <select id="activity-filter" style="width:150px">${['all', 'failed', 'degraded', 'tools'].map((x) => `<option value="${x}" ${x === f ? 'selected' : ''}>${x}</option>`).join('')}</select>
        <label class="check"><input type="checkbox" id="auto-refresh" ${state.autoRefresh ? 'checked' : ''}> auto-refresh</label>
        <button class="btn small" data-action="refresh">Refresh</button></div>
      ${renderActivityTable(entries)}`;
  };

  views.tiers = () => {
    const c = state.config;
    const list = c.Tiers && c.Tiers.length ? c.Tiers : null;
    const effective = tiers();
    const routes = c.Routes || [];
    const models = c.Models || [];
    return `
      <div class="section-head"><h1>Tiers</h1><span class="muted">The capability ladder. Routes say which rung a prompt needs; models say which rung they reach.</span></div>
      <div class="card section">
        <div class="card-head"><h2>Ladder</h2><span class="spacer"></span>
          ${list ? '<button class="btn small" data-action="tiers-defaults">Use defaults</button>' : '<span class="muted small">Using the built-in ladder.</span> <button class="btn small" data-action="tiers-customize">Customize</button>'}
          ${list ? '<button class="btn small" data-action="tier-add">Add tier</button>' : ''}</div>
        ${list ? `<table><tr><th style="width:80px">Level</th><th style="width:220px">Name</th><th>Description</th><th></th></tr>
          ${list.map((t, i) => `<tr>
            <td><input type="number" data-bind="Tiers.${i}.Level" data-kind="number" value="${t.Level}"></td>
            <td><input type="text" data-bind="Tiers.${i}.Name" value="${h(t.Name)}"></td>
            <td><input type="text" data-bind="Tiers.${i}.Description" data-kind="nulltext" value="${h(t.Description || '')}"></td>
            <td><button class="btn ghost small" data-action="tier-remove" data-index="${i}">remove</button></td></tr>`).join('')}</table>`
        : `<table><tr><th style="width:80px">Level</th><th style="width:220px">Name</th><th>Description</th></tr>${effective.map((t) => `<tr><td><span class="tier">T${t.Level}</span></td><td>${h(t.Name)}</td><td class="muted">${h(t.Description || '')}</td></tr>`).join('')}</table>`}
      </div>
      <div class="card section">
        <div class="card-head"><h2>Who needs what, who serves what</h2></div>
        <table class="matrix"><tr><th>Tier</th><th>Routes requiring it</th><th>Models at this tier</th><th>Can serve it (tier ≥)</th></tr>
          ${effective.map((t) => `<tr>
            <td><span class="tier">T${t.Level}</span> ${h(t.Name)}</td>
            <td class="small">${routes.filter((r) => r.Tier === t.Level).map((r) => `<span class="badge">${h(r.Name)}</span>`).join(' ') || '<span class="faint">—</span>'}</td>
            <td class="small">${models.filter((m) => m.Tier === t.Level).map((m) => `<span class="badge ${m.Enabled === false ? '' : 'accent'}">${h(m.Name)}</span>`).join(' ') || '<span class="faint">—</span>'}</td>
            <td class="small">${models.filter((m) => m.Tier >= t.Level && m.Enabled !== false).map((m) => h(m.Name)).join(', ') || '<span class="badge danger">nothing</span>'}</td>
          </tr>`).join('')}
        </table>
        <p class="muted small" style="margin-top:8px">Unmatched prompts need tier ${c.FallbackTier}; complexity can raise a prompt to ${(c.ComplexityTiers || []).map((r) => `T${r.Tier} at ≥ ${r.MinScore}`).join(', ') || 'the defaults (2 / 3 / 4)'}. Change these under <a href="#/routing">Routing rules</a>.</p>
      </div>`;
  };

  const tierOptions = (value) => tiers().map((t) => `<option value="${t.Level}" ${t.Level === value ? 'selected' : ''}>${t.Level} · ${h(t.Name)}</option>`).join('') + (tiers().some((t) => t.Level === value) ? '' : `<option value="${value}" selected>${value}</option>`);

  views.routes = () => {
    const routes = state.config.Routes || [];
    return `
      <div class="section-head"><h1>Routes</h1><span class="muted">Intent clusters: example prompts and the tier they need.</span><span class="spacer"></span><button class="btn primary small" data-action="route-add">Add route</button></div>
      ${routes.map((r, i) => {
        const key = 'route:' + i;
        return `<div class="item ${state.open.has(key) ? 'open' : ''}" data-key="${key}">
          <div class="item-head" data-action="toggle" data-key="${key}">
            <span class="chev">▶</span><span class="title">${h(r.Name || '(unnamed)')}</span>
            <span class="meta"><span class="tier">T${r.Tier}</span>${r.LocalOnly ? '<span class="badge warn">local only</span>' : ''}${(r.RequiredCapabilities || []).map((c) => `<span class="badge info">${h(c)}</span>`).join('')}<span>${(r.Utterances || []).length} utterances</span>${r.SimilarityThreshold != null ? `<span>threshold ${r.SimilarityThreshold}</span>` : ''}</span>
            <span class="spacer"></span><span class="muted small">${h(r.Description || '')}</span>
          </div>
          <div class="item-body"><div class="form-grid">
            <label class="field"><span class="name">Name</span><input type="text" data-bind="Routes.${i}.Name" value="${h(r.Name)}"></label>
            <label class="field"><span class="name">Tier</span><select data-bind="Routes.${i}.Tier" data-kind="number">${tierOptions(r.Tier)}</select></label>
            <label class="field"><span class="name">Required capabilities</span><input type="text" data-bind="Routes.${i}.RequiredCapabilities" data-kind="csv" value="${h(csv(r.RequiredCapabilities))}" placeholder="code, vision"><span class="hint">Tags the model must declare (${CAPABILITY_HINTS.join(', ')}, …).</span></label>
            <label class="field"><span class="name">Similarity threshold</span><input type="number" step="0.01" min="-1" max="1" data-bind="Routes.${i}.SimilarityThreshold" data-kind="float" data-nullable="1" value="${r.SimilarityThreshold == null ? '' : r.SimilarityThreshold}" placeholder="default ${state.config.DefaultSimilarityThreshold}"></label>
            <label class="field wide"><span class="name">Description</span><input type="text" data-bind="Routes.${i}.Description" data-kind="nulltext" value="${h(r.Description || '')}"></label>
            <label class="check wide"><input type="checkbox" data-bind="Routes.${i}.LocalOnly" data-kind="bool" ${r.LocalOnly ? 'checked' : ''}> Local only: only models marked <i>local</i> may answer, even on failover</label>
            <label class="field wide"><span class="name">Utterances (one per line)</span><textarea data-bind="Routes.${i}.Utterances" data-kind="lines" rows="6">${h(lines(r.Utterances))}</textarea><span class="hint">5–20 diverse examples route best.</span></label>
          </div>
          <div class="row-actions" style="margin-top:10px"><button class="btn small" data-action="route-duplicate" data-index="${i}">Duplicate</button><button class="btn danger small" data-action="route-remove" data-index="${i}">Remove</button></div></div>
        </div>`;
      }).join('') || '<div class="card muted">No routes. Without routes every prompt gets the fallback tier (plus complexity escalation).</div>'}`;
  };

  views.models = () => {
    const models = state.config.Models || [];
    const names = connectorNames();
    return `
      <div class="section-head"><h1>Models</h1><span class="muted">The catalog the router chooses from.</span><span class="spacer"></span><button class="btn primary small" data-action="model-add">Add model</button></div>
      ${models.map((m, i) => {
        const key = 'model:' + i;
        const conn = state.config.Connectors[m.Connector];
        const free = !(m.InputCostPerMillionTokens > 0 || m.OutputCostPerMillionTokens > 0);
        return `<div class="item ${state.open.has(key) ? 'open' : ''} ${m.Enabled === false ? 'disabled' : ''}" data-key="${key}">
          <div class="item-head" data-action="toggle" data-key="${key}">
            <span class="chev">▶</span><span class="title">${h(m.Name || '(unnamed)')}</span>
            <span class="meta"><span class="tier">T${m.Tier}</span><span class="mono">${h(m.Model || '')}</span><span>via ${h(m.Connector)}${conn ? ` <span class="faint">(${h(conn.Type || 'OpenAICompatible')}${conn.Type === 'Cli' ? '/' + h(conn.Preset || 'Custom') : ''})</span>` : ' <span class="badge danger">unknown connector</span>'}</span>
            ${m.IsLocal ? '<span class="badge info">local</span>' : ''}${free ? '<span class="badge ok">free</span>' : `<span class="badge">$${m.InputCostPerMillionTokens}/$${m.OutputCostPerMillionTokens} per M</span>`}${(m.Capabilities || []).map((c) => `<span class="badge">${h(c)}</span>`).join('')}</span>
            <span class="spacer"></span><label class="check" onclick="event.stopPropagation()"><input type="checkbox" data-bind="Models.${i}.Enabled" data-kind="bool" ${m.Enabled !== false ? 'checked' : ''}> enabled</label>
          </div>
          <div class="item-body"><div class="form-grid">
            <label class="field"><span class="name">Name</span><input type="text" data-bind="Models.${i}.Name" value="${h(m.Name)}"><span class="hint">Clients can request it directly as <code>model</code>.</span></label>
            <label class="field"><span class="name">Connector</span><select data-bind="Models.${i}.Connector">${names.map((n) => `<option ${n === m.Connector ? 'selected' : ''}>${h(n)}</option>`).join('')}${names.includes(m.Connector) ? '' : `<option selected>${h(m.Connector)}</option>`}</select></label>
            <label class="field"><span class="name">Provider model id</span><div class="inline"><input type="text" data-bind="Models.${i}.Model" data-kind="nulltext" value="${h(m.Model || '')}" placeholder="connector default" list="models-${i}"><button class="btn small" data-action="discover" data-connector="${h(m.Connector)}" data-index="${i}" title="Ask the connector which models it has">Discover</button></div><datalist id="models-${i}"></datalist></label>
            <label class="field"><span class="name">Tier</span><select data-bind="Models.${i}.Tier" data-kind="number">${tierOptions(m.Tier)}</select></label>
            <label class="field"><span class="name">Capabilities</span><input type="text" data-bind="Models.${i}.Capabilities" data-kind="csv" value="${h(csv(m.Capabilities))}" placeholder="chat, code, tools, vision, reasoning"><span class="hint">Declare <code>tools</code> only when the connector can carry them (HTTP, or a CLI with prompted tool calling).</span></label>
            <label class="field"><span class="name">Context window (tokens)</span><input type="number" min="0" data-bind="Models.${i}.ContextWindow" data-kind="number" value="${m.ContextWindow || 0}"><span class="hint">0 = unknown (never skipped).</span></label>
            <label class="field"><span class="name">Input cost ($ / M tokens)</span><input type="number" min="0" step="0.01" data-bind="Models.${i}.InputCostPerMillionTokens" data-kind="float" value="${m.InputCostPerMillionTokens || 0}"></label>
            <label class="field"><span class="name">Output cost ($ / M tokens)</span><input type="number" min="0" step="0.01" data-bind="Models.${i}.OutputCostPerMillionTokens" data-kind="float" value="${m.OutputCostPerMillionTokens || 0}"><span class="hint">0 for local models and flat-rate subscriptions.</span></label>
            <label class="field"><span class="name">Priority</span><input type="number" data-bind="Models.${i}.Priority" data-kind="number" value="${m.Priority || 0}"><span class="hint">Lower is preferred; breaks ties.</span></label>
            <label class="field wide"><span class="name">Description</span><input type="text" data-bind="Models.${i}.Description" data-kind="nulltext" value="${h(m.Description || '')}"></label>
            <label class="check wide"><input type="checkbox" data-bind="Models.${i}.IsLocal" data-kind="bool" ${m.IsLocal ? 'checked' : ''}> Local: prompts stay on this machine (required to serve local-only routes)</label>
          </div>
          <div class="row-actions" style="margin-top:10px"><button class="btn small" data-action="model-duplicate" data-index="${i}">Duplicate</button><button class="btn danger small" data-action="model-remove" data-index="${i}">Remove</button></div></div>
        </div>`;
      }).join('') || '<div class="card muted">No models yet.</div>'}`;
  };

  const secretField = (path, value, label) => `
    <label class="field"><span class="name">${label}</span>
      <div class="inline">
        ${value === '***' || isReference(value)
          ? (isReference(value) ? `<code class="small" title="Resolved when the configuration is applied">${h(value)}</code>` : '<span class="badge ok">set</span>') + '<button class="btn small" data-action="secret-change" data-path="' + path + '">Change</button><button class="btn ghost small" data-action="secret-clear" data-path="' + path + '">Clear</button>'
          : `<input type="password" data-bind="${path}" data-kind="nulltext" value="${h(value || '')}" placeholder="not set, or \${env:VARIABLE}" autocomplete="new-password">`}
      </div><span class="hint">${state.meta.secretStore
        ? 'Typed keys are encrypted into the secret store when you save; the file keeps a <code>${secret:…}</code> reference.'
        : 'Typed keys are written to the configuration file when you save. Prefer <code>${env:VARIABLE}</code>, or leave empty to inherit from appsettings, user-secrets or the environment.'}</span></label>`;

  views.connectors = () => {
    const conns = state.config.Connectors || {};
    return `
      <div class="section-head"><h1>Connectors</h1><span class="muted">How the router reaches each provider.</span><span class="spacer"></span><button class="btn primary small" data-action="connector-add">Add connector</button></div>
      ${Object.entries(conns).map(([name, c]) => {
        const key = 'conn:' + name;
        const type = c.Type || 'OpenAICompatible';
        const p = `Connectors.${name}`;
        const models = (state.config.Models || []).filter((m) => (m.Connector || '').toLowerCase() === name.toLowerCase());
        return `<div class="item ${state.open.has(key) ? 'open' : ''}" data-key="${key}">
          <div class="item-head" data-action="toggle" data-key="${key}">
            <span class="chev">▶</span><span class="title">${h(name)}</span>
            <span class="meta"><span class="badge accent">${h(type)}${type === 'Cli' ? ' / ' + h(c.Preset || 'Custom') : ''}</span><span class="mono">${h(type === 'Cli' ? (c.Executable || '(preset default)') : (c.BaseAddress || ''))}</span><span>${models.length} model${models.length === 1 ? '' : 's'}</span>${c.ApiKey === '***' ? '<span class="badge ok">key set</span>' : (isReference(c.ApiKey) ? '<span class="badge ok" title="' + h(c.ApiKey) + '">key: reference</span>' : '')}${type === 'Cli' && c.ToolCalling === 'Prompted' ? '<span class="badge info">prompted tools</span>' : ''}</span>
          </div>
          <div class="item-body"><div class="form-grid">
            <label class="field"><span class="name">Name</span><input type="text" data-action-input="connector-rename" data-name="${h(name)}" value="${h(name)}"><span class="hint">Models refer to it by this name; renaming updates them.</span></label>
            <label class="field"><span class="name">Type</span><select data-bind="${p}.Type">${CONNECTOR_TYPES.map((t) => `<option ${t === type ? 'selected' : ''}>${t}</option>`).join('')}</select><span class="hint">OpenAICompatible: LM Studio, Ollama, vLLM, OpenAI, Gemini, OpenRouter… · Anthropic: Messages API with a key · Cli: a vendor CLI on your own login.</span></label>
            ${type === 'Cli' ? `
              <label class="field"><span class="name">Preset</span><select data-bind="${p}.Preset">${PRESETS.map((x) => `<option ${x === (c.Preset || 'Custom') ? 'selected' : ''}>${x}</option>`).join('')}</select></label>
              <label class="field"><span class="name">Executable</span><input type="text" data-bind="${p}.Executable" data-kind="nulltext" value="${h(c.Executable || '')}" placeholder="preset default (claude / copilot / codex)"><span class="hint">Started directly, never through a shell. On Windows use the .exe.</span></label>
              <label class="field"><span class="name">Tool calling</span><select data-bind="${p}.ToolCalling">${['None', 'Prompted'].map((x) => `<option ${x === (c.ToolCalling || 'None') ? 'selected' : ''}>${x}</option>`).join('')}</select><span class="hint">Prompted lets agentic clients (Claude Code) use this CLI: tools are described in the prompt and tool calls parsed from the reply.</span></label>
              <label class="field"><span class="name">Max concurrency</span><input type="number" min="1" data-bind="${p}.MaxConcurrency" data-kind="number" value="${c.MaxConcurrency || 2}"></label>
              <label class="field"><span class="name">Max argument length</span><input type="number" min="100" data-bind="${p}.MaxArgumentLength" data-kind="number" value="${c.MaxArgumentLength || 24000}"><span class="hint">Longer prompts go on stdin.</span></label>
              <label class="field"><span class="name">Timeout (hh:mm:ss)</span><input type="text" data-bind="${p}.Timeout" value="${h(c.Timeout || '00:00:00')}" placeholder="00:05:00"><span class="hint">Time to first output; 00:00:00 = default (5 min).</span></label>
              ${(c.Preset || 'Custom') === 'Custom' ? `<label class="field"><span class="name">Prompt input</span><select data-bind="${p}.PromptInput">${['Stdin', 'Argument'].map((x) => `<option ${x === (c.PromptInput || 'Stdin') ? 'selected' : ''}>${x}</option>`).join('')}</select></label>` : ''}
              <label class="field wide"><span class="name">Arguments (one per line)</span><textarea data-bind="${p}.Arguments" data-kind="lines" rows="3">${h(lines(c.Arguments))}</textarea><span class="hint">Presets: extra flags appended (e.g. <code>--effort</code> / <code>low</code>). Custom: the full template with <code>{model}</code> and <code>{prompt}</code>.</span></label>
              <label class="field"><span class="name">Quota error patterns</span><textarea data-bind="${p}.QuotaErrorPatterns" data-kind="lines" rows="3">${h(lines(c.QuotaErrorPatterns))}</textarea><span class="hint">Output fragments that mean “out of quota” (added to the preset's).</span></label>
              <label class="field"><span class="name">Environment variables (KEY=value)</span><textarea data-bind="${p}.EnvironmentVariables" data-kind="kv" rows="3">${h(kv(c.EnvironmentVariables))}</textarea></label>
              <label class="field"><span class="name">Remove environment variables</span><textarea data-bind="${p}.RemoveEnvironmentVariables" data-kind="lines" rows="3">${h(lines(c.RemoveEnvironmentVariables))}</textarea><span class="hint">Empty = preset defaults (API keys that would switch billing away from your subscription).</span></label>
              <label class="check wide"><input type="checkbox" data-bind="${p}.AllowRemoteClients" data-kind="bool" ${c.AllowRemoteClients ? 'checked' : ''}> Allow remote clients <span class="muted">— read docs/TERMS.md first; a personal subscription must not serve other people</span></label>`
            : `
              <label class="field"><span class="name">Base address</span><input type="url" data-bind="${p}.BaseAddress" data-kind="nulltext" value="${h(c.BaseAddress || '')}" placeholder="${type === 'Anthropic' ? 'https://api.anthropic.com' : 'http://localhost:1234'}"></label>
              <label class="field"><span class="name">Request path</span><input type="text" data-bind="${p}.RequestPath" value="${h(c.RequestPath || '/v1/chat/completions')}"><span class="hint">${type === 'Anthropic' ? 'Leave the default; /v1/messages is used.' : 'Gemini: /v1beta/openai/chat/completions · OpenRouter: /api/v1/chat/completions'}</span></label>
              ${secretField(p + '.ApiKey', c.ApiKey, 'API key')}
              <label class="field"><span class="name">Timeout (hh:mm:ss)</span><input type="text" data-bind="${p}.Timeout" value="${h(c.Timeout || '00:00:00')}" placeholder="00:01:40"><span class="hint">Time to response headers; 00:00:00 = default (100 s).</span></label>
              ${type === 'Anthropic' ? `<label class="field"><span class="name">anthropic-version</span><input type="text" data-bind="${p}.AnthropicVersion" value="${h(c.AnthropicVersion || '2023-06-01')}"></label>` : ''}
              <label class="field"><span class="name">Extra headers (Name=value)</span><textarea data-bind="${p}.Headers" data-kind="kv" rows="3">${h(kv(c.Headers))}</textarea><span class="hint">Azure: <code>api-key=…</code> · OpenAI org: <code>OpenAI-Organization=org_…</code></span></label>
              <label class="field"><span class="name">Forward client headers</span><textarea data-bind="${p}.ForwardHeaders" data-kind="lines" rows="3">${h(lines(c.ForwardHeaders))}</textarea><span class="hint">Copied from the client request (e.g. <code>anthropic-beta</code>). Authorization headers are never forwarded.</span></label>`}
          </div>
          <div class="row-actions" style="margin-top:10px"><button class="btn small" data-action="discover" data-connector="${h(name)}">List models</button><button class="btn danger small" data-action="connector-remove" data-name="${h(name)}">Remove</button></div>
          <div class="small muted" id="discover-${h(name)}" style="margin-top:6px"></div></div>
        </div>`;
      }).join('') || '<div class="card muted">No connectors yet.</div>'}`;
  };

  views.routing = () => {
    const c = state.config;
    const sel = c.Selection || {};
    const cx = c.Complexity || {};
    const rules = c.ComplexityTiers;
    return `
      <div class="section-head"><h1>Routing rules</h1><span class="muted">How a prompt becomes a required tier, and how models are ranked.</span></div>
      <div class="card section"><div class="card-head"><h2>Classification</h2></div><div class="form-grid">
        <label class="field"><span class="name">Default similarity threshold</span><input type="number" step="0.01" min="-1" max="1" data-bind="DefaultSimilarityThreshold" data-kind="float" value="${c.DefaultSimilarityThreshold}"><span class="hint">Hashing: 0.25–0.35 · nomic-embed-text: 0.60–0.70 · OpenAI text-embedding-3: 0.40–0.55.</span></label>
        <label class="field"><span class="name">Fallback tier</span><select data-bind="FallbackTier" data-kind="number">${tierOptions(c.FallbackTier)}</select><span class="hint">When no route matches or classification fails.</span></label>
        <label class="field"><span class="name">Ambiguity margin</span><input type="number" step="0.005" min="0" max="2" data-bind="AmbiguityMargin" data-kind="float" value="${c.AmbiguityMargin}"><span class="hint">Near-tied routes contribute their tier. 0 disables.</span></label>
        <label class="field"><span class="name">Scoring strategy</span><select data-bind="ScoringStrategy">${SCORING.map((x) => `<option ${x === (c.ScoringStrategy || 'MaxSimilarity') ? 'selected' : ''}>${x}</option>`).join('')}</select></label>
        <label class="field"><span class="name">Complexity short-circuit</span><input type="number" step="0.01" min="0" max="2" data-bind="ComplexityShortCircuitThreshold" data-kind="float" value="${c.ComplexityShortCircuitThreshold}"><span class="hint">Skip the embedding call above this score. &gt; 1 disables.</span></label>
        <label class="field"><span class="name">Reported route scores</span><input type="number" min="0" data-bind="MaxReportedScores" data-kind="number" value="${c.MaxReportedScores == null ? 3 : c.MaxReportedScores}"></label>
      </div></div>

      <div class="card section"><div class="card-head"><h2>Complexity → tier</h2><span class="spacer"></span>${rules ? '<button class="btn small" data-action="rule-add">Add rule</button><button class="btn ghost small" data-action="rules-defaults">Use defaults</button>' : '<span class="muted small">Using defaults: 0.45 → T2, 0.65 → T3, 0.85 → T4.</span> <button class="btn small" data-action="rules-customize">Customize</button>'}</div>
        ${rules ? (rules.length ? `<table><tr><th>Min score</th><th>Tier</th><th></th></tr>${rules.map((r, i) => `<tr><td><input type="number" step="0.01" min="0" max="1" data-bind="ComplexityTiers.${i}.MinScore" data-kind="float" value="${r.MinScore}"></td><td><select data-bind="ComplexityTiers.${i}.Tier" data-kind="number">${tierOptions(r.Tier)}</select></td><td><button class="btn ghost small" data-action="rule-remove" data-index="${i}">remove</button></td></tr>`).join('')}</table>` : '<div class="muted small">No rules: complexity never raises the tier.</div>') : ''}
      </div>

      <div class="card section"><div class="card-head"><h2>Model selection and failover</h2></div><div class="form-grid">
        <label class="field"><span class="name">Strategy</span><select data-bind="Selection.Strategy">${STRATEGIES.map((x) => `<option ${x === (sel.Strategy || 'CheapestCapable') ? 'selected' : ''}>${x}</option>`).join('')}</select><span class="hint">Clients can override per request with auto:cheapest / auto:best / auto:priority.</span></label>
        <label class="field"><span class="name">Max attempts</span><input type="number" min="1" data-bind="Selection.MaxAttempts" data-kind="number" value="${sel.MaxAttempts || 3}"></label>
        <label class="field"><span class="name">Quota cooldown</span><input type="text" data-bind="Selection.QuotaCooldown" value="${h(sel.QuotaCooldown || '00:15:00')}"></label>
        <label class="field"><span class="name">Failure cooldown</span><input type="text" data-bind="Selection.FailureCooldown" value="${h(sel.FailureCooldown || '00:00:30')}"></label>
        <label class="field"><span class="name">Estimated output tokens</span><input type="number" min="0" data-bind="Selection.EstimatedOutputTokens" data-kind="number" value="${sel.EstimatedOutputTokens == null ? 512 : sel.EstimatedOutputTokens}"><span class="hint">For cost estimates and context-window checks.</span></label>
        <label class="check wide"><input type="checkbox" data-bind="Selection.AllowBelowRequiredTier" data-kind="bool" ${sel.AllowBelowRequiredTier !== false ? 'checked' : ''}> Allow below required tier: degrade to the best available model instead of failing</label>
      </div></div>

      <div class="card section"><div class="card-head"><h2>Complexity heuristics</h2></div><div class="form-grid">
        <label class="field"><span class="name">Token saturation</span><input type="number" min="1" data-bind="Complexity.TokenSaturation" data-kind="number" value="${cx.TokenSaturation || 2048}"></label>
        <label class="field"><span class="name">Length weight</span><input type="number" step="0.05" min="0" max="1" data-bind="Complexity.LengthWeight" data-kind="float" value="${cx.LengthWeight == null ? 0.6 : cx.LengthWeight}"></label>
        <label class="field"><span class="name">Code weight</span><input type="number" step="0.05" min="0" max="1" data-bind="Complexity.CodeWeight" data-kind="float" value="${cx.CodeWeight == null ? 0.6 : cx.CodeWeight}"></label>
        <label class="field"><span class="name">Structure weight</span><input type="number" step="0.05" min="0" max="1" data-bind="Complexity.StructureWeight" data-kind="float" value="${cx.StructureWeight == null ? 0.5 : cx.StructureWeight}"></label>
        <label class="field"><span class="name">Reasoning weight</span><input type="number" step="0.05" min="0" max="1" data-bind="Complexity.ReasoningWeight" data-kind="float" value="${cx.ReasoningWeight == null ? 0.6 : cx.ReasoningWeight}"></label>
        <label class="field"><span class="name">Reasoning keyword saturation</span><input type="number" min="1" data-bind="Complexity.ReasoningKeywordSaturation" data-kind="number" value="${cx.ReasoningKeywordSaturation || 3}"></label>
        <label class="field wide"><span class="name">Reasoning keywords (one per line; empty = defaults)</span><textarea data-bind="Complexity.ReasoningKeywords" data-kind="lines" data-nullable="1" rows="4">${h(lines(cx.ReasoningKeywords))}</textarea></label>
      </div></div>`;
  };

  views.embeddings = () => {
    const e = state.config.Embeddings || {};
    const provider = e.Provider || 'Hashing';
    return `
      <div class="section-head"><h1>Embeddings</h1><span class="muted">How prompts and utterances are turned into vectors.</span></div>
      <div class="card section"><div class="form-grid">
        <label class="field"><span class="name">Provider</span><select data-bind="Embeddings.Provider">${['Hashing', 'OpenAICompatible'].map((x) => `<option ${x === provider ? 'selected' : ''}>${x}</option>`).join('')}</select><span class="hint">Hashing is lexical (shared words only); use a real embedding model in production.</span></label>
        ${provider === 'Hashing' ? `<label class="field"><span class="name">Dimensions</span><input type="number" min="16" data-bind="Embeddings.Dimensions" data-kind="number" value="${e.Dimensions || 512}"></label>` : `
          <label class="field"><span class="name">Endpoint</span><input type="url" data-bind="Embeddings.Endpoint" data-kind="nulltext" value="${h(e.Endpoint || '')}" placeholder="http://localhost:1234"></label>
          <label class="field"><span class="name">Model</span><input type="text" data-bind="Embeddings.Model" data-kind="nulltext" value="${h(e.Model || '')}" placeholder="text-embedding-nomic-embed-text-v1.5"></label>
          <label class="field"><span class="name">Request path</span><input type="text" data-bind="Embeddings.RequestPath" value="${h(e.RequestPath || '/v1/embeddings')}"></label>
          ${secretField('Embeddings.ApiKey', e.ApiKey, 'API key')}
          <label class="field"><span class="name">Max batch size</span><input type="number" min="1" data-bind="Embeddings.MaxBatchSize" data-kind="number" value="${e.MaxBatchSize || 64}"></label>
          <label class="field"><span class="name">Timeout</span><input type="text" data-bind="Embeddings.Timeout" value="${h(e.Timeout || '00:00:10')}"><span class="hint">On the hot path of every request: keep it short.</span></label>`}
        <label class="check wide"><input type="checkbox" data-bind="Embeddings.WarmUpOnStartup" data-kind="bool" ${e.WarmUpOnStartup !== false ? 'checked' : ''}> Warm up on start-up (embed utterances in the background)</label>
      </div>
      <div class="inline" style="margin-top:12px"><button class="btn" data-action="embeddings-test">Test current provider</button><span id="embeddings-result" class="small muted"></span></div>
      <p class="muted small" style="margin-top:8px">The test uses the <b>applied</b> configuration. Apply your changes first.</p></div>`;
  };

  views.gateway = () => {
    const m = state.config.Middleware || {};
    const aliases = Object.entries(m.ModelAliases || {});
    return `
      <div class="section-head"><h1>Gateway</h1><span class="muted">The front door: which requests are routed, and how client model names map.</span></div>
      <div class="card section"><div class="card-head"><h2>Mode and paths</h2></div><div class="form-grid">
        <label class="field"><span class="name">Mode</span><select data-bind="Middleware.Mode">${['Dispatch', 'Annotate'].map((x) => `<option ${x === (m.Mode || 'Annotate') ? 'selected' : ''}>${x}</option>`).join('')}</select><span class="hint">Dispatch serves requests through the connectors. Annotate only attaches the plan for your own endpoint.</span></label>
        <label class="field"><span class="name">Message selection</span><select data-bind="Middleware.MessageSelection">${MESSAGE_SELECTION.map((x) => `<option ${x === (m.MessageSelection || 'LastUserMessage') ? 'selected' : ''}>${x}</option>`).join('')}</select><span class="hint">Which messages are classified.</span></label>
        <label class="field"><span class="name">OpenAI paths (one per line)</span><textarea data-bind="Middleware.PathPrefixes" data-kind="lines" rows="3">${h(lines(m.PathPrefixes))}</textarea><span class="hint">Chat-completions bodies. Required in Dispatch mode.</span></label>
        <label class="field"><span class="name">Anthropic paths (one per line)</span><textarea data-bind="Middleware.AnthropicPathPrefixes" data-kind="lines" rows="3" placeholder="/v1/messages">${h(lines(m.AnthropicPathPrefixes))}</textarea><span class="hint">Messages API bodies (Claude Code, Anthropic SDKs). Empty = <code>/v1/messages</code>.</span></label>
        <label class="field"><span class="name">Max request body (bytes)</span><input type="number" min="1024" data-bind="Middleware.MaxRequestBodyBytes" data-kind="number" value="${m.MaxRequestBodyBytes || 8388608}"><span class="hint">Agentic clients send large system prompts and tool lists; 8 MiB is a safe default.</span></label>
        <label class="field"><span class="name">Header prefix</span><input type="text" data-bind="Middleware.HeaderPrefix" value="${h(m.HeaderPrefix || 'X-Prompt-Route')}"></label>
        <label class="check"><input type="checkbox" data-bind="Middleware.EmitResponseHeaders" data-kind="bool" ${m.EmitResponseHeaders !== false ? 'checked' : ''}> Emit routing headers</label>
        <label class="check"><input type="checkbox" data-bind="Middleware.FailOpen" data-kind="bool" ${m.FailOpen !== false ? 'checked' : ''}> Fail open (fallback tier when classification throws)</label>
        <label class="check"><input type="checkbox" data-bind="Middleware.PinToolCallingTurns" data-kind="bool" ${m.PinToolCallingTurns !== false ? 'checked' : ''}> Pin tool-calling turns to the model that started them</label>
        <label class="check"><input type="checkbox" data-bind="Middleware.StripSystemReminders" data-kind="bool" ${m.StripSystemReminders !== false ? 'checked' : ''}> Ignore &lt;system-reminder&gt; blocks when classifying</label>
      </div></div>
      <div class="card section"><div class="card-head"><h2>Gateway keys</h2><span class="muted small">Optional. When set, clients on other machines must send one (<code>Authorization: Bearer</code> or <code>x-api-key</code>); the client name is shown in Activity.</span></div><div class="form-grid">
        <label class="field wide"><span class="name">Keys (client=key, one per line)</span><textarea data-bind="Middleware.GatewayKeys" data-kind="kv" rows="3" placeholder="laptop=\${env:ROUTER_KEY_LAPTOP}">${h(kv(m.GatewayKeys))}</textarea><span class="hint">Stored keys show as <code>***</code> and are kept when left as is. Use <code>\${env:…}</code> references to keep keys out of the file.</span></label>
        <label class="check"><input type="checkbox" data-bind="Middleware.RequireGatewayKeyForLocalClients" data-kind="bool" ${m.RequireGatewayKeyForLocalClients ? 'checked' : ''}> Require a key from this machine too (for a local reverse proxy)</label>
      </div></div>
      <div class="card section"><div class="card-head"><h2>Model aliases</h2><span class="muted small">Map the names clients send to routing aliases or catalog models. Globs, first match wins.</span><span class="spacer"></span><button class="btn small" data-action="alias-add">Add alias</button><button class="btn ghost small" data-action="alias-claude">Add Claude Code defaults</button></div>
        <table><tr><th>Client model (glob)</th><th>Routes as</th><th></th></tr>
          ${aliases.map(([k, v], i) => `<tr><td><input type="text" data-action-input="alias-key" data-index="${i}" value="${h(k)}"></td><td><input type="text" data-action-input="alias-value" data-index="${i}" value="${h(v)}" list="alias-targets"></td><td><button class="btn ghost small" data-action="alias-remove" data-index="${i}">remove</button></td></tr>`).join('') || '<tr><td colspan="3" class="muted small">No aliases. Unknown names route as <code>auto</code>.</td></tr>'}
        </table>
        <datalist id="alias-targets">${['auto', 'auto:cheapest', 'auto:best', 'auto:priority'].concat((state.config.Models || []).map((x) => x.Name)).map((x) => `<option value="${h(x)}">`).join('')}</datalist>
      </div>`;
  };

  views.clients = () => {
    const origin = location.origin;
    const s = state.status;
    const anth = (s.gateway.anthropicPathPrefixes[0] || '/v1/messages');
    const snippet = (title, code, note) => `<div class="card section"><div class="card-head"><h2>${title}</h2></div>${note ? `<p class="muted small">${note}</p>` : ''}<div class="snippet"><pre><code>${h(code)}</code><button class="btn small copy" data-action="copy">Copy</button></pre></div></div>`;
    return `
      <div class="section-head"><h1>Connect clients</h1><span class="muted">Point your tools at <code>${h(origin)}</code>; keep their workflow, let the router pick the model.</span></div>
      ${s.gateway.mode !== 'Dispatch' ? '<div class="banner danger" style="margin-bottom:14px;border-radius:8px">The gateway is in <b>Annotate</b> mode: it only attaches routing information for your own endpoint. Switch to <b>Dispatch</b> under Gateway for the router to answer clients itself.</div>' : ''}
      ${snippet('Claude Code', `# Anthropic Messages API on ${origin}${anth}
export ANTHROPIC_BASE_URL=${origin}
export ANTHROPIC_AUTH_TOKEN=semantic-router   # any value: the router ignores it and never forwards it
claude

# One-off:
ANTHROPIC_BASE_URL=${origin} ANTHROPIC_AUTH_TOKEN=semantic-router claude -p "explain this repo"`,
        `Claude Code keeps its own model names (<code>/model opus</code>, <code>haiku</code> for side tasks); the <a href="#/gateway">model aliases</a> decide what they mean here. Tool-bearing turns go to models whose connector can carry tools: HTTP models declaring <code>tools</code>, or a CLI connector with <b>prompted</b> tool calling. ${(s.connectors.some((c) => c.type === 'Cli' && c.toolCalling === 'Prompted')) ? '' : 'None of your CLI connectors has prompted tool calling on, so agentic turns will only use HTTP models.'}`)}
      ${snippet('OpenAI SDKs and most tools', `# Base URL ${origin}/v1 · any API key · model "auto"
export OPENAI_BASE_URL=${origin}/v1
export OPENAI_API_KEY=semantic-router

# Python
from openai import OpenAI
client = OpenAI(base_url="${origin}/v1", api_key="semantic-router")
client.chat.completions.create(model="auto", messages=[{"role": "user", "content": "hello"}])`,
        'Works with Aider, Continue, Cline, Open WebUI, LibreChat, the OpenAI CLI and anything else with a configurable base URL. <code>auto:best</code>, <code>auto:cheapest</code> and <code>auto:priority</code> pick a strategy; a catalog model name forces that model.')}
      ${snippet('OpenAI Codex CLI', `# ~/.codex/config.toml
model_provider = "semantic-router"
model = "auto"

[model_providers.semantic-router]
name = "Semantic Router"
base_url = "${origin}/v1"
env_key = "OPENAI_API_KEY"   # export OPENAI_API_KEY=semantic-router`, 'Codex treats the router as an OpenAI-compatible provider.')}
      ${snippet('curl', `curl -N ${origin}/v1/chat/completions -H 'Content-Type: application/json' \\
  -d '{"model":"auto","stream":true,"messages":[{"role":"user","content":"Compare event sourcing and CRUD"}]}'

curl ${origin}${anth} -H 'Content-Type: application/json' -H 'anthropic-version: 2023-06-01' \\
  -d '{"model":"auto","max_tokens":256,"messages":[{"role":"user","content":"hello"}]}'

curl -s ${origin}/semantic-router/classify -H 'Content-Type: application/json' -d '{"prompt":"why does this deadlock?"}'`)}
      <div class="card section"><div class="card-head"><h2>Routing headers</h2></div><p class="muted small">Every response carries <code>${h(s.gateway && state.config.Middleware.HeaderPrefix || 'X-Prompt-Route')}</code>, <code>-Tier</code>, <code>-Reason</code>, <code>-Similarity</code>, <code>-Complexity</code>, <code>-Model</code>, <code>-Connector</code>, <code>-Attempt</code> and <code>-Degraded</code> headers, so you can see what happened without reading logs.</p></div>`;
  };

  views.json = () => `
      <div class="section-head"><h1>JSON</h1><span class="muted">The whole document, the same shape as the <code>SemanticRouter</code> section of appsettings.json.</span><span class="spacer"></span>
        <button class="btn small" data-action="json-load">Load into editor</button><button class="btn small" data-action="json-download">Download</button><button class="btn ghost small" data-action="json-baseline" title="Show what the application's own configuration says">Show baseline</button></div>
      <div class="card section">
        <textarea id="json-editor" rows="30" spellcheck="false">${h(JSON.stringify(state.config, null, 2))}</textarea>
        <div class="inline" style="margin-top:10px"><button class="btn primary" data-action="json-use">Use this JSON as the draft</button><span class="muted small">Secrets show as <code>***</code>; that placeholder keeps the stored value.</span></div>
      </div>
      <div class="card section flat"><details><summary>Where does the configuration come from?</summary><p class="small muted" style="margin-top:8px">The application's <code>SemanticRouter</code> section (appsettings.json, environment variables, user-secrets, code) is the baseline. When a configuration store is registered (<code>UseConfigurationFile</code>), a saved document replaces the baseline on start-up; secrets left empty in the document are inherited from the baseline. <b>Apply</b> changes the running router; <b>Apply &amp; save</b> also writes the store. <b>Reset</b> (in the JSON page) goes back to the baseline for this process.</p><button class="btn danger small" data-action="config-reset">Reset running configuration to baseline</button></details></div>`;

  /* ---------------------------------------------------------------------------------------------
     Rendering and routing
     --------------------------------------------------------------------------------------------- */
  function render() {
    const view = views[state.view] || views.overview;
    const root = $('#view');
    if (!state.config || !state.status) { root.innerHTML = '<div class="muted">Loading…</div>'; return; }
    try {
      root.innerHTML = (state.errors ? `<div class="card section" style="border-color:var(--danger)"><h3>Configuration problems</h3><ul class="errors">${state.errors.map((e) => `<li>${h(e)}</li>`).join('')}</ul></div>` : '') + view();
    } catch (e) {
      root.innerHTML = `<div class="card"><h3>Could not render</h3><pre>${h(e.stack || e)}</pre></div>`;
    }
    renderChrome();
  }

  function route() {
    const hash = location.hash.replace(/^#\/?/, '') || 'overview';
    state.view = views[hash] ? hash : 'overview';
    render();
    window.scrollTo(0, 0);
  }

  /* ---------------------------------------------------------------------------------------------
     Actions
     --------------------------------------------------------------------------------------------- */
  const actions = {
    toggle(el) { const k = el.dataset.key; if (state.open.has(k)) state.open.delete(k); else state.open.add(k); $(`.item[data-key="${CSS.escape(k)}"]`).classList.toggle('open'); },
    refresh() { refreshStatus(); },

    async validate() {
      const r = await api('POST', '/config/validate', state.config);
      state.errors = r.valid ? null : r.errors;
      render();
      toast(r.valid ? 'Configuration is valid.' : `${r.errors.length} problem(s).`, r.valid ? 'ok' : 'danger');
    },
    async apply() { await applyConfig(false); },
    async 'apply-save'() { await applyConfig(true); },
    discard() { state.config = JSON.parse(state.original); state.errors = null; render(); },
    async 'config-reset'() {
      if (!confirm('Reset the running configuration to the application baseline? Unsaved edits are lost; the stored file is left in place.')) return;
      setConfig(await api('POST', '/config/reset'));
      await refreshStatus();
      render();
      toast('Reset to baseline.', 'ok');
    },
    async 'reset-health'() { await api('POST', '/health/reset'); await refreshStatus(); toast('Cooldowns cleared.', 'ok'); },

    'tiers-customize'() { state.config.Tiers = tiers().map((t) => ({ Level: t.Level, Name: t.Name, Description: t.Description || null })); render(); },
    'tiers-defaults'() { state.config.Tiers = null; render(); },
    'tier-add'() { const next = Math.max(0, ...(state.config.Tiers || []).map((t) => t.Level)) + 1; state.config.Tiers.push({ Level: next, Name: 'Tier ' + next, Description: null }); render(); },
    'tier-remove'(el) { state.config.Tiers.splice(+el.dataset.index, 1); render(); },

    'route-add'() { state.config.Routes = state.config.Routes || []; state.config.Routes.push({ Name: 'new-route', Tier: 1, RequiredCapabilities: [], Utterances: [], SimilarityThreshold: null, LocalOnly: false, Description: null }); state.open.add('route:' + (state.config.Routes.length - 1)); render(); },
    'route-remove'(el) { state.config.Routes.splice(+el.dataset.index, 1); render(); },
    'route-duplicate'(el) { const r = JSON.parse(JSON.stringify(state.config.Routes[+el.dataset.index])); r.Name += '-copy'; state.config.Routes.push(r); render(); },
    'model-add'() { state.config.Models = state.config.Models || []; state.config.Models.push({ Name: 'new-model', Connector: connectorNames()[0] || '', Model: null, Tier: 1, Capabilities: ['chat'], IsLocal: false, ContextWindow: 0, InputCostPerMillionTokens: 0, OutputCostPerMillionTokens: 0, Priority: 0, Enabled: true, Description: null }); state.open.add('model:' + (state.config.Models.length - 1)); render(); },
    'model-remove'(el) { state.config.Models.splice(+el.dataset.index, 1); render(); },
    'model-duplicate'(el) { const m = JSON.parse(JSON.stringify(state.config.Models[+el.dataset.index])); m.Name += '-copy'; state.config.Models.push(m); render(); },
    'connector-add'() {
      let name = 'new-connector'; let n = 1;
      while (state.config.Connectors[name]) name = 'new-connector-' + (++n);
      state.config.Connectors[name] = { Type: 'OpenAICompatible', BaseAddress: 'http://localhost:1234', RequestPath: '/v1/chat/completions', ApiKey: null, Headers: {}, ForwardHeaders: [], AnthropicVersion: '2023-06-01', Preset: 'Custom', Executable: null, Arguments: null, PromptInput: 'Stdin', ToolCalling: 'None', QuotaErrorPatterns: [], EnvironmentVariables: {}, RemoveEnvironmentVariables: null, MaxArgumentLength: 24000, MaxConcurrency: 2, AllowRemoteClients: false, Timeout: '00:00:00' };
      state.open.add('conn:' + name); render();
    },
    'connector-remove'(el) { delete state.config.Connectors[el.dataset.name]; render(); },
    'secret-change'(el) { setPath(state.config, el.dataset.path, ''); render(); },
    'secret-clear'(el) { setPath(state.config, el.dataset.path, null); render(); },
    async discover(el) {
      const name = el.dataset.connector;
      const out = $('#discover-' + CSS.escape(name));
      try {
        const r = await api('POST', `/connectors/${encodeURIComponent(name)}/discover`);
        if (el.dataset.index != null) {
          const dl = $('#models-' + el.dataset.index);
          if (dl) dl.innerHTML = r.models.map((m) => `<option value="${h(m)}">`).join('');
        }
        const text = r.models.length ? `${r.models.length} model(s): ${r.models.join(', ')}` : 'No models reported.';
        if (out) out.textContent = (r.note ? r.note + ' ' : '') + text; else toast(text, 'ok');
      } catch (e) { if (out) out.textContent = 'Discovery failed: ' + e.message; else toast('Discovery failed: ' + e.message, 'danger'); }
    },
    'rule-add'() { state.config.ComplexityTiers.push({ MinScore: 0.5, Tier: 2 }); render(); },
    'rule-remove'(el) { state.config.ComplexityTiers.splice(+el.dataset.index, 1); render(); },
    'rules-defaults'() { state.config.ComplexityTiers = null; render(); },
    'rules-customize'() { state.config.ComplexityTiers = [{ MinScore: 0.45, Tier: 2 }, { MinScore: 0.65, Tier: 3 }, { MinScore: 0.85, Tier: 4 }]; render(); },
    'alias-add'() { state.config.Middleware.ModelAliases = state.config.Middleware.ModelAliases || {}; let k = '*pattern*'; let n = 1; while (k in state.config.Middleware.ModelAliases) k = `*pattern${++n}*`; state.config.Middleware.ModelAliases[k] = 'auto'; render(); },
    'alias-claude'() { const a = state.config.Middleware.ModelAliases = state.config.Middleware.ModelAliases || {}; Object.assign(a, { '*haiku*': 'auto:cheapest', '*sonnet*': 'auto', '*opus*': 'auto:best', 'claude-*': 'auto', 'gpt-*': 'auto', 'o*-mini': 'auto:cheapest' }); render(); },
    'alias-remove'(el) { const entries = Object.entries(state.config.Middleware.ModelAliases); entries.splice(+el.dataset.index, 1); state.config.Middleware.ModelAliases = Object.fromEntries(entries); render(); },
    async 'embeddings-test'() {
      const out = $('#embeddings-result'); out.textContent = 'testing…';
      try { const r = await api('POST', '/embeddings/test', { text: 'The quick brown fox' }); out.innerHTML = r.ok ? `<span class="badge ok">ok</span> ${r.dimensions} dimensions in ${r.milliseconds} ms` : `<span class="badge danger">failed</span> ${h(r.error)}`; }
      catch (e) { out.textContent = e.message; }
    },
    'json-load'() { $('#json-editor').value = JSON.stringify(state.config, null, 2); },
    'json-use'() { try { state.config = JSON.parse($('#json-editor').value); state.errors = null; render(); toast('Draft replaced from JSON. Apply to use it.', 'ok'); } catch (e) { toast('Invalid JSON: ' + e.message, 'danger'); } },
    'json-download'() { const blob = new Blob([JSON.stringify(state.config, null, 2)], { type: 'application/json' }); const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = 'semantic-router.json'; a.click(); },
    async 'json-baseline'() { const b = await api('GET', '/config/baseline'); $('#json-editor').value = JSON.stringify(b.config, null, 2); toast('Showing the application baseline (not the draft).'); },
    copy(el) { const code = el.parentElement.querySelector('code').textContent; navigator.clipboard.writeText(code).then(() => toast('Copied.', 'ok')); },

    async 'tester-classify'() {
      readTester();
      state.tester.busy = true; state.tester.reply = null; render();
      try { state.tester.result = await api('POST', '/classify', { prompt: state.tester.prompt, model: state.tester.model }); }
      catch (e) { toast(e.message, 'danger'); }
      state.tester.busy = false; render();
    },
    async 'tester-send'() {
      readTester();
      state.tester.busy = true; state.tester.result = null; render();
      const started = performance.now();
      try {
        const path = (state.status.gateway.pathPrefixes[0] || '/v1/chat/completions');
        const res = await fetch(location.origin + path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ model: state.tester.model, messages: [{ role: 'user', content: state.tester.prompt }] }) });
        const text = await res.text();
        let reply = text;
        try { const j = JSON.parse(text); reply = (j.choices && j.choices[0] && (j.choices[0].message.content || JSON.stringify(j.choices[0].message.tool_calls))) || j.detail || text; } catch { /* raw */ }
        const headers = []; res.headers.forEach((v, k) => { if (k.toLowerCase().startsWith('x-prompt-route')) headers.push([k.replace(/(^|-)(\w)/g, (m0, p, c) => p + c.toUpperCase()), v]); });
        state.tester.reply = { text: res.ok ? reply : `HTTP ${res.status}: ${reply}`, headers, ms: Math.round(performance.now() - started) };
      } catch (e) { toast(e.message, 'danger'); }
      state.tester.busy = false; render(); refreshStatus();
    },
  };

  function readTester() {
    const p = $('#tester-prompt'); const m = $('#tester-model');
    if (p) state.tester.prompt = p.value;
    if (m) state.tester.model = m.value;
  }

  async function applyConfig(persist) {
    try {
      const r = await api('PUT', '/config?persist=' + persist, state.config);
      setConfig(r);
      state.errors = null;
      await refreshStatus();
      render();
      toast(persist ? (r.store ? `Applied and saved to ${r.store}.` : 'Applied (no store registered: not saved).') : 'Applied to the running router (not saved).', 'ok');
    } catch (e) {
      state.errors = (e.data && e.data.errors) || [e.message];
      render();
      toast('Not applied: ' + state.errors.length + ' problem(s).', 'danger');
    }
  }

  /* ---------------------------------------------------------------------------------------------
     Event wiring
     --------------------------------------------------------------------------------------------- */
  document.addEventListener('click', (ev) => {
    const el = ev.target.closest('[data-action]');
    if (!el) return;
    const action = actions[el.dataset.action];
    if (!action) return;
    if (el.tagName === 'INPUT' && el.type === 'checkbox') return;
    ev.preventDefault();
    Promise.resolve(action(el)).catch((e) => toast(e.message, 'danger'));
  });

  document.addEventListener('input', (ev) => {
    const el = ev.target;
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
    if (el.id === 'tester-prompt') state.tester.prompt = el.value;
  });

  document.addEventListener('change', (ev) => {
    const el = ev.target;
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
    } else if (el.id === 'activity-filter') { state.activityFilter = el.value; render(); }
    else if (el.id === 'auto-refresh') { state.autoRefresh = el.checked; }
    else if (el.id === 'tester-model') { state.tester.model = el.value; }
  });

  window.addEventListener('hashchange', route);
  window.addEventListener('beforeunload', (ev) => { if (isDirty()) { ev.preventDefault(); ev.returnValue = ''; } });

  initTheme();
  const signOutBtn = $('#sign-out');
  if (signOutBtn) signOutBtn.addEventListener('click', async () => { await api('DELETE', '/session').catch(() => {}); location.reload(); });
  loadAll().then(route).catch((e) => {
    if (e.status === 401) { showSignIn(); return; }
    $('#view').innerHTML = `<div class="card"><h3>Could not reach the router API</h3><pre>${h(e.message)}</pre><p class="muted small">Is the dashboard mapped (<code>app.MapRouterDashboard()</code>) and are you browsing from the same machine?</p></div>`; });
  setInterval(() => { if (state.autoRefresh && document.visibilityState === 'visible' && !isDirty()) refreshStatus(); else if (state.autoRefresh && document.visibilityState === 'visible') { api('GET', '/status').then((s) => { state.status = s; renderChrome(); }).catch(() => {}); } }, 5000);
})();
