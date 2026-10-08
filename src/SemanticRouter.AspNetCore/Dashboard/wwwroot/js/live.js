/* Semantic Router dashboard: Live stream (server-sent events) and the Live page. */
import { H, W } from './charts.js';
import { $, API, h, num, state, views, when } from './core.js';
import { graph, palette, updateMemorySide, updateMemorySummary } from './memory.js';
import { render } from './shell.js';

/* ---------------------------------------------------------------------------------------------
   Live stream: server-sent events from api/events keep the activity list current between polls.
   --------------------------------------------------------------------------------------------- */
export const appliedExposure = new Set();   // entry ids whose memory notes were already counted locally
export let liveFrame = 0;
export let activityRenderTimer = null;

export const modelByName = (name) => (state.status && name ? state.status.models.find((m) => m.name.toLowerCase() === String(name).toLowerCase()) : null);
export const isLocalModel = (name) => { const m = modelByName(name); return !!(m && m.isLocal); };
export const clientLabel = (e) => e.client || (e.protocol === 'Anthropic' ? 'Anthropic clients' : 'OpenAI clients');
export const compact = (n) => (n >= 1e6 ? (n / 1e6).toFixed(1) + 'M' : n >= 1e4 ? Math.round(n / 1e3) + 'k' : n >= 1e3 ? (n / 1e3).toFixed(1) + 'k' : String(n));
export const money = (v) => (v === 0 ? '$0' : v < 0.01 ? '<$0.01' : '$' + v.toFixed(2));

export function connectStream() {
  if (!window.EventSource || state.stream) return;
  const source = new EventSource(API + '/events');
  state.stream = source;
  source.onopen = () => { state.streamState = 'live'; scheduleLive(); };
  source.onerror = () => { state.streamState = source.readyState === EventSource.CLOSED ? 'off' : 'reconnecting'; scheduleLive(); };
  source.addEventListener('activity', (ev) => {
    let entry;
    try { entry = JSON.parse(ev.data); } catch { return; }
    upsertActivity(entry);
    if (entry.totalMilliseconds != null) {
      applyExposure(entry);
      if (entry.servedBy || entry.plannedModel) state.live.flash = { model: entry.servedBy || entry.plannedModel, client: clientLabel(entry), until: Date.now() + 1600 };
    }
    if (state.view === 'live') scheduleLive();
    else if (state.view === 'activity' && !activityRenderTimer) activityRenderTimer = setTimeout(() => { activityRenderTimer = null; if (state.view === 'activity') render(); }, 800);
  });
}

export function upsertActivity(entry) {
  const i = state.activity.findIndex((x) => x.id === entry.id);
  if (i >= 0) { state.activity[i] = entry; return; }
  state.activity.unshift(entry);
  state.activity.sort((a, b) => b.id - a.id);
  if (state.activity.length > 500) state.activity.length = 500;
}

/* Counts a finished request's notes against the models it reached, as the server does, so the map updates live. */
export function applyExposure(entry) {
  if (!entry.memory || !entry.memory.length || !entry.sentTo || !entry.sentTo.length || appliedExposure.has(entry.id)) return;
  appliedExposure.add(entry.id);
  const m = state.memory;
  for (const ref of entry.memory) {
    let cloud = false;
    for (const model of new Set(entry.sentTo)) {
      const local = isLocalModel(model);
      cloud = cloud || !local;
      if (!m) continue;
      const x = m.exposure[ref.id] || (m.exposure[ref.id] = { localContent: 0, localReference: 0, cloudContent: 0, cloudReference: 0, firstShared: entry.timestamp, lastShared: entry.timestamp, models: {} });
      const key = (local ? 'local' : 'cloud') + (ref.kind === 'Content' ? 'Content' : 'Reference');
      x[key]++;
      x.lastShared = entry.timestamp;
      x.models[model] = (x.models[model] || 0) + 1;
    }
    if (graph && !matchMedia('(prefers-reduced-motion: reduce)').matches) graph.pulse(ref.id, cloud ? palette.signal : palette.accent);
  }
  if (state.view === 'memory') { if (graph) graph.redraw(); updateMemorySide(); updateMemorySummary(); }
}

