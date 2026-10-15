/* Semantic Router dashboard: Memory page: memory sources as a graph, coloured by where their notes have been sent. */
import { $, api, csv, h, readOnly, state, toast, views, when } from './core.js';
import { appliedExposure, compact, isLocalModel, updateLive } from './live.js';
import { render } from './shell.js';

/* ---------------------------------------------------------------------------------------------
   Memory view: the notes of every memory source as a graph, coloured by where they have been sent.
   --------------------------------------------------------------------------------------------- */
export let graph = null;
export const palette = { accent: '#228fa7', signal: '#d08c34', faint: '#66757e', muted: '#94a3ac' };
export const CATEGORY_COLORS = ['#228fa7', '#d08c34', '#8a7fd1', '#4fae7a', '#c75b7a', '#6f9fd8', '#b0a03a', '#9a6b4f', '#5bb0a6', '#c48ad6'];
export const MEMORY_TYPES = ['Markdown', 'ClaudeCode', 'KnowledgeGraph'];
export const MEMORY_PATH_HINTS = {
  Markdown: 'C:\\Users\\me\\Documents\\Obsidian\\Vault  or  ~/notes',
  ClaudeCode: '~/.claude (default)',
  KnowledgeGraph: '~/memory.jsonl  (the MCP memory server\'s MEMORY_FILE_PATH)',
};

export function readPalette() {
  const css = getComputedStyle(document.documentElement);
  for (const [k, v] of [['accent', '--accent'], ['signal', '--signal'], ['faint', '--text-faint'], ['muted', '--text-muted']]) {
    palette[k] = (css.getPropertyValue(v) || '').trim() || palette[k];
  }
}

export const exposureOf = (id) => (state.memory && state.memory.exposure[id]) || null;
export function shareClass(n) {
  if (n.missing) return 'missing';
  const e = exposureOf(n.id);
  if (!e) return 'none';
  if (e.cloudContent) return 'cloud';
  if (e.cloudReference) return 'cloud-ref';
  if (e.localContent || e.localReference) return 'local';
  return 'none';
}
export const hashColor = (text) => { let x = 0; for (const c of text || '') x = (x * 31 + c.charCodeAt(0)) | 0; return CATEGORY_COLORS[Math.abs(x) % CATEGORY_COLORS.length]; };

export function nodeStyle(n) {
  if (n.missing) return { fill: null, stroke: palette.faint, strokeWidth: 1.2 };
  const color = state.memoryUi.colorBy;
  if (color === 'source') return { fill: hashColor(n.source) };
  if (color === 'folder') return { fill: hashColor(n.source + '/' + (n.folder || '').split('/')[0]) };
  switch (shareClass(n)) {
    case 'cloud': return { fill: palette.signal };
    case 'cloud-ref': return { fill: palette.faint, stroke: palette.signal, strokeWidth: 2.2 };
    case 'local': return { fill: palette.accent };
    default: return { fill: palette.faint };
  }
}

export function nodeVisible(n) {
  const ui = state.memoryUi;
  if (n.missing && !ui.showMissing) return false;
  const share = shareClass(n);
  if (ui.filter === 'shared' && (share === 'none' || share === 'missing')) return false;
  if (ui.filter === 'cloud' && share !== 'cloud' && share !== 'cloud-ref') return false;
  if (ui.filter === 'local' && share !== 'local') return false;
  if (ui.filter === 'never' && share !== 'none') return false;
  const q = ui.search.trim().toLowerCase();
  if (!q) return true;
  return n.title.toLowerCase().includes(q) || n.path.toLowerCase().includes(q)
    || (n.tags || []).some((t) => t.toLowerCase().includes(q)) || (n.aliases || []).some((a) => a.toLowerCase().includes(q));
}

export async function loadMemory(refresh) {
  if (state.memoryLoading) return;
  state.memoryLoading = true;
  try {
    const data = await api(refresh ? 'POST' : 'GET', refresh ? '/memory/refresh' : '/memory');
    const old = new Map(((state.memory && state.memory.nodes) || []).map((n) => [n.id, n]));
    for (const n of data.nodes) { const o = old.get(n.id); if (o && typeof o.x === 'number') { n.x = o.x; n.y = o.y; n.fx = o.fx; n.fy = o.fy; } }
    state.memory = data;
    // The server's counts already include every request finished so far; only later ones are added from the stream.
    for (const e of state.activity) if (e.totalMilliseconds != null) appliedExposure.add(e.id);
    if (refresh) toast(`Scanned ${data.sources.length} source(s): ${data.nodes.filter((n) => !n.missing).length} notes.`, 'ok');
  } catch (e) {
    toast('Could not load the memory map: ' + e.message, 'danger');
    state.memory = state.memory || { nodes: [], links: [], sources: [], exposure: {}, trackSharing: true, mode: '' };
  } finally {
    state.memoryLoading = false;
  }
  if (state.view === 'memory') render();
}

