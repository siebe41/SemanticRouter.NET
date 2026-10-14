/* Semantic Router dashboard: Configuration pages: tiers, routes, models, connectors, routing rules, embeddings, gateway, clients, history. */
import { connectorStateBadge } from './endpoints.js';
import { reliabilityBadges, reliabilityCard } from './reliability.js';
import { CAPABILITY_HINTS, CONNECTOR_TYPES, MESSAGE_SELECTION, PRESETS, SCORING, STRATEGIES, connectorNames, csv, h, isReference, kv, lineupFor, lines, readOnly, state, tiers, views } from './core.js';

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

export const tierOptions = (value) => tiers().map((t) => `<option value="${t.Level}" ${t.Level === value ? 'selected' : ''}>${t.Level} · ${h(t.Name)}</option>`).join('') + (tiers().some((t) => t.Level === value) ? '' : `<option value="${value}" selected>${value}</option>`);

views.routes = () => {
  const routes = state.config.Routes || [];
  return `
    <div class="section-head"><h1>Routes</h1><span class="muted">Intent clusters: example prompts and the tier they need.</span><span class="spacer"></span><button class="btn primary small" data-action="route-add">Add route</button></div>
    ${routes.map((r, i) => {
      const key = 'route:' + i;
      return `<div class="item ${state.open.has(key) ? 'open' : ''}" data-key="${key}">
        <div class="item-head" data-action="toggle" data-key="${key}" role="button" tabindex="0" aria-expanded="${state.open.has(key)}">
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

const modelItem = (m, i) => {
  const key = 'model:' + i;
  const names = connectorNames();
  const conn = state.config.Connectors[m.Connector];
  const free = !(m.InputCostPerMillionTokens > 0 || m.OutputCostPerMillionTokens > 0);
  return `<div class="item ${state.open.has(key) ? 'open' : ''} ${m.Enabled === false ? 'disabled' : ''}" data-key="${key}">
    <div class="item-head" data-action="toggle" data-key="${key}" role="button" tabindex="0" aria-expanded="${state.open.has(key)}">
      <span class="chev">▶</span><span class="title">${h(m.Name || '(unnamed)')}</span>
      <span class="meta"><span class="tier">T${m.Tier}</span><span class="mono">${h(m.Model || '')}</span><span>via ${h(m.Connector)}${conn ? ` <span class="faint">(${h(conn.Type || 'OpenAICompatible')}${conn.Type === 'Cli' ? '/' + h(conn.Preset || 'Custom') : ''})</span>` : ' <span class="badge danger">unknown connector</span>'}</span>
      ${m.IsLocal ? '<span class="badge info">local</span>' : ''}${reliabilityBadges(m.Name)}${free ? '<span class="badge ok">free</span>' : `<span class="badge">$${m.InputCostPerMillionTokens}/$${m.OutputCostPerMillionTokens} per M</span>`}${(m.Capabilities || []).map((c) => `<span class="badge">${h(c)}</span>`).join('')}</span>
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
      <label class="field"><span class="name">Cache read cost ($ / M)</span><input type="number" min="0" step="0.01" data-bind="Models.${i}.CacheReadCostPerMillionTokens" data-kind="float" data-nullable="1" value="${m.CacheReadCostPerMillionTokens ?? ''}" placeholder="input price"><span class="hint">Prompt-cache reads, if the provider prices them differently.</span></label>
      <label class="field"><span class="name">Cache write cost ($ / M)</span><input type="number" min="0" step="0.01" data-bind="Models.${i}.CacheWriteCostPerMillionTokens" data-kind="float" data-nullable="1" value="${m.CacheWriteCostPerMillionTokens ?? ''}" placeholder="input price"></label>
      <label class="field"><span class="name">Priority</span><input type="number" data-bind="Models.${i}.Priority" data-kind="number" value="${m.Priority || 0}"><span class="hint">Lower is preferred; breaks ties.</span></label>
      <label class="field"><span class="name">Active hours</span><input type="text" data-bind="Models.${i}.ActiveHours" data-kind="nulltext" value="${h(m.ActiveHours || '')}" placeholder="always (e.g. 08:00-18:00)"><span class="hint">Outside them the model is not tried. Time zone: Routing rules.</span></label>
      <label class="field wide"><span class="name">Description</span><input type="text" data-bind="Models.${i}.Description" data-kind="nulltext" value="${h(m.Description || '')}"></label>
      <label class="check wide"><input type="checkbox" data-bind="Models.${i}.IsLocal" data-kind="bool" ${m.IsLocal ? 'checked' : ''}> Local: prompts stay on this machine (required to serve local-only routes)</label>
    </div>
    <div class="row-actions" style="margin-top:10px"><button class="btn small" data-action="model-duplicate" data-index="${i}">Duplicate</button><button class="btn danger small" data-action="model-remove" data-index="${i}">Remove</button></div></div>
  </div>`;
};

const LOADING_LABELS = { OnDemand: 'loads models on demand', LoadedOnly: 'serves loaded models only' };
const sizeText = (b) => (b == null ? '' : (b >= 10 ? Math.round(b) : Math.round(b * 10) / 10) + 'B');

/* Discover models: what a connector offers, each with a suggested catalog entry to tick, adjust and add. */
export const lineupPanel = (connector) => {
  const l = lineupFor(connector);
  if (!l) return '';
  const index = state.lineups.indexOf(l);
  const head = `<div class="card-head"><h3>Models ${h(connector)} offers</h3>${l.server ? `<span class="badge accent">${h(l.server)}</span>` : ''}${LOADING_LABELS[l.loading] ? `<span class="badge ${l.loading === 'LoadedOnly' ? 'warn' : 'info'}">${LOADING_LABELS[l.loading]}</span>` : ''}<span class="spacer"></span><button class="btn ghost small" data-action="lineup-close" data-connector="${h(connector)}">Close</button></div>`;
  if (l.busy) return `<div class="card lineup">${head}<p class="muted small">Asking ${h(connector)}…</p></div>`;
  if (l.error) return `<div class="card lineup">${head}<p class="small" style="color:var(--danger)">${h(l.error)}</p></div>`;
  const rows = l.lineup || [];
  const selectable = rows.filter((e) => e.suggestion && !e.inCatalog);
  const selected = selectable.filter((e) => e.selected).length;
  const loadState = (e) => (e.kind === 'Embedding' ? '<span class="badge">embedding</span>'
    : !e.listed ? '<span class="badge warn" title="The server does not serve it until it is loaded">not served</span>'
    : e.loaded === true ? '<span class="badge ok">loaded</span>' : e.loaded === false ? '<span class="badge">not loaded</span>' : '');
  return `<div class="card lineup">${head}
    ${l.loadingNote ? `<p class="muted small">${h(l.loadingNote)}</p>` : ''}${l.note ? `<p class="muted small">${h(l.note)}</p>` : ''}${l.familiesWarning ? `<p class="small" style="color:var(--warn)">${h(l.familiesWarning)}</p>` : ''}
    ${rows.length ? `<div class="table-wrap"><table class="compact lineup-table">
      <tr><th style="width:44px">${selectable.length ? `<button class="btn ghost small" data-action="lineup-all" data-index="${index}" aria-label="${selected === selectable.length ? 'Select none' : 'Select every model not in the catalog'}">${selected === selectable.length ? 'None' : 'All'}</button>` : ''}</th><th>Provider model</th><th>State</th><th>Size</th><th>Context</th><th>Catalog name</th><th>Tier</th><th>Capabilities</th></tr>
      ${rows.map((e, i) => {
        const p = `lineups.${index}.lineup.${i}`;
        const facts = `<td>${loadState(e)}</td><td class="small">${h(sizeText(e.parametersBillions))}</td><td class="small mono">${e.contextLength ? e.contextLength.toLocaleString() : ''}</td>`;
        if (e.inCatalog) return `<tr class="muted"><td></td><td class="mono small">${h(e.id)}</td>${facts}<td colspan="3" class="small">in the catalog as <b>${h(e.inCatalog)}</b></td></tr>`;
        if (!e.suggestion) return `<tr class="muted"><td></td><td class="mono small">${h(e.id)}</td>${facts}<td colspan="3" class="small">${h(e.reason)}</td></tr>`;
        const s = e.suggestion;
        return `<tr><td><input type="checkbox" aria-label="Add ${h(e.id)}" data-state="${p}.selected" data-kind="bool" ${e.selected ? 'checked' : ''}></td>
          <td class="mono small">${h(e.id)}${s.Enabled === false ? ' <span class="badge">added disabled</span>' : ''}</td>${facts}
          <td><input type="text" aria-label="Catalog name for ${h(e.id)}" data-state="${p}.suggestion.Name" value="${h(s.Name)}"></td>
          <td><select aria-label="Tier for ${h(e.id)}" data-state="${p}.suggestion.Tier" data-kind="number">${tierOptions(s.Tier)}</select></td>
          <td><input type="text" aria-label="Capabilities of ${h(e.id)}" data-state="${p}.suggestion.Capabilities" data-kind="csv" value="${h(csv(s.Capabilities))}"></td></tr>
          <tr class="lineup-reason"><td></td><td colspan="7" class="small muted">${TIER_FROM[e.tierFrom] ? `<span class="badge" title="${h(l.families || '')}">${TIER_FROM[e.tierFrom]}</span> ` : ''}${h(e.reason)}</td></tr>`;
      }).join('')}</table></div>` : ''}
    ${selectable.length ? `<div class="row-actions" style="margin-top:10px"><button class="btn primary small" data-action="lineup-add" data-index="${index}" ${selected ? '' : 'disabled'}>Add ${selected} selected to the catalog</button><span class="muted small">Added to the draft; Apply to use them. Context, prices and the rest are on each model.</span></div>` : ''}
  </div>`;
};

/* Where a suggested tier came from. */
const TIER_FROM = { family: 'tier from the family table', size: 'tier from the size', name: 'tier from the name' };

views.models = () => {
  const models = state.config.Models || [];
  const names = connectorNames();
  const groups = [...names, ...[...new Set(models.map((m) => m.Connector))].filter((c) => !names.some((n) => n.toLowerCase() === (c || '').toLowerCase()))];
  const byTier = (a, b) => (a.m.Tier - b.m.Tier) || String(a.m.Name).localeCompare(String(b.m.Name));
  return `
    <div class="section-head"><h1>Models</h1><span class="muted">The catalog the router chooses from, by connector. <b>Discover models</b> lists what a provider offers and suggests a tier for each.</span><span class="spacer"></span><button class="btn primary small" data-action="model-add">Add model</button></div>
    ${reliabilityCard()}
    ${groups.map((connector) => {
      const conn = state.config.Connectors[connector];
      const own = models.map((m, i) => ({ m, i })).filter((x) => (x.m.Connector || '').toLowerCase() === (connector || '').toLowerCase()).sort(byTier);
      const tierCounts = [...new Set(own.map((x) => x.m.Tier))].sort((a, b) => a - b).map((t) => `<span class="tier" title="${own.filter((x) => x.m.Tier === t).map((x) => h(x.m.Name)).join(', ')}">T${t}${own.filter((x) => x.m.Tier === t).length > 1 ? ' ×' + own.filter((x) => x.m.Tier === t).length : ''}</span>`).join('');
      return `<section class="model-group" aria-label="Models via ${h(connector)}">
        <div class="section-head group-head"><h2>${h(connector || '(no connector)')}</h2>${conn ? `<span class="badge accent">${h(conn.Type || 'OpenAICompatible')}${conn.Type === 'Cli' ? ' / ' + h(conn.Preset || 'Custom') : ''}</span>` : '<span class="badge danger">unknown connector</span>'}<span class="muted small">${own.length} model${own.length === 1 ? '' : 's'}</span>${tierCounts}<span class="spacer"></span>${conn ? `<button class="btn small" data-action="lineup-discover" data-connector="${h(connector)}">Discover models</button>` : ''}</div>
        ${lineupPanel(connector)}
        ${own.map((x) => modelItem(x.m, x.i)).join('')}
      </section>`;
    }).join('') || '<div class="card muted">No connectors yet. Add one under <a href="#/connectors">Connectors</a>, then discover its models.</div>'}`;
};

export const secretField = (path, value, label) => `
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
        <div class="item-head" data-action="toggle" data-key="${key}" role="button" tabindex="0" aria-expanded="${state.open.has(key)}">
          <span class="chev">▶</span><span class="title">${h(name)}</span>
          <span class="meta"><span class="badge accent">${h(type)}${type === 'Cli' ? ' / ' + h(c.Preset || 'Custom') : ''}</span><span class="mono">${h(type === 'Cli' ? (c.Executable || '(preset default)') : type === 'Copilot' ? (c.Executable || 'copilot') : (c.BaseAddress || ''))}</span><span>${models.length} model${models.length === 1 ? '' : 's'}</span>${connectorStateBadge(name)}${c.ApiKey === '***' ? '<span class="badge ok">key set</span>' : (isReference(c.ApiKey) ? '<span class="badge ok" title="' + h(c.ApiKey) + '">key: reference</span>' : '')}${type === 'Cli' && c.ToolCalling === 'Prompted' ? '<span class="badge info">prompted tools</span>' : ''}</span>
        </div>
        <div class="item-body"><div class="form-grid">
          <label class="field"><span class="name">Name</span><input type="text" data-action-input="connector-rename" data-name="${h(name)}" value="${h(name)}"><span class="hint">Models refer to it by this name; renaming updates them.</span></label>
          <label class="field"><span class="name">Type</span><select data-bind="${p}.Type">${CONNECTOR_TYPES.map((t) => `<option ${t === type ? 'selected' : ''}>${t}</option>`).join('')}</select><span class="hint">OpenAICompatible: LM Studio, Ollama, vLLM, OpenAI, Gemini, OpenRouter… · Anthropic: Messages API with a key · Responses: OpenAI Responses API (/v1/responses) · Cli: a vendor CLI on your own login · Copilot: GitHub's Copilot SDK on your Copilot sign-in (native tools and streaming).</span></label>
          ${type === 'Cli' ? `
            <label class="field"><span class="name">Preset</span><select data-bind="${p}.Preset">${PRESETS.map((x) => `<option ${x === (c.Preset || 'Custom') ? 'selected' : ''}>${x}</option>`).join('')}</select></label>
            <label class="field"><span class="name">Executable</span><input type="text" data-bind="${p}.Executable" data-kind="nulltext" value="${h(c.Executable || '')}" placeholder="preset default (claude / copilot / codex)"><span class="hint">Started directly, never through a shell. On Windows use the .exe.</span></label>
            <label class="field"><span class="name">Tool calling</span><select data-bind="${p}.ToolCalling">${['None', 'Prompted'].map((x) => `<option ${x === (c.ToolCalling || 'None') ? 'selected' : ''}>${x}</option>`).join('')}</select><span class="hint">Prompted lets agentic clients (Claude Code) use this CLI: tools are described in the prompt and tool calls parsed from the reply.</span></label>
            ${c.ToolCalling === 'Prompted' ? `<label class="check"><input type="checkbox" data-bind="${p}.StreamPromptedReplies" data-kind="bool" ${c.StreamPromptedReplies !== false ? 'checked' : ''}> Stream text before tool calls <span class="muted">— off: buffer the reply, so a failed run can still fail over</span></label>
            <label class="check"><input type="checkbox" data-bind="${p}.RepairToolCalls" data-kind="bool" ${c.RepairToolCalls !== false ? 'checked' : ''}> Repair broken tool calls <span class="muted">— one short extra run</span></label>` : ''}
            <label class="field"><span class="name">Max concurrency</span><input type="number" min="1" data-bind="${p}.MaxConcurrency" data-kind="number" value="${c.MaxConcurrency || 2}"></label>
            <label class="field"><span class="name">Max queue</span><input type="number" min="0" data-bind="${p}.MaxQueue" data-kind="number" data-nullable="1" value="${c.MaxQueue ?? ''}" placeholder="always wait"><span class="hint">When this many requests are already waiting, the next one goes to another model.</span></label>
            <label class="field"><span class="name">Max argument length</span><input type="number" min="100" data-bind="${p}.MaxArgumentLength" data-kind="number" value="${c.MaxArgumentLength || 24000}"><span class="hint">With Prompt input Argument, longer prompts go on stdin.</span></label>
            <label class="field"><span class="name">Timeout (hh:mm:ss)</span><input type="text" data-bind="${p}.Timeout" value="${h(c.Timeout || '00:00:00')}" placeholder="00:05:00"><span class="hint">Time to first output; 00:00:00 = default (5 min).</span></label>
            ${(c.Preset || 'Custom') !== 'Codex' ? `<label class="field"><span class="name">Prompt input</span><select data-bind="${p}.PromptInput">${['Stdin', 'Argument'].map((x) => `<option ${x === (c.PromptInput || 'Stdin') ? 'selected' : ''}>${x}</option>`).join('')}</select><span class="hint">Argument puts the prompt on the command line, where other users of this machine can read it.</span></label>` : ''}
            <label class="field wide"><span class="name">Arguments (one per line)</span><textarea data-bind="${p}.Arguments" data-kind="lines" rows="3">${h(lines(c.Arguments))}</textarea><span class="hint">Presets: extra flags appended (e.g. <code>--effort</code> / <code>low</code>). Custom: the full template with <code>{model}</code> and <code>{prompt}</code>.</span></label>
            <label class="field"><span class="name">Quota error patterns</span><textarea data-bind="${p}.QuotaErrorPatterns" data-kind="lines" rows="3">${h(lines(c.QuotaErrorPatterns))}</textarea><span class="hint">Output fragments that mean “out of quota” (added to the preset's).</span></label>
            <label class="field"><span class="name">Environment variables (KEY=value)</span><textarea data-bind="${p}.EnvironmentVariables" data-kind="kv" rows="3">${h(kv(c.EnvironmentVariables))}</textarea></label>
            <label class="field"><span class="name">Remove environment variables</span><textarea data-bind="${p}.RemoveEnvironmentVariables" data-kind="lines" rows="3">${h(lines(c.RemoveEnvironmentVariables))}</textarea><span class="hint">Empty = preset defaults (API keys that would switch billing away from your subscription).</span></label>
            <label class="check wide"><input type="checkbox" data-bind="${p}.AllowRemoteClients" data-kind="bool" ${c.AllowRemoteClients ? 'checked' : ''}> Allow remote clients <span class="muted">— read docs/TERMS.md first; a personal subscription must not serve other people</span></label>`
          : type === 'Copilot' ? `
            <label class="field"><span class="name">Copilot CLI</span><input type="text" data-bind="${p}.Executable" data-kind="nulltext" value="${h(c.Executable || '')}" placeholder="copilot (from PATH)"><span class="hint">The SDK runs it as its runtime: <code>npm install -g @github/copilot</code>, then sign in once with <code>copilot</code> (/login) or <code>gh auth login</code>.</span></label>
            <label class="field"><span class="name">Copilot home</span><input type="text" data-bind="${p}.HomeDirectory" data-kind="nulltext" value="${h(c.HomeDirectory || '')}" placeholder="~/.copilot"><span class="hint">COPILOT_HOME for the runtime; the default reuses the CLI's login.</span></label>
            <label class="field"><span class="name">Max concurrency</span><input type="number" min="1" data-bind="${p}.MaxConcurrency" data-kind="number" value="${c.MaxConcurrency || 2}"></label>
            <label class="field"><span class="name">Max queue</span><input type="number" min="0" data-bind="${p}.MaxQueue" data-kind="number" data-nullable="1" value="${c.MaxQueue ?? ''}" placeholder="always wait"></label>
            <label class="field"><span class="name">Timeout (hh:mm:ss)</span><input type="text" data-bind="${p}.Timeout" value="${h(c.Timeout || '00:00:00')}" placeholder="00:05:00"><span class="hint">Time to first output; 00:00:00 = default (5 min).</span></label>
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
        <div class="row-actions" style="margin-top:10px"><button class="btn small" data-action="lineup-discover" data-connector="${h(name)}">Discover models</button><button class="btn danger small" data-action="connector-remove" data-name="${h(name)}">Remove</button></div>
        ${lineupPanel(name)}</div>
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
      <label class="field"><span class="name">Strategy</span><select data-bind="Selection.Strategy">${STRATEGIES.map((x) => `<option ${x === (sel.Strategy || 'CheapestCapable') ? 'selected' : ''}>${x}</option>`).join('')}</select><span class="hint">Fastest and Balanced use each model's recent time to first byte. Clients can override per request with auto:cheapest / auto:best / auto:priority / auto:fastest / auto:balanced.</span></label>
      <label class="field"><span class="name">Max attempts</span><input type="number" min="1" data-bind="Selection.MaxAttempts" data-kind="number" value="${sel.MaxAttempts || 3}"></label>
      <label class="field"><span class="name">Quota cooldown</span><input type="text" data-bind="Selection.QuotaCooldown" value="${h(sel.QuotaCooldown || '00:15:00')}"></label>
      <label class="field"><span class="name">Failure cooldown</span><input type="text" data-bind="Selection.FailureCooldown" value="${h(sel.FailureCooldown || '00:00:30')}"></label>
      <label class="field"><span class="name">Time zone</span><input type="text" data-bind="Selection.TimeZone" data-kind="nulltext" value="${h(sel.TimeZone || '')}" placeholder="this machine's"><span class="hint">For model active hours (IANA id, e.g. Europe/Amsterdam).</span></label>
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
  const st = state.status.embeddings || {};
  const available = st.available || ['Hashing', 'OpenAICompatible'];
  const onnxModels = (st.models || []).filter((m) => m.provider === 'Onnx');
  const chosen = onnxModels.find((m) => m.name.toLowerCase() === (e.Model || 'all-MiniLM-L6-v2').toLowerCase());
  const mb = (b) => Math.round(b / 1e6) + ' MB';
  const onnxFields = () => `
        <label class="field"><span class="name">Model</span><select data-bind="Embeddings.Model" data-kind="nulltext">${onnxModels.map((m) => `<option value="${h(m.name)}" ${m === chosen ? 'selected' : ''}>${h(m.name)} (${mb(m.downloadBytes)})</option>`).join('')}</select><span class="hint">${chosen ? h(chosen.description) + ' Downloaded and checksum-verified on first use.' : ''}</span></label>
        <label class="field"><span class="name">Or a model on disk</span><input type="text" data-bind="Embeddings.ModelPath" data-kind="nulltext" value="${h(e.ModelPath || '')}" placeholder="a directory with model.onnx and vocab.txt"><span class="hint">Overrides the model above.</span></label>
        ${e.ModelPath ? `<label class="field"><span class="name">Pooling</span><select data-bind="Embeddings.Pooling" data-kind="nulltext">${['Mean', 'Cls'].map((x) => `<option ${x === (e.Pooling || 'Mean') ? 'selected' : ''}>${x}</option>`).join('')}</select></label>` : ''}
        ${chosen && !e.ModelPath ? `<div class="field wide"><span class="name">Similarity threshold</span><div class="inline"><span class="small">This model's suggested starting threshold is <b>${chosen.suggestedThreshold}</b>; the configuration has ${state.config.DefaultSimilarityThreshold}.</span>${chosen.suggestedThreshold !== state.config.DefaultSimilarityThreshold ? `<button class="btn small" data-action="embeddings-threshold" data-value="${chosen.suggestedThreshold}">Use ${chosen.suggestedThreshold}</button>` : ''}<a class="small" href="#/evaluate">Calibrate on your prompts →</a></div></div>` : ''}`;
  return `
    <div class="section-head"><h1>Embeddings</h1><span class="muted">How prompts and utterances are turned into vectors.</span></div>
    <div class="card section"><div class="form-grid">
      <label class="field"><span class="name">Provider</span><select data-bind="Embeddings.Provider">${available.map((x) => `<option ${x === provider ? 'selected' : ''}>${x}</option>`).join('')}</select><span class="hint">Hashing is lexical (shared words only). Onnx runs a real embedding model in this process; OpenAICompatible calls a server (LM Studio, Ollama, OpenAI).</span></label>
      ${provider === 'Onnx' ? onnxFields() : provider === 'Hashing' ? `<label class="field"><span class="name">Dimensions</span><input type="number" min="16" data-bind="Embeddings.Dimensions" data-kind="number" value="${e.Dimensions || 512}"></label>` : `
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
      <label class="field"><span class="name">Responses paths (one per line)</span><textarea data-bind="Middleware.ResponsesPathPrefixes" data-kind="lines" rows="3" placeholder="/v1/responses">${h(lines(m.ResponsesPathPrefixes))}</textarea><span class="hint">OpenAI Responses API bodies (Codex CLI, the Agents SDK). Empty = <code>/v1/responses</code>.</span></label>
      <label class="field"><span class="name">Max request body (bytes)</span><input type="number" min="1024" data-bind="Middleware.MaxRequestBodyBytes" data-kind="number" value="${m.MaxRequestBodyBytes || 8388608}"><span class="hint">Agentic clients send large system prompts and tool lists; 8 MiB is a safe default.</span></label>
      <label class="field"><span class="name">Header prefix</span><input type="text" data-bind="Middleware.HeaderPrefix" value="${h(m.HeaderPrefix || 'X-Prompt-Route')}"></label>
      <label class="check"><input type="checkbox" data-bind="Middleware.EmitResponseHeaders" data-kind="bool" ${m.EmitResponseHeaders !== false ? 'checked' : ''}> Emit routing headers</label>
      <label class="check"><input type="checkbox" data-bind="Middleware.FailOpen" data-kind="bool" ${m.FailOpen !== false ? 'checked' : ''}> Fail open (fallback tier when classification throws)</label>
      <label class="check"><input type="checkbox" data-bind="Middleware.PinToolCallingTurns" data-kind="bool" ${m.PinToolCallingTurns !== false ? 'checked' : ''}> Pin tool-calling turns to the model that started them</label>
      <label class="field"><span class="name">More injected tags (one per line)</span><textarea data-bind="Middleware.InjectedTags" data-kind="lines" rows="2" placeholder="environment_details">${h(lines(m.InjectedTags))}</textarea><span class="hint">Blocks a client adds to your messages, ignored when classifying. Built in: <code>system-reminder</code>, <code>system_reminder</code>, <code>current_datetime</code>, <code>copilot_tauri_workspace</code>.</span></label>
      <label class="check"><input type="checkbox" data-bind="Middleware.StripSystemReminders" data-kind="bool" ${m.StripSystemReminders !== false ? 'checked' : ''}> Ignore injected context blocks (&lt;system-reminder&gt; and the like) when classifying</label>
    </div></div>
    <div class="card section"><div class="card-head"><h2>Gateway keys</h2><span class="muted small">Clients on other machines must send one (<code>Authorization: Bearer</code> or <code>x-api-key</code>); without keys they are refused. The client name is shown in Activity.</span></div><div class="form-grid">
      <label class="field wide"><span class="name">Keys (client=key, one per line)</span><textarea data-bind="Middleware.GatewayKeys" data-kind="kv" rows="3" placeholder="laptop=\${env:ROUTER_KEY_LAPTOP}">${h(kv(m.GatewayKeys))}</textarea><span class="hint">Stored keys show as <code>***</code> and are kept when left as is. Use <code>\${env:…}</code> references to keep keys out of the file.</span></label>
      <label class="check"><input type="checkbox" data-bind="Middleware.RequireGatewayKeyForLocalClients" data-kind="bool" ${m.RequireGatewayKeyForLocalClients ? 'checked' : ''}> Require a key from this machine too (for a local reverse proxy)</label>
      <label class="check"><input type="checkbox" data-bind="Middleware.AllowUnauthenticatedRemoteClients" data-kind="bool" ${m.AllowUnauthenticatedRemoteClients ? 'checked' : ''}> Serve other machines without a key while none is set (only behind a proxy that authenticates callers)</label>
    </div></div>
    ${rateLimitsCard(m)}
    <div class="card section"><div class="card-head"><h2>Model aliases</h2><span class="muted small">Map the names clients send to routing aliases or catalog models. Globs, first match wins.</span><span class="spacer"></span><button class="btn small" data-action="alias-add">Add alias</button><button class="btn ghost small" data-action="alias-claude">Add Claude Code defaults</button></div>
      <table><tr><th>Client model (glob)</th><th>Routes as</th><th></th></tr>
        ${aliases.map(([k, v], i) => `<tr><td><input type="text" data-action-input="alias-key" data-index="${i}" value="${h(k)}"></td><td><input type="text" data-action-input="alias-value" data-index="${i}" value="${h(v)}" list="alias-targets"></td><td><button class="btn ghost small" data-action="alias-remove" data-index="${i}">remove</button></td></tr>`).join('') || '<tr><td colspan="3" class="muted small">No aliases. Unknown names route as <code>auto</code>.</td></tr>'}
      </table>
      <datalist id="alias-targets">${['auto', 'auto:cheapest', 'auto:best', 'auto:fastest', 'auto:balanced', 'auto:priority'].concat((state.config.Models || []).map((x) => x.Name)).map((x) => `<option value="${h(x)}">`).join('')}</datalist>
    </div>`;
};

/* Rate limits per gateway client ("*" for every other client), with what each client used in the last minute. */
const rateLimitsCard = (m) => {
  const limits = Object.entries(m.RateLimits || {});
  const seen = (state.status && state.status.gateway && state.status.gateway.rateLimits) || [];
  const num = (v) => (v == null ? '' : v);
  const used = (used, max) => (max ? `${Number(used).toLocaleString()} / ${Number(max).toLocaleString()}` : Number(used).toLocaleString());
  return `<div class="card section"><div class="card-head"><h2>Rate limits</h2><span class="muted small">Per gateway client, over a sliding minute. A client over its limit gets a 429 with <code>Retry-After</code>. <code>*</code> covers every client without its own limit, and clients without a key by address.</span><span class="spacer"></span><button class="btn small" data-action="ratelimit-add">Add limit</button></div>
      <table><tr><th>Client</th><th>Requests per minute</th><th>Tokens per minute</th><th></th></tr>
        ${limits.map(([k, v], i) => `<tr><td><input type="text" aria-label="Client" data-action-input="ratelimit-key" data-index="${i}" value="${h(k)}" list="ratelimit-clients"></td><td><input type="number" min="1" aria-label="Requests per minute for ${h(k)}" data-action-input="ratelimit-rpm" data-index="${i}" value="${num(v.RequestsPerMinute)}" placeholder="no limit"></td><td><input type="number" min="1" aria-label="Tokens per minute for ${h(k)}" data-action-input="ratelimit-tpm" data-index="${i}" value="${num(v.TokensPerMinute)}" placeholder="no limit"></td><td><button class="btn ghost small" data-action="ratelimit-remove" data-index="${i}">remove</button></td></tr>`).join('') || '<tr><td colspan="4" class="muted small">No rate limits.</td></tr>'}
      </table>
      <datalist id="ratelimit-clients">${['*'].concat(Object.keys(m.GatewayKeys || {})).map((x) => `<option value="${h(x)}">`).join('')}</datalist>
      ${seen.length ? `<table><tr><th>Client</th><th>Limit</th><th>Requests, last minute</th><th>Tokens, last minute</th><th>Turned away</th></tr>
        ${seen.map((r) => `<tr><td>${h(r.client)}</td><td><code>${h(r.limit)}</code></td><td>${used(r.requests, r.requestsPerMinute)}</td><td>${used(r.tokens, r.tokensPerMinute)}</td><td>${r.limited ? `<span class="badge warn">${Number(r.limited).toLocaleString()}</span> <span class="faint small">last ${h(new Date(r.lastLimited).toLocaleTimeString())}</span>` : '<span class="muted">none</span>'}</td></tr>`).join('')}
      </table>` : ''}
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
      'Works with Aider, Continue, Cline, Open WebUI, LibreChat, the OpenAI CLI and anything else with a configurable base URL. <code>auto:best</code>, <code>auto:cheapest</code>, <code>auto:fastest</code>, <code>auto:balanced</code> and <code>auto:priority</code> pick a strategy; a catalog model name forces that model.')}
    ${snippet('OpenAI Codex CLI', `# ~/.codex/config.toml
model_provider = "semantic-router"
model = "auto"

[model_providers.semantic-router]
name = "Semantic Router"
base_url = "${origin}/v1"
wire_api = "responses"
env_key = "OPENAI_API_KEY"   # export OPENAI_API_KEY=semantic-router`, `Codex speaks the Responses API to <code>${h(s.gateway.responsesPathPrefixes[0] || '/v1/responses')}</code>; any backend can answer it. Hosted tools (web search) need a Responses backend.`)}
    ${snippet('curl', `curl -N ${origin}/v1/chat/completions -H 'Content-Type: application/json' \\
-d '{"model":"auto","stream":true,"messages":[{"role":"user","content":"Compare event sourcing and CRUD"}]}'

curl ${origin}${anth} -H 'Content-Type: application/json' -H 'anthropic-version: 2023-06-01' \\
-d '{"model":"auto","max_tokens":256,"messages":[{"role":"user","content":"hello"}]}'

curl -s ${origin}/semantic-router/classify -H 'Content-Type: application/json' -d '{"prompt":"why does this deadlock?"}'`)}
    <div class="card section"><div class="card-head"><h2>Routing headers</h2></div><p class="muted small">Every response carries <code>${h(s.gateway && state.config.Middleware.HeaderPrefix || 'X-Prompt-Route')}</code>, <code>-Tier</code>, <code>-Reason</code>, <code>-Similarity</code>, <code>-Complexity</code>, <code>-Model</code>, <code>-Connector</code>, <code>-Attempt</code> and <code>-Degraded</code> headers (plus <code>-Dropped</code> when the serving backend could not carry a request feature), so you can see what happened without reading logs.</p></div>`;
};

/* Line diff (longest common subsequence), with unchanged runs folded to three lines of context. */
export function lineDiff(before, after) {
  const a = before.split('\n'); const b = after.split('\n');
  const n = a.length; const m = b.length;
  const lcs = Array.from({ length: n + 1 }, () => new Uint16Array(m + 1));
  for (let i = n - 1; i >= 0; i--) for (let j = m - 1; j >= 0; j--) lcs[i][j] = a[i] === b[j] ? lcs[i + 1][j + 1] + 1 : Math.max(lcs[i + 1][j], lcs[i][j + 1]);
  const out = []; let i = 0; let j = 0;
  while (i < n || j < m) {
    if (i < n && j < m && a[i] === b[j]) { out.push([' ', a[i]]); i++; j++; }
    else if (j < m && (i >= n || lcs[i][j + 1] > lcs[i + 1][j])) { out.push(['+', b[j]]); j++; }
    else { out.push(['-', a[i]]); i++; }
  }
  const keep = out.map((l, k) => l[0] !== ' ' || out.slice(Math.max(0, k - 3), k + 4).some((x) => x[0] !== ' '));
  const lines = []; let skipped = 0;
  out.forEach((l, k) => { if (keep[k]) { if (skipped) { lines.push(`<span class="faint">… ${skipped} unchanged line${skipped === 1 ? '' : 's'}</span>`); skipped = 0; } lines.push(`<span class="${l[0] === '+' ? 'diff-add' : l[0] === '-' ? 'diff-del' : ''}">${h(l[0] + ' ' + l[1])}</span>`); } else skipped++; });
  if (skipped) lines.push(`<span class="faint">… ${skipped} unchanged line${skipped === 1 ? '' : 's'}</span>`);
  return out.some((l) => l[0] !== ' ') ? lines.join('\n') : '<span class="faint">No differences.</span>';
}

export const historyCard = () => {
  const hs = state.history;
  if (!hs.versions) return `<div class="card section"><div class="card-head"><h2>History</h2><span class="spacer"></span><button class="btn small" data-action="history-load">Show applied versions</button></div></div>`;
  return `<div class="card section"><div class="card-head"><h2>History</h2><span class="muted small">Every applied configuration. Compare one with the draft, or roll back to it (applied and saved).</span><span class="spacer"></span><button class="btn ghost small" data-action="history-load">Refresh</button></div>
    <table><tr><th>Version</th><th>When</th><th>What</th><th>Who</th><th></th></tr>
      ${hs.versions.map((v) => `<tr><td class="mono">${v.id}</td><td>${h(new Date(v.timestamp).toLocaleString())}</td><td>${h(v.source)}${v.note ? ' <span class="faint small">' + h(v.note) + '</span>' : ''}</td><td class="small">${h(v.actor || '')}</td>
        <td class="row-actions"><button class="btn small" data-action="history-diff" data-id="${v.id}">Compare with draft</button>${readOnly() ? '' : `<button class="btn ghost small" data-action="history-rollback" data-id="${v.id}">Roll back</button>`}</td></tr>`).join('') || '<tr><td colspan="5" class="muted small">Nothing applied yet.</td></tr>'}
    </table>
    ${hs.diff ? `<h3 style="margin-top:14px">Version ${hs.diff.id} → draft</h3><pre class="diff">${hs.diff.html}</pre>` : ''}
  </div>`;
};