/* ---------------------------------------------------------------------------------------------
   Live view: who is asking, which model answers, and which memory crosses to the cloud.
   --------------------------------------------------------------------------------------------- */
export function scheduleLive() {
  if (liveFrame) return;
  liveFrame = requestAnimationFrame(() => { liveFrame = 0; updateLive(); });
}

export function liveStats() {
  let served = 0; let local = 0; let failed = 0; let tokLocal = 0; let tokCloud = 0; let cost = 0;
  const cloudContent = new Set(); const cloudRefs = new Set(); const localNotes = new Set();
  for (const e of state.activity) {
    if (e.outcome === 'failed') failed++;
    const m = modelByName(e.servedBy);
    if (m) {
      served++;
      // Reported usage when the backend sent it, otherwise the router's own estimate.
      const t = e.inputTokens != null ? e.inputTokens + (e.cacheReadTokens || 0) + (e.cacheCreationTokens || 0) : (e.estimatedInputTokens || 0);
      if (m.isLocal) { local++; tokLocal += t; } else { tokCloud += t; cost += e.costUsd != null ? e.costUsd : t * (m.inputCostPerMillionTokens || 0) / 1e6; }
    }
    const sent = e.sentTo || [];
    const toCloud = sent.some((n) => !isLocalModel(n));
    for (const r of e.memory || []) {
      if (toCloud) (r.kind === 'Content' ? cloudContent : cloudRefs).add(r.id);
      else if (sent.length) localNotes.add(r.id);
    }
  }
  for (const id of cloudContent) cloudRefs.delete(id);
  return { total: state.activity.length, served, local, failed, tokLocal, tokCloud, cost, cloudContent: cloudContent.size, cloudRefs: cloudRefs.size, localNotes: localNotes.size };
}

views.live = () => `
  <div class="section-head"><h1>Live</h1><span class="muted">What the router is doing, as it happens.</span><span class="spacer"></span><span id="live-stream"></span></div>
  <div class="grid cols-4 section" id="live-tiles"></div>
  <div class="live-grid section">
    <div class="card"><div class="card-head"><h2>Model router</h2><span class="spacer"></span><span class="share-legend"><i class="sw accent"></i>local <i class="sw signal"></i>cloud</span></div><div id="live-graph"></div></div>
    <div class="card"><div class="card-head"><h2>Decisions</h2><span class="muted small">newest first</span><span class="spacer"></span><a href="#/activity" class="small">table →</a></div><div class="feed" id="live-feed"></div></div>
  </div>
  <div class="card section"><div class="card-head"><h2>Memory in requests</h2><span class="muted small">Notes from your memory sources found in what was sent, and where it went.</span><span class="spacer"></span><a href="#/memory" class="small">memory map →</a></div><div id="live-memory"></div></div>`;

export function updateLive() {
  if (state.view !== 'live' || !$('#live-tiles') || !state.status) return;
  const st = liveStats();
  const stream = state.streamState === 'live' ? '<span class="pill"><span class="dot ok"></span> streaming</span>'
    : state.streamState === 'reconnecting' ? '<span class="pill"><span class="dot warn"></span> reconnecting</span>'
      : '<span class="pill"><span class="dot"></span> polling every 5 s</span>';
  $('#live-stream').innerHTML = stream;
  const localShare = st.served ? Math.round(st.local / st.served * 100) : 0;
  $('#live-tiles').innerHTML = `
    <div class="card stat"><span class="value">${st.total}</span><span class="label">requests in view · ${st.failed} failed</span></div>
    <div class="card stat"><span class="value">${st.served ? localShare + '%' : '—'}</span><span class="label">answered locally (${st.local} of ${st.served})</span></div>
    <div class="card stat"><span class="value">${compact(st.tokCloud)}<span class="muted" style="font-size:14px"> / ${compact(st.tokLocal)}</span></span><span class="label">input tokens to cloud / local · ${money(st.cost)}</span></div>
    <div class="card stat"><span class="value ${st.cloudContent ? 'signal-text' : ''}">${st.cloudContent}</span><span class="label">notes whose text went to the cloud${st.cloudRefs ? ` · ${st.cloudRefs} mentioned` : ''}</span></div>`;
  $('#live-graph').innerHTML = routerGraph();
  $('#live-feed').innerHTML = liveFeed();
  $('#live-memory').innerHTML = liveMemory();
}