export function memorySummaryText() {
  const m = state.memory;
  const notes = m.nodes.filter((n) => !n.missing);
  const counts = { cloud: 0, 'cloud-ref': 0, local: 0 };
  for (const n of notes) { const c = shareClass(n); if (c in counts) counts[c]++; }
  return `${notes.length} notes · ${m.links.length} links from ${m.sources.length} source${m.sources.length === 1 ? '' : 's'}${m.loadedAt ? ' · scanned ' + h(when(m.loadedAt)) : ''}
    · <span class="signal-text">${counts.cloud} sent to cloud</span>${counts['cloud-ref'] ? ` · ${counts['cloud-ref']} mentioned to cloud` : ''} · <span class="local-text">${counts.local} local only</span>`;
}
export function updateMemorySummary() { const el = $('#memory-summary'); if (el && state.memory) el.innerHTML = memorySummaryText(); }

views.memory = () => {
  if (!state.memory) {
    loadMemory(false);
    return '<div class="section-head"><h1>Memory</h1><span class="muted">Scanning memory sources…</span></div>';
  }
  const m = state.memory;
  const ui = state.memoryUi;
  const hasNotes = m.nodes.some((n) => !n.missing);
  const admin = !readOnly();
  return `
    <div class="section-head"><h1>Memory</h1><span class="muted small" id="memory-summary">${memorySummaryText()}</span><span class="spacer"></span>
      <button class="btn small" data-action="memory-fit">Fit</button>
      ${admin ? `<button class="btn small" data-action="memory-refresh">Rescan</button>
      <button class="btn small" data-action="memory-reset" title="Forget which notes were sent where">Clear sharing</button>` : ''}</div>
    ${m.mode && m.mode !== 'Dispatch' ? '<div class="card section small" style="border-color:var(--warn)">The gateway is in <b>Annotate</b> mode: your own endpoint calls the model, so the router can show which notes were in a request but not where they went.</div>' : ''}
    ${m.trackSharing === false ? '<div class="card section small" style="border-color:var(--warn)">Sharing is not tracked (<code>Memory:TrackSharing</code> is off): the map shows your notes, but requests are not checked for them.</div>' : ''}
    ${hasNotes ? `
    <div class="memory-toolbar section">
      <input type="text" id="memory-search" placeholder="Search notes, paths, tags…" value="${h(ui.search)}" style="max-width:280px">
      <select id="memory-filter" style="width:170px">${[['all', 'All notes'], ['shared', 'Sent anywhere'], ['cloud', 'Sent to the cloud'], ['local', 'Local only'], ['never', 'Never sent']].map(([v, t]) => `<option value="${v}" ${ui.filter === v ? 'selected' : ''}>${t}</option>`).join('')}</select>
      <select id="memory-color" style="width:170px">${[['sharing', 'Colour by sharing'], ['source', 'Colour by source'], ['folder', 'Colour by folder']].map(([v, t]) => `<option value="${v}" ${ui.colorBy === v ? 'selected' : ''}>${t}</option>`).join('')}</select>
      <label class="check small"><input type="checkbox" id="memory-missing" ${ui.showMissing ? 'checked' : ''}> unresolved links</label>
      <span class="spacer"></span>
      ${ui.colorBy === 'sharing' ? `<span class="share-legend"><i class="sw signal"></i>text sent to cloud <i class="sw ring"></i>mentioned to cloud <i class="sw accent"></i>local only <i class="sw faint"></i>not sent <i class="sw hollow"></i>unresolved</span>` : ''}
    </div>
    <div class="memory-layout section">
      <div class="card graph-card"><canvas id="memory-canvas" aria-label="Memory map"></canvas><div id="graph-tip" class="graph-tip hidden"></div>
        <div class="graph-hint small faint">drag to pan · scroll to zoom · drag a note to pin it · double-click to fit</div></div>
      <div class="card memory-side" id="memory-side">${memorySide()}</div>
    </div>` : memoryEmpty()}
    ${memorySourcesCard()}`;
};

export function memoryEmpty() {
  return `<div class="card section empty-state">
    <h2>Map your memory</h2>
    <p class="muted">Point the router at the notes you and your AI tools keep. It draws them as a graph and, for every routed request, checks which notes were in it and which model received them: local, or the cloud. Note text never leaves the router; the dashboard only gets titles, paths, tags and links.</p>
    ${readOnly() ? '' : `<div class="inline">
      <button class="btn primary small" data-action="memory-add" data-type="Markdown">Add an Obsidian vault or notes folder</button>
      <button class="btn small" data-action="memory-add" data-type="ClaudeCode">Add Claude Code's memory</button>
      <button class="btn small" data-action="memory-add" data-type="KnowledgeGraph">Add an MCP memory graph</button></div>`}
  </div>`;
}

export function memorySide() {
  const m = state.memory;
  const id = state.memoryUi.selected;
  const n = id && m.nodes.find((x) => x.id === id);
  if (!n) {
    const top = m.nodes.filter((x) => exposureOf(x.id))
      .sort((a, b) => { const ea = exposureOf(a.id); const eb = exposureOf(b.id); return (eb.cloudContent * 4 + eb.cloudReference) - (ea.cloudContent * 4 + ea.cloudReference) || new Date(eb.lastShared) - new Date(ea.lastShared); })
      .slice(0, 12);
    return `<h3>Most shared</h3>
      ${top.length ? `<ul class="note-list">${top.map((x) => { const e = exposureOf(x.id); const twin = m.nodes.some((y) => y !== x && !y.missing && y.title.toLowerCase() === x.title.toLowerCase()); return `<li><a href="#/memory" data-action="memory-select" data-id="${h(x.id)}" class="${shareClass(x)}-text" title="${h(x.source + ' · ' + x.path)}">${h(x.title)}${twin ? ` <span class="faint small">${h(x.source)}</span>` : ''}</a><span class="small faint">${e.cloudContent + e.cloudReference ? `cloud ×${e.cloudContent + e.cloudReference}` : ''}${e.localContent + e.localReference ? ` local ×${e.localContent + e.localReference}` : ''}</span></li>`; }).join('')}</ul>`
      : '<p class="muted small">Nothing sent yet. Notes light up here, and pulse on the map, as requests carry them.</p>'}
      <h3 style="margin-top:14px">How notes are spotted</h3>
      <p class="small muted"><b>Text</b>: distinctive lines of a note (${h(String((state.config.Memory || {}).MinimumLineLength || 24))}+ characters) appear in the request, e.g. Claude Code read the file or you pasted it. <b>Mentioned</b>: its path (<code>notes/team.md</code>) or <code>[[title]]</code> appears, without the text. Counts start when the router starts.</p>
      <p class="small faint">Click a note for details.</p>`;
  }
  const e = exposureOf(n.id);
  const idx = m.nodes.indexOf(n);
  const out = m.links.filter((l) => l[0] === idx).map((l) => m.nodes[l[1]]);
  const inn = m.links.filter((l) => l[1] === idx).map((l) => m.nodes[l[0]]);
  const linkList = (list) => list.length ? list.slice(0, 30).map((x) => `<a href="#/memory" data-action="memory-select" data-id="${h(x.id)}" class="note-chip ${shareClass(x) === 'cloud' ? 'cloud' : shareClass(x) === 'local' ? 'local' : 'ref'}">${h(x.title)}</a>`).join('') + (list.length > 30 ? ` <span class="faint small">+${list.length - 30}</span>` : '') : '<span class="faint small">none</span>';
  return `<div class="inline" style="justify-content:space-between"><h3 style="margin:0">${h(n.title)}</h3><button class="btn ghost small" data-action="memory-select" data-id="">✕</button></div>
    ${n.missing ? '<p class="small muted">Linked to, but no note has this title.</p>' : `<div class="small faint mono" style="word-break:break-all">${h(n.source)} · ${h(n.path)}</div>`}
    ${(n.tags || []).length ? `<div class="inline" style="margin-top:6px">${n.tags.map((t) => `<span class="badge">#${h(t)}</span>`).join('')}</div>` : ''}
    ${(n.aliases || []).length ? `<div class="small muted" style="margin-top:4px">also: ${n.aliases.map(h).join(', ')}</div>` : ''}
    ${n.missing ? '' : `<h3 style="margin-top:12px">Where it went</h3>
    ${e ? `<table class="compact"><tr><th></th><th class="num">text</th><th class="num">mentioned</th></tr>
      <tr><td><span class="signal-text">cloud</span></td><td class="num">${e.cloudContent}</td><td class="num">${e.cloudReference}</td></tr>
      <tr><td><span class="local-text">local</span></td><td class="num">${e.localContent}</td><td class="num">${e.localReference}</td></tr></table>
      <div class="small" style="margin-top:6px">${Object.entries(e.models).sort((a, b) => b[1] - a[1]).map(([name, c]) => `<span class="badge ${isLocalModel(name) ? 'accent' : 'signal'}">${h(name)} ×${c}</span>`).join(' ')}</div>
      <div class="small faint" style="margin-top:4px">first ${h(when(e.firstShared))} · last ${h(when(e.lastShared))}</div>`
      : '<p class="small muted">Not sent to any model since the router started.</p>'}
    <p class="small faint" style="margin-top:6px">${n.fingerprints ? `${n.fingerprints} distinctive line${n.fingerprints === 1 ? '' : 's'} identify its text in a request.` : 'Too short to recognise by its text; only mentions of it are detected.'} ${n.size ? compact(n.size) + ' characters.' : ''}</p>`}
    <h3 style="margin-top:12px">Links to</h3><div class="chips">${linkList(out)}</div>
    <h3 style="margin-top:10px">Linked from</h3><div class="chips">${linkList(inn)}</div>`;
}
export function updateMemorySide() { const el = $('#memory-side'); if (el && state.memory) el.innerHTML = memorySide(); }