export function routerGraph() {
  const s = state.status;
  const clientCounts = new Map(); const modelCounts = new Map(); const failures = new Map();
  for (const e of state.activity) {
    const c = clientLabel(e);
    clientCounts.set(c, (clientCounts.get(c) || 0) + 1);
    const m = e.servedBy || (e.outcome === 'planned' ? e.plannedModel : null);
    if (m) modelCounts.set(m, (modelCounts.get(m) || 0) + 1);
    for (const t of e.sentTo || []) if (t !== e.servedBy) failures.set(t, (failures.get(t) || 0) + 1);
  }
  const models = s.models.filter((m) => m.enabled).map((m) => m.name);
  for (const m of modelCounts.keys()) if (!models.some((x) => x.toLowerCase() === m.toLowerCase())) models.push(m);
  const clients = clientCounts.size ? [...clientCounts.keys()] : ['waiting for clients'];
  const rows = Math.max(clients.length, models.length, 3);
  const W = 680; const H = Math.max(230, rows * 60 + 24); const mid = H / 2;
  const y = (i, n) => mid + (i - (n - 1) / 2) * 60;
  const maxC = Math.max(1, ...clientCounts.values()); const maxM = Math.max(1, ...modelCounts.values());
  const flash = state.live.flash && Date.now() < state.live.flash.until ? state.live.flash : null;
  const trunc = (t, n) => (t.length > n ? t.slice(0, n - 1) + '…' : t);
  let out = `<svg class="router-graph" viewBox="0 0 ${W} ${H}" role="img" aria-label="Clients, the router and the models it sends to">`;
  clients.forEach((c, i) => {
    const cy = y(i, clients.length); const count = clientCounts.get(c) || 0;
    const hot = flash && flash.client === c;
    out += `<path class="edge ${count ? '' : 'idle'} ${hot ? 'flow' : ''}" d="M170 ${cy} C 220 ${cy}, 220 ${mid}, 270 ${mid}" stroke-width="${1.2 + 5 * count / maxC}"/>`;
    out += `<g class="gnode client"><rect x="10" y="${cy - 22}" width="160" height="44" rx="8"/><text x="22" y="${cy - 3}" class="t1">${h(trunc(c, 20))}</text><text x="22" y="${cy + 13}" class="t2">${count ? count + ' request' + (count === 1 ? '' : 's') : 'none yet'}</text></g>`;
  });
  models.forEach((name, i) => {
    const cy = y(i, models.length); const m = modelByName(name); const count = modelCounts.get(name) || 0;
    const local = m && m.isLocal; const cooling = m && m.coolingDownUntil;
    const hot = flash && flash.model.toLowerCase() === name.toLowerCase();
    out += `<path class="edge ${local ? 'local' : 'cloud'} ${count ? '' : 'idle'} ${hot ? 'flow' : ''}" d="M410 ${mid} C 455 ${mid}, 455 ${cy}, 500 ${cy}" stroke-width="${1.2 + 5 * count / maxM}"/>`;
    out += `<g class="gnode model ${local ? 'local' : 'cloud'} ${cooling ? 'cooling' : ''} ${hot ? 'hot' : ''}"><title>${h(name)}${m ? ` · ${h(m.connector)} · ${h(m.model || '')}` : ''}</title><rect x="500" y="${cy - 25}" width="170" height="50" rx="8"/>
      <text x="512" y="${cy - 5}" class="t1">${h(trunc(name, 19))}</text>
      <text x="512" y="${cy + 12}" class="t2">${m ? 'T' + m.tier + ' · ' : ''}${local ? 'local' : 'cloud'} · ×${count}${failures.get(name) ? ' · ' + failures.get(name) + ' failed' : ''}${cooling ? ' · cooling' : ''}</text></g>`;
  });
  out += `<g class="gnode router"><rect x="270" y="${mid - 34}" width="140" height="68" rx="10"/><text x="340" y="${mid - 6}" text-anchor="middle" class="t1">Semantic Router</text><text x="340" y="${mid + 12}" text-anchor="middle" class="t2">${h(s.gateway.strategy)}</text></g>`;
  return out + '</svg>';
}