export function memorySourcesCard() {
  const m = state.memory;
  const cfg = state.config.Memory || { Sources: [] };
  const status = new Map((m.sources || []).map((s) => [s.name.toLowerCase(), s]));
  const sources = cfg.Sources || [];
  const ro = readOnly();
  const custom = (m.sources || []).filter((s) => !sources.some((c) => (c.Name || '').toLowerCase() === s.name.toLowerCase()));
  return `<div class="card section">
    <div class="card-head"><h2>Memory sources</h2><span class="muted small">Changes apply with the save bar, like any setting.</span><span class="spacer"></span>
      ${ro ? '' : `<button class="btn small" data-action="memory-add" data-type="Markdown">Add folder</button><button class="btn small" data-action="memory-add" data-type="ClaudeCode">Add Claude Code</button><button class="btn small" data-action="memory-add" data-type="KnowledgeGraph">Add MCP graph</button>`}</div>
    ${sources.map((src, i) => {
      const key = 'mem:' + i;
      const st = status.get((src.Name || '').toLowerCase());
      const type = src.Type || 'Markdown';
      return `<div class="item ${state.open.has(key) ? 'open' : ''} ${src.Enabled === false ? 'disabled' : ''}" data-key="${key}">
        <div class="item-head" data-action="toggle" data-key="${key}" role="button" tabindex="0" aria-expanded="${state.open.has(key)}"><span class="chev">▶</span><span class="title">${h(src.Name || '(unnamed)')}</span>
          <span class="meta"><span class="badge">${h(type)}</span><span class="mono">${h(src.Path || (type === 'ClaudeCode' ? '~/.claude' : ''))}</span>
          ${st ? (st.error ? `<span class="badge danger" title="${h(st.error)}">error</span>` : `<span class="badge ok">${st.notes} notes</span>${st.skipped ? `<span class="badge warn">${st.skipped} over the limit</span>` : ''}`) : '<span class="badge info">not scanned</span>'}</span>
          ${st && st.error ? `<span class="spacer"></span><span class="small" style="color:var(--danger)">${h(st.error)}</span>` : ''}</div>
        <div class="item-body"><div class="form-grid">
          <label class="field"><span class="name">Name</span><input type="text" data-bind="Memory.Sources.${i}.Name" value="${h(src.Name)}"></label>
          <label class="field"><span class="name">Type</span><select data-bind="Memory.Sources.${i}.Type">${MEMORY_TYPES.map((t) => `<option ${t === type ? 'selected' : ''}>${t}</option>`).join('')}</select></label>
          <label class="field wide"><span class="name">Path</span><input type="text" data-bind="Memory.Sources.${i}.Path" data-kind="nulltext" value="${h(src.Path || '')}" placeholder="${h(MEMORY_PATH_HINTS[type] || '')}">
            <span class="hint">${type === 'Markdown' ? 'A folder of notes, read recursively; dot-folders (.obsidian, .git, .trash) are skipped.' : type === 'ClaudeCode' ? 'Reads CLAUDE.md and every project\'s auto-memory folder (projects/*/memory). Add a repository\'s CLAUDE.md files as a Markdown folder.' : 'The JSONL file of @modelcontextprotocol/server-memory, or the folder holding memory.jsonl.'} <code>~</code> and <code>%VARIABLES%</code> are expanded. A new path is set in the configuration file, unless <code>Dashboard:AllowProcessSettings</code> is on.</span></label>
          ${type === 'Markdown' ? `<label class="field"><span class="name">Exclude</span><input type="text" data-bind="Memory.Sources.${i}.Exclude" data-kind="csv" value="${h(csv(src.Exclude))}" placeholder="Templates/*, *.excalidraw.md"></label>
          <label class="field"><span class="name">Extensions</span><input type="text" data-bind="Memory.Sources.${i}.Extensions" data-kind="csv" value="${h(csv(src.Extensions))}" placeholder=".md"></label>` : ''}
          <label class="check wide"><input type="checkbox" data-bind="Memory.Sources.${i}.Enabled" data-kind="bool" ${src.Enabled !== false ? 'checked' : ''}> Enabled</label>
        </div>${ro ? '' : `<div class="row-actions" style="margin-top:10px"><button class="btn danger small" data-action="memory-remove" data-index="${i}">Remove</button></div>`}</div></div>`;
    }).join('') || '<p class="muted small">No sources configured.</p>'}
    ${custom.length ? `<p class="small muted">Registered in code: ${custom.map((s) => `<span class="badge">${h(s.name)} · ${h(s.kind)} · ${s.error ? 'error' : s.notes + ' notes'}</span>`).join(' ')}</p>` : ''}
    <div class="form-grid" style="margin-top:10px">
      <label class="check"><input type="checkbox" data-bind="Memory.TrackSharing" data-kind="bool" ${cfg.TrackSharing !== false ? 'checked' : ''}> Check requests for notes</label>
      <label class="field"><span class="name">Shortest matching line</span><input type="number" min="8" data-bind="Memory.MinimumLineLength" data-kind="number" value="${cfg.MinimumLineLength || 24}"><span class="hint">Characters; shorter lines are too common to identify a note.</span></label>
      <label class="field"><span class="name">Rescan every</span><input type="text" data-bind="Memory.RefreshInterval" value="${h(cfg.RefreshInterval || '00:01:00')}"><span class="hint">hh:mm:ss, checked when requests arrive.</span></label>
    </div>
  </div>`;
}

export function showTip(node, x, y) {
  const tip = $('#graph-tip');
  if (!tip) return;
  if (!node) { tip.classList.add('hidden'); return; }
  const e = exposureOf(node.id);
  tip.innerHTML = `<b>${h(node.title)}</b><div class="faint">${node.missing ? 'unresolved link' : h(node.source + ' · ' + node.path)}</div>
    <div>${e ? `${e.cloudContent + e.cloudReference ? `<span class="signal-text">cloud ×${e.cloudContent + e.cloudReference}</span> ` : ''}${e.localContent + e.localReference ? `<span class="local-text">local ×${e.localContent + e.localReference}</span>` : ''}` : (node.missing ? '' : '<span class="faint">not sent</span>')}</div>`;
  tip.style.left = Math.min(x + 14, tip.parentElement.clientWidth - 240) + 'px';
  tip.style.top = (y + 14) + 'px';
  tip.classList.remove('hidden');
}

export const mounts = {
  live() { updateLive(); },
  memory() {
    const canvas = $('#memory-canvas');
    if (!canvas || !state.memory || !window.MemoryGraph) return;
    readPalette();
    graph = window.MemoryGraph.create(canvas, {
      onSelect(n) { state.memoryUi.selected = n ? n.id : null; updateMemorySide(); },
      onHover: showTip,
    });
    graph.setStyle(nodeStyle);
    graph.setVisible(nodeVisible);
    graph.setData(state.memory.nodes, state.memory.links);
    if (state.memoryUi.selected) { graph.select(state.memoryUi.selected); graph.centerOn(state.memoryUi.selected); }
  },
};

export function unmount() { if (graph) { graph.destroy(); graph = null; } }