export function liveFeed() {
  const rows = state.activity.slice(0, 80);
  if (!rows.length) return '<div class="muted small">Nothing routed yet. Point a client at the gateway (see <a href="#/clients">Connect clients</a>) and requests appear here as they arrive.</div>';
  return rows.map((e) => {
    const model = e.servedBy || e.plannedModel;
    const local = isLocalModel(model);
    const pending = e.totalMilliseconds == null;
    const sent = e.sentTo || [];
    const toCloud = sent.some((n) => !isLocalModel(n));
    const notes = (e.memory || []);
    return `<div class="feed-row ${e.outcome === 'failed' ? 'failed' : ''} ${pending ? 'pending' : ''}">
      <span class="t">${h(when(e.timestamp))}</span>
      <span class="who">${h(clientLabel(e))}</span>
      <span class="arrow">→</span>
      ${e.outcome === 'failed' ? '<span class="badge danger">failed</span>' : `<span class="model ${local ? 'local' : 'cloud'}">${h(model || '—')}</span>`}
      <span class="tier">T${e.tier}</span>
      <span class="why">${h(e.reason)}${e.route ? ' · ' + h(e.route) + (e.similarity != null ? ' ' + num(e.similarity, 2) : '') : ''}${e.hasTools ? ' · tools' : ''}${e.toolContinuation ? ' · tool result' : ''}${e.attempt > 1 ? ' · try ' + e.attempt : ''}${e.degraded ? ' · degraded' : ''}</span>
      <span class="ms">${pending ? '…' : num(e.totalMilliseconds, 0) + ' ms'}</span>
      <div class="prompt">${h(e.promptPreview)}</div>
      ${notes.length ? `<div class="mem">${notes.slice(0, 6).map((r) => `<a href="#/memory" data-action="memory-open" data-id="${h(r.id)}" class="note-chip ${r.kind === 'Content' ? (toCloud ? 'cloud' : 'local') : 'ref'}" title="${h(r.source)} · ${r.kind === 'Content' ? r.lines + ' of ' + r.totalLines + ' lines' : 'mentioned'}">${h(r.title)}</a>`).join('')}${notes.length > 6 ? ` <span class="faint">+${notes.length - 6}</span>` : ''}</div>` : ''}
    </div>`;
  }).join('');
}

export function liveMemory() {
  const cards = [];
  for (const e of state.activity) {
    if (!e.memory || !e.memory.length || !(e.sentTo || []).length) continue;
    for (const r of e.memory) {
      cards.push({ e, r });
      if (cards.length >= 16) break;
    }
    if (cards.length >= 16) break;
  }
  if (!cards.length) {
    return (state.memory && state.memory.nodes.length) || ((state.config.Memory || {}).Sources || []).length
      ? '<div class="muted small">No notes from your memory sources have been sent yet. When a request carries one (Claude Code reading a memory file, a note pasted into a prompt, a <code>[[link]]</code>), it shows up here with the model it went to.</div>'
      : '<div class="muted small">No memory sources configured. Add an Obsidian vault, Claude Code\'s memory or an MCP memory graph on the <a href="#/memory">Memory</a> page to see which notes reach which models.</div>';
  }
  return `<div class="mem-strip">${cards.map(({ e, r }) => {
    const targets = [...new Set(e.sentTo)];
    const cloud = targets.some((n) => !isLocalModel(n));
    return `<a href="#/memory" class="mem-card ${cloud ? 'cloud' : 'local'}" data-action="memory-open" data-id="${h(r.id)}">
      <span class="small faint">${h(when(e.timestamp))} · ${h(r.source)}</span>
      <b>${h(r.title)}</b>
      <span class="small">${r.kind === 'Content' ? `text (${r.lines}/${r.totalLines} lines)` : 'mentioned'} → ${targets.map((n) => `<span class="${isLocalModel(n) ? 'local' : 'cloud'}-text">${h(n)}</span>`).join(', ')}</span>
    </a>`;
  }).join('')}</div>`;
}
