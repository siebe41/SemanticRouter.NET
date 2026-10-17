/* Semantic Router dashboard: Configuration pages: tiers, routes, models, connectors, routing rules, embeddings, gateway, clients, history. */
import { aliasRows, limitRows, rowProblems } from './draft.js';
import { connectorStateBadge } from './endpoints.js';
import { advanced, durationField, numField, pickList, timeZoneList } from './fields.js';
import { fullStamp, plural, tierChip } from './labels.js';
import { reliabilityBadges, reliabilityCard } from './reliability.js';
import { addConnectorMenu, presetList } from './setup.js';
import { addresses } from './service.js';
import { CAPABILITY_HINTS, CONNECTOR_TYPES, MESSAGE_SELECTION, PRESETS, STRATEGIES, adminOnly, connectorNames, h, isReference, kv, lineupFor, lines, readOnly, state, tiers, views } from './core.js';

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
        ${list ? '<button class="btn small" data-action="tiers-defaults" title="Go back to the built-in ladder">Use defaults</button>' : '<span class="muted small">Using the built-in ladder.</span> <button class="btn small" data-action="tiers-customize">Customize</button>'}
        ${list ? '<button class="btn small" data-action="tier-add">Add tier</button>' : ''}</div>
      ${list ? `<div class="table-wrap"><table><tr><th style="width:80px">Level</th><th style="width:220px">Name</th><th>Description</th><th><span class="visually-hidden">Actions</span></th></tr>
        ${list.map((t, i) => `<tr>
          <td><input type="number" min="0" step="1" aria-label="Level" data-bind="Tiers.${i}.Level" data-kind="number" value="${t.Level}"></td>
          <td><input type="text" aria-label="Name of tier ${h(t.Level)}" data-bind="Tiers.${i}.Name" value="${h(t.Name)}"></td>
          <td><input type="text" aria-label="Description of tier ${h(t.Level)}" data-bind="Tiers.${i}.Description" data-kind="nulltext" value="${h(t.Description || '')}"></td>
          <td><button class="btn danger small" data-action="tier-remove" data-index="${i}">Remove</button></td></tr>`).join('')}</table></div>`
      : `<div class="table-wrap"><table><tr><th style="width:80px">Level</th><th style="width:220px">Name</th><th>Description</th></tr>${effective.map((t) => `<tr><td><span class="tier">T${t.Level}</span></td><td>${h(t.Name)}</td><td class="muted">${h(t.Description || '')}</td></tr>`).join('')}</table></div>`}
    </div>
    <div class="card section">
      <div class="card-head"><h2>Who needs what, who serves what</h2></div>
      <div class="table-wrap"><table class="matrix"><tr><th>Tier</th><th>Routes requiring it</th><th>Models at this tier</th><th>Can serve it (tier ≥)</th></tr>
        ${effective.map((t) => `<tr>
          <td><span class="tier">T${t.Level}</span> ${h(t.Name)}</td>
          <td class="small">${routes.filter((r) => r.Tier === t.Level).map((r) => `<span class="badge">${h(r.Name)}</span>`).join(' ') || '<span class="faint">—</span>'}</td>
          <td class="small">${models.filter((m) => m.Tier === t.Level).map((m) => `<span class="badge ${m.Enabled === false ? '' : 'accent'}">${h(m.Name)}</span>`).join(' ') || '<span class="faint">—</span>'}</td>
          <td class="small">${models.filter((m) => m.Tier >= t.Level && m.Enabled !== false).map((m) => h(m.Name)).join(', ') || '<span class="badge danger">nothing</span>'}</td>
        </tr>`).join('')}
      </table></div>
      <p class="muted small" style="margin-top:8px">Unmatched prompts need tier ${c.FallbackTier}; complexity can raise a prompt to ${(c.ComplexityTiers || []).map((r) => `T${r.Tier} at ≥ ${r.MinScore}`).join(', ') || 'the defaults (2 / 3 / 4)'}. Change these under <a href="#/routing">Rules</a>.</p>
    </div>`;
};

/* The tiers as options. With pick and no value, a disabled first option asks for a choice instead of preselecting one. */
export const tierOptions = (value, pick) => (pick && value == null ? `<option value="" disabled selected>${h(pick)}</option>` : '')
  + tiers().map((t) => `<option value="${t.Level}" ${t.Level === value ? 'selected' : ''}>${t.Level} · ${h(t.Name)}</option>`).join('')
  + (value == null || tiers().some((t) => t.Level === value) ? '' : `<option value="${value}" selected>${value}</option>`);

views.routes = () => {
  const routes = state.config.Routes || [];
  return `
    <div class="section-head"><h1>Routes</h1><span class="muted">Intent clusters: example prompts and the tier they need.</span><span class="spacer"></span><button class="btn primary small" data-action="route-add">Add route</button></div>
    ${routes.map((r, i) => {
      const key = 'route:' + i;
      return `<div class="item ${state.open.has(key) ? 'open' : ''}" data-key="${key}">
        <div class="item-head" data-action="toggle" data-key="${key}" role="button" tabindex="0" aria-expanded="${state.open.has(key)}">
          <span class="chev">▶</span><span class="title">${h(r.Name || '(unnamed)')}</span>
          <span class="meta">${tierChip(r.Tier)}${r.LocalOnly ? '<span class="badge warn">local only</span>' : ''}${r.FollowsConversation ? '<span class="badge" title="In a conversation, prompts matching it take the conversation\'s tier">follows the conversation</span>' : ''}${(r.RequiredCapabilities || []).map((c) => `<span class="badge info">${h(c)}</span>`).join('')}<span>${plural((r.Utterances || []).length, 'utterance')}</span>${r.SimilarityThreshold != null ? `<span>threshold ${r.SimilarityThreshold}</span>` : ''}</span>
          <span class="spacer"></span><span class="muted small">${h(r.Description || '')}</span>
        </div>
        <div class="item-body"><div class="form-grid">
          <label class="field"><span class="name">Name</span><input type="text" data-bind="Routes.${i}.Name" value="${h(r.Name)}"></label>
          <label class="field"><span class="name">Tier</span><select data-bind="Routes.${i}.Tier" data-kind="number">${tierOptions(r.Tier)}</select></label>
          ${pickList({ bind: `Routes.${i}.RequiredCapabilities`, values: r.RequiredCapabilities, known: CAPABILITY_HINTS, label: 'Required capabilities', otherLabel: 'Other required capabilities', otherPlaceholder: 'other tags, comma-separated', hint: 'Tags the model must declare.' })}
          <label class="field"><span class="name">Similarity threshold</span><input type="number" step="0.01" min="-1" max="1" data-bind="Routes.${i}.SimilarityThreshold" data-kind="float" data-nullable="1" value="${r.SimilarityThreshold == null ? '' : r.SimilarityThreshold}" placeholder="default ${state.config.DefaultSimilarityThreshold}"></label>
          <label class="field wide"><span class="name">Description</span><input type="text" data-bind="Routes.${i}.Description" data-kind="nulltext" value="${h(r.Description || '')}"></label>
          <label class="check wide"><input type="checkbox" data-bind="Routes.${i}.LocalOnly" data-kind="bool" ${r.LocalOnly ? 'checked' : ''}> Local only: only models marked <i>local</i> may answer, even on failover</label>
          <label class="check wide"><input type="checkbox" data-bind="Routes.${i}.FollowsConversation" data-kind="bool" ${r.FollowsConversation ? 'checked' : ''}> Follows the conversation: prompts like “go ahead” say nothing about difficulty, so in a conversation they take its tier (this route's tier applies otherwise)</label>
          <label class="field wide"><span class="name">Utterances (one per line)</span><textarea data-bind="Routes.${i}.Utterances" data-kind="lines" rows="6">${h(lines(r.Utterances))}</textarea><span class="hint">5–20 diverse examples route best.</span></label>
        </div>
        <div class="row-actions" style="margin-top:10px"><button class="btn small" data-action="route-duplicate" data-index="${i}">Duplicate</button><button class="btn danger small" data-action="route-remove" data-index="${i}">Remove</button></div></div>
      </div>`;
    }).join('') || '<div class="card muted">No routes yet. Without routes every prompt gets the fallback tier (raised for complexity). Add a route with a few example prompts for each kind of work.</div>'}`;
};

/* The input price with recent prompt-cache reads and writes at the cache prices, next to the list price, when the router knows it and it differs. */
const effectiveCostBadge = (m) => {
  const s = state.status && (state.status.models || []).find((x) => x.name.toLowerCase() === String(m.Name || '').toLowerCase());
  if (!s || s.effectiveInputCostPerMillionTokens == null || !s.cacheShare || s.effectiveInputCostPerMillionTokens === s.inputCostPerMillionTokens) return '';
  const pct = (v) => Math.round(v * 100) + '%';
  return `<span class="badge info" title="Recent turns read ${pct(s.cacheShare.read)} of their input from the prompt cache and wrote ${pct(s.cacheShare.write)}; cost estimates and trials use this price">$${+s.effectiveInputCostPerMillionTokens.toPrecision(3)} input per M with cache (${pct(s.cacheShare.read)} cached)</span>`;
};

const modelItem = (m, i) => {
  const key = 'model:' + i;
  const names = connectorNames();
  const conn = state.config.Connectors[m.Connector];
  const free = !(m.InputCostPerMillionTokens > 0 || m.OutputCostPerMillionTokens > 0);
  // The enabled box sits beside the header, not in it: a control inside the header's button is not reachable as itself.
  return `<div class="item ${state.open.has(key) ? 'open' : ''} ${m.Enabled === false ? 'disabled' : ''}" data-key="${key}">
    <div class="item-bar"><div class="item-head" data-action="toggle" data-key="${key}" role="button" tabindex="0" aria-expanded="${state.open.has(key)}">
      <span class="chev">▶</span><span class="title">${h(m.Name || '(unnamed)')}</span>
      <span class="meta">${tierChip(m.Tier)}<span class="mono">${h(m.Model || '')}</span><span>via ${h(m.Connector)}${conn ? ` <span class="faint">(${h(conn.Type || 'OpenAICompatible')}${conn.Type === 'Cli' ? '/' + h(conn.Preset || 'Custom') : ''})</span>` : ' <span class="badge danger">unknown connector</span>'}</span>
      ${m.IsLocal ? '<span class="badge info">local</span>' : ''}${reliabilityBadges(m.Name)}${!free ? `<span class="badge">$${m.InputCostPerMillionTokens}/$${m.OutputCostPerMillionTokens} per M</span>${effectiveCostBadge(m)}` : m.IsLocal ? '<span class="badge ok">free</span>'
        : !conn || conn.Type === 'Cli' || conn.Type === 'Copilot' ? `<span class="badge" title="Free per request, but it uses up the plan's quota${m.ApiInputCostPerMillionTokens != null ? '; its use is valued at the API price on Usage' : ''}">subscription${m.ApiInputCostPerMillionTokens != null ? ` · API $${m.ApiInputCostPerMillionTokens}/$${m.ApiOutputCostPerMillionTokens ?? 0}` : ''}</span>`
        : '<span class="badge warn" title="Costs nothing as far as the router knows, so it ranks like a subscription: set its prices">no price set</span>'}${(m.Capabilities || []).map((c) => `<span class="badge">${h(c)}</span>`).join('')}</span>
    </div><label class="check item-switch"><input type="checkbox" data-bind="Models.${i}.Enabled" data-kind="bool" aria-label="${h(`${m.Name || 'This model'} enabled`)}" ${m.Enabled !== false ? 'checked' : ''}> enabled</label></div>
    <div class="item-body"><div class="form-grid">
      <label class="field"><span class="name">Name</span><input type="text" data-bind="Models.${i}.Name" value="${h(m.Name)}"><span class="hint">Clients can request it directly as <code>model</code>.</span></label>
      <label class="field"><span class="name">Connector</span><select data-bind="Models.${i}.Connector">${names.map((n) => `<option ${n === m.Connector ? 'selected' : ''}>${h(n)}</option>`).join('')}${names.includes(m.Connector) ? '' : `<option selected>${h(m.Connector)}</option>`}</select></label>
      <label class="field"><span class="name">Provider model id</span><div class="inline"><input type="text" data-bind="Models.${i}.Model" data-kind="nulltext" value="${h(m.Model || '')}" placeholder="connector default" list="models-${i}">${readOnly() ? '' : `<button class="btn small" data-action="discover" data-connector="${h(m.Connector)}" data-index="${i}" title="Ask the connector which models it has">Discover</button>`}</div><datalist id="models-${i}"></datalist></label>
      <label class="field"><span class="name">Tier</span><select data-bind="Models.${i}.Tier" data-kind="number">${tierOptions(m.Tier)}</select></label>
      ${pickList({ bind: `Models.${i}.Capabilities`, values: m.Capabilities, known: CAPABILITY_HINTS, label: 'Capabilities', otherLabel: 'Other capabilities', otherPlaceholder: 'other tags, comma-separated', hint: 'Declare <code>tools</code> only when the connector can carry them (HTTP, or a CLI with prompted tool calling).' })}
      ${numField({ bind: `Models.${i}.ContextWindow`, value: m.ContextWindow, def: 0, label: 'Context window (tokens)', min: 0, step: 1, placeholder: 'unknown', hint: 'Empty = unknown (never skipped for length).' })}
      ${numField({ bind: `Models.${i}.InputCostPerMillionTokens`, value: m.InputCostPerMillionTokens, def: 0, label: 'Input cost ($ / M tokens)', min: 0, step: 0.01, float: true, placeholder: '0 (free)' })}
      ${numField({ bind: `Models.${i}.OutputCostPerMillionTokens`, value: m.OutputCostPerMillionTokens, def: 0, label: 'Output cost ($ / M tokens)', min: 0, step: 0.01, float: true, placeholder: '0 (free)', hint: 'Empty for local models and flat-rate subscriptions.' })}
      <label class="field"><span class="name">Cache read cost ($ / M)</span><input type="number" min="0" step="0.01" data-bind="Models.${i}.CacheReadCostPerMillionTokens" data-kind="float" data-nullable="1" value="${m.CacheReadCostPerMillionTokens ?? ''}" placeholder="input price"><span class="hint">Prompt-cache reads, if the provider prices them differently.</span></label>
      <label class="field"><span class="name">Cache write cost ($ / M)</span><input type="number" min="0" step="0.01" data-bind="Models.${i}.CacheWriteCostPerMillionTokens" data-kind="float" data-nullable="1" value="${m.CacheWriteCostPerMillionTokens ?? ''}" placeholder="input price"></label>
      ${free && !m.IsLocal && (!conn || conn.Type === 'Cli' || conn.Type === 'Copilot') ? `<label class="field"><span class="name">API input price ($ / M)</span><input type="number" min="0" step="0.01" data-bind="Models.${i}.ApiInputCostPerMillionTokens" data-kind="float" data-nullable="1" value="${m.ApiInputCostPerMillionTokens ?? ''}" placeholder="not valued"><span class="hint">A subscription's use, valued at what the API charges. Shown on Usage, never counted as spend.</span></label>
      <label class="field"><span class="name">API output price ($ / M)</span><input type="number" min="0" step="0.01" data-bind="Models.${i}.ApiOutputCostPerMillionTokens" data-kind="float" data-nullable="1" value="${m.ApiOutputCostPerMillionTokens ?? ''}" placeholder="not valued"></label>
      <label class="field"><span class="name">API cache read price ($ / M)</span><input type="number" min="0" step="0.01" data-bind="Models.${i}.ApiCacheReadCostPerMillionTokens" data-kind="float" data-nullable="1" value="${m.ApiCacheReadCostPerMillionTokens ?? ''}" placeholder="API input price"><span class="hint">What the API charges for prompt-cache reads, usually far less than input.</span></label>
      <label class="field"><span class="name">API cache write price ($ / M)</span><input type="number" min="0" step="0.01" data-bind="Models.${i}.ApiCacheWriteCostPerMillionTokens" data-kind="float" data-nullable="1" value="${m.ApiCacheWriteCostPerMillionTokens ?? ''}" placeholder="API input price"></label>` : ''}
      ${numField({ bind: `Models.${i}.Priority`, value: m.Priority, def: 0, label: 'Priority', step: 1, hint: 'Lower is preferred; breaks ties.' })}
      <label class="field"><span class="name">Active hours</span><input type="text" data-bind="Models.${i}.ActiveHours" data-kind="nulltext" value="${h(m.ActiveHours || '')}" placeholder="always (e.g. 08:00-18:00)"><span class="hint">Outside them the model is not tried. Time zone: <a href="#/routing">Rules</a>.</span></label>
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
  // A reason every suggested row shares (e.g. "marked local because …") is said once, above the list.
  const reasons = (e) => String(e.reason || '').replace(/\.$/, '').split('; ').filter(Boolean);
  const suggested = rows.filter((e) => e.suggestion && !e.inCatalog);
  const common = suggested.length > 1 ? reasons(suggested[0]).filter((r) => suggested.every((e) => reasons(e).includes(r))) : [];
  const own = (e) => { const left = reasons(e).filter((r) => !common.includes(r)); return left.length ? left.join('; ') + '.' : ''; };
  const loadState = (e) => (e.kind === 'Embedding' ? '<span class="badge">embedding</span>'
    : !e.listed ? '<span class="badge warn" title="The server does not serve it until it is loaded">not served</span>'
    : e.loaded === true ? '<span class="badge ok">loaded</span>' : e.loaded === false ? '<span class="badge">not loaded</span>' : '');
  return `<div class="card lineup">${head}
    ${l.loadingNote ? `<p class="muted small">${h(l.loadingNote)}</p>` : ''}${l.note ? `<p class="muted small">${h(l.note)}</p>` : ''}${common.length ? `<p class="small lineup-common">${h(common.map((c) => c[0].toUpperCase() + c.slice(1)).join('. '))}.</p>` : ''}${l.familiesWarning ? `<p class="small" style="color:var(--warn)">${h(l.familiesWarning)}</p>` : ''}
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
          <td><input type="text" aria-label="Capabilities of ${h(e.id)}" data-state="${p}.suggestion.Capabilities" data-kind="csv" value="${h((s.Capabilities || []).join(', '))}"></td></tr>
          ${TIER_FROM[e.tierFrom] || own(e) ? `<tr class="lineup-reason"><td></td><td colspan="7" class="small muted">${TIER_FROM[e.tierFrom] ? `<span class="badge" title="${h(l.families || '')}">${TIER_FROM[e.tierFrom]}</span> ` : ''}${h(own(e))}</td></tr>` : ''}`;
      }).join('')}</table></div>` : ''}
    ${selectable.length ? `<div class="row-actions" style="margin-top:10px"><button class="btn primary small" data-action="lineup-add" data-index="${index}" ${selected ? '' : 'disabled'}>Add ${selected} selected to the catalog</button><span class="muted small">Added to the draft; Apply &amp; save to use them. Context, prices and the rest are on each model.</span></div>` : ''}
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
    <div class="section-head"><h1>Models</h1><span class="muted">The catalog the router chooses from, by connector. <b>Discover models</b> lists what a provider offers and suggests a tier for each.</span><span class="spacer"></span>${names.length ? '<button class="btn primary small" data-action="model-add">Add model</button>' : '<button class="btn primary small" disabled aria-describedby="models-need-connector">Add model</button><span class="small muted" id="models-need-connector">Add a <a href="#/connectors">connector</a> first.</span>'}</div>
    ${groups.map((connector) => {
      const conn = state.config.Connectors[connector];
      const own = models.map((m, i) => ({ m, i })).filter((x) => (x.m.Connector || '').toLowerCase() === (connector || '').toLowerCase()).sort(byTier);
      const tierCounts = [...new Set(own.map((x) => x.m.Tier))].sort((a, b) => a - b).map((t) => `<span class="tier" title="${own.filter((x) => x.m.Tier === t).map((x) => h(x.m.Name)).join(', ')}">T${t}${own.filter((x) => x.m.Tier === t).length > 1 ? ' ×' + own.filter((x) => x.m.Tier === t).length : ''}</span>`).join('');
      return `<section class="model-group" aria-label="Models via ${h(connector)}">
        <div class="section-head group-head"><h2>${h(connector || '(no connector)')}</h2>${conn ? `<span class="badge accent">${h(conn.Type || 'OpenAICompatible')}${conn.Type === 'Cli' ? ' / ' + h(conn.Preset || 'Custom') : ''}</span>` : '<span class="badge danger">unknown connector</span>'}<span class="muted small">${own.length} model${own.length === 1 ? '' : 's'}</span>${tierCounts}<span class="spacer"></span>${conn && !readOnly() ? `<button class="btn small" data-action="lineup-discover" data-connector="${h(connector)}">Discover models</button>` : ''}</div>
        ${lineupPanel(connector)}
        ${own.map((x) => modelItem(x.m, x.i)).join('')}
      </section>`;
    }).join('') || `<div class="card section empty-state"><h2>No models yet</h2><p class="muted">A model is what the router picks for a prompt; a connector is how it reaches the provider that runs it (LM Studio, OpenAI, a CLI on your sign-in). Add a connector first, then discover its models.</p>${readOnly() ? '' : '<a class="btn primary small" href="#/connectors">Add a connector</a>'}</div>`}
    ${reliabilityCard()}`;
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
    <div class="section-head"><h1>Connectors</h1><span class="muted">How the router reaches each provider.</span><span class="spacer"></span>${readOnly() || !Object.keys(conns).length ? '' : addConnectorMenu()}</div>
    ${Object.entries(conns).map(([name, c]) => {
      const key = 'conn:' + name;
      const type = c.Type || 'OpenAICompatible';
      const p = `Connectors.${name}`;
      const models = (state.config.Models || []).filter((m) => (m.Connector || '').toLowerCase() === name.toLowerCase());
      return `<div class="item ${state.open.has(key) ? 'open' : ''}" data-key="${key}">
        <div class="item-head" data-action="toggle" data-key="${key}" role="button" tabindex="0" aria-expanded="${state.open.has(key)}">
          <span class="chev">▶</span><span class="title">${h(name)}</span>
          <span class="meta"><span class="badge accent">${h(type)}${type === 'Cli' ? ' / ' + h(c.Preset || 'Custom') : ''}</span><span class="mono">${h(type === 'Cli' ? (c.Executable || '(preset default)') : type === 'Copilot' ? (c.Executable || 'copilot') : (c.BaseAddress || ''))}</span><span>${plural(models.length, 'model')}</span>${connectorStateBadge(name)}${c.ApiKey === '***' ? '<span class="badge ok">key set</span>' : (isReference(c.ApiKey) ? '<span class="badge ok" title="' + h(c.ApiKey) + '">key: reference</span>' : '')}${type === 'Cli' && c.ToolCalling === 'Prompted' ? '<span class="badge info">prompted tools</span>' : ''}</span>
        </div>
        <div class="item-body"><div class="form-grid">
          <label class="field"><span class="name">Name</span><input type="text" data-action-input="connector-rename" data-name="${h(name)}" value="${h(name)}"><span class="hint">Models refer to it by this name; renaming updates them.</span></label>
          <label class="field"><span class="name">Type</span><select data-bind="${p}.Type">${CONNECTOR_TYPES.map((t) => `<option ${t === type ? 'selected' : ''}>${t}</option>`).join('')}</select><span class="hint">OpenAICompatible: LM Studio, Ollama, vLLM, OpenAI, Gemini, OpenRouter… · Anthropic: Messages API with a key · Responses: OpenAI Responses API (/v1/responses) · Cli: a vendor CLI on your own login · Copilot: GitHub's Copilot SDK on your Copilot sign-in (native tools and streaming).</span></label>
          ${type === 'Cli' ? `
            <label class="field"><span class="name">Preset</span><select data-bind="${p}.Preset">${PRESETS.map((x) => `<option ${x === (c.Preset || 'Custom') ? 'selected' : ''}>${x}</option>`).join('')}</select></label>
            <label class="field"><span class="name">Executable</span><input type="text" data-bind="${p}.Executable" data-kind="nulltext" value="${h(c.Executable || '')}" placeholder="preset default (claude / copilot / codex)"><span class="hint">Started directly, never through a shell. On Windows use the .exe.</span></label>
            <label class="field"><span class="name">Tool calling</span><select data-bind="${p}.ToolCalling">${['None', 'Prompted'].map((x) => `<option ${x === (c.ToolCalling || 'None') ? 'selected' : ''}>${x}</option>`).join('')}</select><span class="hint">Prompted lets agentic clients (Claude Code) use this CLI: tools are described in the prompt and tool calls parsed from the reply.</span></label>
            ${c.ToolCalling === 'Prompted' ? `<label class="check"><input type="checkbox" data-bind="${p}.StreamPromptedReplies" data-kind="bool" ${c.StreamPromptedReplies !== false ? 'checked' : ''}> Stream text before tool calls <span class="muted">— off: buffer the reply, so a failed run can still fail over</span></label>
            <label class="check"><input type="checkbox" data-bind="${p}.RepairToolCalls" data-kind="bool" ${c.RepairToolCalls !== false ? 'checked' : ''}> Repair broken tool calls <span class="muted">— one short extra run</span></label>
            <label class="field"><span class="name">Tool call parsing</span><select data-bind="${p}.PromptedToolParsing">${['Lenient', 'Strict'].map((x) => `<option ${x === (c.PromptedToolParsing || 'Lenient') ? 'selected' : ''}>${x}</option>`).join('')}</select><span class="hint">Strict: only this run's own labelled block at the end of the reply is a call. Harder for text a tool returned to pass for a call.</span></label>` : ''}
            ${numField({ bind: `${p}.MaxConcurrency`, value: c.MaxConcurrency, def: 2, label: 'Max concurrency', min: 1, step: 1, hint: 'Runs at the same time.' })}
            ${numField({ bind: `${p}.MaxQueue`, value: c.MaxQueue, label: 'Max queue', min: 0, step: 1, nullable: true, placeholder: '16 (default)', hint: 'When this many requests are already waiting, the next one goes to another model.' })}
            ${numField({ bind: `${p}.MaxArgumentLength`, value: c.MaxArgumentLength, def: 24000, label: 'Max argument length', min: 100, step: 1, hint: 'With Prompt input Argument, longer prompts go on stdin.' })}
            ${durationField({ bind: `${p}.Timeout`, value: c.Timeout, def: '00:00:00', zeroMeansDefault: true, shownDefault: '00:05:00', label: 'Timeout', hint: 'Time to first output.' })}
            ${(c.Preset || 'Custom') !== 'Codex' ? `<label class="field"><span class="name">Prompt input</span><select data-bind="${p}.PromptInput">${['Stdin', 'Argument'].map((x) => `<option ${x === (c.PromptInput || 'Stdin') ? 'selected' : ''}>${x}</option>`).join('')}</select><span class="hint">Argument puts the prompt on the command line, where other users of this machine can read it.</span></label>` : ''}
            <label class="field wide"><span class="name">Arguments (one per line)</span><textarea data-bind="${p}.Arguments" data-kind="lines" rows="3">${h(lines(c.Arguments))}</textarea><span class="hint">Presets: extra flags appended (e.g. <code>--effort</code> / <code>low</code>). Custom: the full template with <code>{model}</code> and <code>{prompt}</code>.</span></label>
            <label class="field"><span class="name">Quota error patterns</span><textarea data-bind="${p}.QuotaErrorPatterns" data-kind="lines" rows="3">${h(lines(c.QuotaErrorPatterns))}</textarea><span class="hint">Output fragments that mean “out of quota” (added to the preset's).</span></label>
            <label class="field"><span class="name">Environment variables (KEY=value)</span><textarea data-bind="${p}.EnvironmentVariables" data-kind="kv" rows="3">${h(kv(c.EnvironmentVariables))}</textarea></label>
            <label class="field"><span class="name">Remove environment variables</span><textarea data-bind="${p}.RemoveEnvironmentVariables" data-kind="lines" rows="3">${h(lines(c.RemoveEnvironmentVariables))}</textarea><span class="hint">Empty = preset defaults (API keys that would switch billing away from your subscription).</span></label>
            <label class="check wide"><input type="checkbox" data-bind="${p}.AllowRemoteClients" data-kind="bool" ${c.AllowRemoteClients ? 'checked' : ''}> Allow remote clients <span class="muted">— read docs/TERMS.md first; a personal subscription must not serve other people</span></label>`
          : type === 'Copilot' ? `
            <label class="field"><span class="name">Copilot CLI</span><input type="text" data-bind="${p}.Executable" data-kind="nulltext" value="${h(c.Executable || '')}" placeholder="copilot (from PATH)"><span class="hint">The SDK runs it as its runtime: <code>npm install -g @github/copilot</code>, then sign in once with <code>copilot</code> (/login) or <code>gh auth login</code>.</span></label>
            <label class="field"><span class="name">Copilot home</span><input type="text" data-bind="${p}.HomeDirectory" data-kind="nulltext" value="${h(c.HomeDirectory || '')}" placeholder="~/.copilot"><span class="hint">COPILOT_HOME for the runtime; the default reuses the CLI's login.</span></label>
            ${numField({ bind: `${p}.MaxConcurrency`, value: c.MaxConcurrency, def: 2, label: 'Max concurrency', min: 1, step: 1 })}
            ${numField({ bind: `${p}.MaxQueue`, value: c.MaxQueue, label: 'Max queue', min: 0, step: 1, nullable: true, placeholder: '16 (default)' })}
            ${durationField({ bind: `${p}.Timeout`, value: c.Timeout, def: '00:00:00', zeroMeansDefault: true, shownDefault: '00:05:00', label: 'Timeout', hint: 'Time to first output.' })}
            <label class="check wide"><input type="checkbox" data-bind="${p}.AllowRemoteClients" data-kind="bool" ${c.AllowRemoteClients ? 'checked' : ''}> Allow remote clients <span class="muted">— read docs/TERMS.md first; a personal subscription must not serve other people</span></label>`
          : `
            <label class="field"><span class="name">Base address</span><input type="url" data-bind="${p}.BaseAddress" data-kind="nulltext" value="${h(c.BaseAddress || '')}" placeholder="${type === 'Anthropic' ? 'https://api.anthropic.com' : 'http://localhost:1234'}"></label>
            <label class="field"><span class="name">Request path</span><input type="text" data-bind="${p}.RequestPath" data-empty="drop" value="${h(c.RequestPath && c.RequestPath !== '/v1/chat/completions' ? c.RequestPath : '')}" placeholder="/v1/chat/completions (default)"><span class="hint">${type === 'Anthropic' ? 'Leave the default; /v1/messages is used.' : 'Gemini: /v1beta/openai/chat/completions · OpenRouter: /api/v1/chat/completions'}</span></label>
            ${secretField(p + '.ApiKey', c.ApiKey, 'API key')}
            ${durationField({ bind: `${p}.Timeout`, value: c.Timeout, def: '00:00:00', zeroMeansDefault: true, shownDefault: '00:01:40', label: 'Timeout', hint: 'Time to response headers.' })}
            ${type === 'Anthropic' ? `<label class="field"><span class="name">anthropic-version</span><input type="text" data-bind="${p}.AnthropicVersion" data-empty="drop" value="${h(c.AnthropicVersion && c.AnthropicVersion !== '2023-06-01' ? c.AnthropicVersion : '')}" placeholder="2023-06-01 (default)"></label>
            <label class="field"><span class="name">Prompt caching</span><select data-bind="${p}.PromptCaching">${['Auto', 'Off'].map((x) => `<option ${x === (c.PromptCaching || 'Auto') ? 'selected' : ''}>${x}</option>`).join('')}</select><span class="hint">Auto: cache markers on requests translated from OpenAI and Codex clients, once the prompt is long enough to cache. Off: for servers that reject them.</span></label>
            <label class="field"><span class="name">Cache lifetime</span><select data-bind="${p}.CacheTtl">${['5m', '1h'].map((x) => `<option ${x === (c.CacheTtl || '5m') ? 'selected' : ''}>${x}</option>`).join('')}</select><span class="hint">1h costs 2× the input price to write instead of 1.25×; worth it when turns are often more than 5 minutes apart.</span></label>` : ''}
            <label class="field"><span class="name">Extra headers (Name=value)</span><textarea data-bind="${p}.Headers" data-kind="kv" rows="3">${h(kv(c.Headers))}</textarea><span class="hint">Azure: <code>api-key=…</code> · OpenAI org: <code>OpenAI-Organization=org_…</code></span></label>
            <label class="field"><span class="name">Forward client headers</span><textarea data-bind="${p}.ForwardHeaders" data-kind="lines" rows="3">${h(lines(c.ForwardHeaders))}</textarea><span class="hint">Copied from the client request (e.g. <code>anthropic-beta</code>). Authorization headers are never forwarded.</span></label>`}
        </div>
        <div class="row-actions" style="margin-top:10px">${readOnly() ? '' : `<button class="btn small" data-action="lineup-discover" data-connector="${h(name)}">Discover models</button>`}<button class="btn danger small" data-action="connector-remove" data-name="${h(name)}">Remove</button></div>
        ${lineupPanel(name)}</div>
      </div>`;
    }).join('') || `<div class="card section empty-state"><h2>Add a connector</h2><p class="muted">A connector is how the router reaches a provider: a local server (LM Studio, Ollama), a cloud API with a key, or a CLI on your own sign-in. Its models are added next.</p>${readOnly() ? '' : presetList(true)}</div>`}`;
};

views.routing = () => {
  const c = state.config;
  const sel = c.Selection || {};
  const cx = c.Complexity || {};
  const rules = c.ComplexityTiers;
  const weight = (key, label, def, hint) => numField({ bind: `Complexity.${key}`, value: cx[key], def, label, min: 0, max: 1, step: 0.05, float: true, hint });
  return `
    <div class="section-head"><h1>Routing rules</h1><span class="muted">How a prompt becomes a required tier, and how a model is picked for it.</span></div>
    <div class="card section"><div class="card-head"><h2>Classification</h2></div><div class="form-grid">
      <label class="field"><span class="name">Default similarity threshold</span><input type="number" step="0.01" min="-1" max="1" data-bind="DefaultSimilarityThreshold" data-kind="float" value="${c.DefaultSimilarityThreshold}"><span class="hint">How close a prompt must be to a route's examples to match it. Hashing: 0.25–0.35 · nomic-embed-text: 0.60–0.70 · OpenAI text-embedding-3: 0.40–0.55. <a href="#/evaluate">Calibrate it</a>.</span></label>
      <label class="field"><span class="name">Fallback tier</span><select data-bind="FallbackTier" data-kind="number">${tierOptions(c.FallbackTier)}</select><span class="hint">When no route matches or classification fails.</span></label>
    </div>
    ${advanced('rules-classification', 'Advanced: near ties, scoring, shortcuts', `<div class="form-grid">
      ${numField({ bind: 'AmbiguityMargin', value: c.AmbiguityMargin, def: 0.02, label: 'Ambiguity margin', min: 0, max: 2, step: 0.005, float: true, hint: 'When two routes score within this of each other, the prompt gets the higher of their tiers. 0 turns it off.' })}
      <label class="field"><span class="name">Scoring strategy</span><select data-bind="ScoringStrategy">${[['MaxSimilarity', 'Closest example (MaxSimilarity)'], ['Centroid', 'Average of the examples (Centroid)']].map(([x, label]) => `<option value="${x}" ${x === (c.ScoringStrategy || 'MaxSimilarity') ? 'selected' : ''}>${label}</option>`).join('')}</select><span class="hint">How a route is scored: by its single closest utterance, or by the average of them all.</span></label>
      ${numField({ bind: 'ComplexityShortCircuitThreshold', value: c.ComplexityShortCircuitThreshold, def: 0.9, label: 'Complexity short-circuit', min: 0, max: 2, step: 0.01, float: true, hint: 'Above this complexity score the embedding call is skipped and complexity alone sets the tier. Above 1 turns it off.' })}
      ${numField({ bind: 'MaxReportedScores', value: c.MaxReportedScores, def: 3, label: 'Reported route scores', min: 0, step: 1, hint: 'How many route scores headers and activity show.' })}
    </div>`)}</div>

    <div class="card section"><div class="card-head"><h2>Complexity → tier</h2><span class="spacer"></span>${rules ? '<button class="btn small" data-action="rule-add">Add rule</button><button class="btn small" data-action="rules-defaults" title="Go back to the built-in rules">Use defaults</button>' : '<span class="muted small">Using the built-in rules: 0.45 → T2, 0.65 → T3, 0.85 → T4.</span> <button class="btn small" data-action="rules-customize">Customize</button>'}</div>
      <p class="small muted">A long, code-heavy or reasoning-heavy prompt is raised to at least this tier, whatever route it matched.</p>
      ${rules ? (rules.length ? `<div class="table-wrap"><table><tr><th>Min score</th><th>Tier</th><th><span class="visually-hidden">Actions</span></th></tr>${rules.map((r, i) => `<tr><td><input type="number" step="0.01" min="0" max="1" aria-label="Minimum complexity score" data-bind="ComplexityTiers.${i}.MinScore" data-kind="float" value="${r.MinScore}"></td><td><select aria-label="Tier" data-bind="ComplexityTiers.${i}.Tier" data-kind="number">${tierOptions(r.Tier)}</select></td><td><button class="btn danger small" data-action="rule-remove" data-index="${i}">Remove</button></td></tr>`).join('')}</table></div>` : '<div class="muted small">No rules: complexity never raises the tier.</div>') : ''}
    </div>

    <div class="card section"><div class="card-head"><h2>Model selection and failover</h2></div><div class="form-grid">
      <label class="field"><span class="name">Strategy</span><select data-bind="Selection.Strategy">${STRATEGIES.map((x) => `<option ${x === (sel.Strategy || 'CheapestCapable') ? 'selected' : ''}>${x}</option>`).join('')}</select><span class="hint">Fastest and Balanced use each model's recent time to first byte. Clients can override per request with auto:cheapest / auto:best / auto:priority / auto:fastest / auto:balanced.</span></label>
      ${numField({ bind: 'Selection.MaxAttempts', value: sel.MaxAttempts, def: 3, label: 'Max attempts', min: 1, step: 1, hint: 'Models tried for one request before it fails.' })}
      ${durationField({ bind: 'Selection.QuotaCooldown', value: sel.QuotaCooldown, def: '00:15:00', label: 'Quota cooldown', hint: 'How long a model out of quota is skipped.' })}
      ${durationField({ bind: 'Selection.FailureCooldown', value: sel.FailureCooldown, def: '00:00:30', label: 'Failure cooldown', hint: 'How long a failing model is skipped.' })}
      <label class="check wide"><input type="checkbox" data-bind="Selection.AllowBelowRequiredTier" data-kind="bool" ${sel.AllowBelowRequiredTier !== false ? 'checked' : ''}> Allow below required tier: answer with the best model available instead of failing</label>
    </div>
    ${advanced('rules-selection', 'Advanced: time zone, output estimate', `<div class="form-grid">
      <label class="field"><span class="name">Time zone</span><input type="text" data-bind="Selection.TimeZone" data-kind="nulltext" value="${h(sel.TimeZone || '')}" placeholder="this machine's" list="time-zones"><span class="hint">For model active hours (e.g. Europe/Amsterdam).</span></label>
      ${numField({ bind: 'Selection.EstimatedOutputTokens', value: sel.EstimatedOutputTokens, def: 512, label: 'Estimated output tokens', min: 0, step: 1, hint: 'For cost estimates and context-window checks.' })}
    </div>${timeZoneList('time-zones')}`)}</div>

    <div class="card section"><div class="card-head"><h2>Complexity heuristics</h2><span class="muted small">How the complexity score is worked out. The defaults suit most prompts.</span></div>
    ${advanced('rules-heuristics', 'Advanced: weights and keywords', `<div class="form-grid">
      ${numField({ bind: 'Complexity.TokenSaturation', value: cx.TokenSaturation, def: 2048, label: 'Token saturation', min: 1, step: 1, hint: 'A prompt this long (in tokens) counts as fully long.' })}
      ${weight('LengthWeight', 'Length weight', 0.6, 'How much length adds to the score.')}
      ${weight('CodeWeight', 'Code weight', 0.6, 'How much code adds.')}
      ${weight('StructureWeight', 'Structure weight', 0.5, 'How much structure (lists, steps, headings) adds.')}
      ${weight('ReasoningWeight', 'Reasoning weight', 0.6, 'How much reasoning words (why, prove, trade-off) add.')}
      ${numField({ bind: 'Complexity.ReasoningKeywordSaturation', value: cx.ReasoningKeywordSaturation, def: 3, label: 'Reasoning keyword saturation', min: 1, step: 1, hint: 'This many reasoning words count as fully reasoning-heavy.' })}
      <label class="field wide"><span class="name">Reasoning keywords (one per line; empty = defaults)</span><textarea data-bind="Complexity.ReasoningKeywords" data-kind="lines" data-nullable="1" rows="4">${h(lines(cx.ReasoningKeywords))}</textarea></label>
    </div>`)}</div>`;
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
      ${provider === 'Onnx' ? onnxFields() : provider === 'Hashing' ? numField({ bind: 'Embeddings.Dimensions', value: e.Dimensions, def: 512, label: 'Dimensions', min: 16, step: 1, hint: 'More dimensions, fewer accidental matches between unrelated words.' }) : `
        <label class="field"><span class="name">Endpoint</span><input type="url" data-bind="Embeddings.Endpoint" data-kind="nulltext" value="${h(e.Endpoint || '')}" placeholder="http://localhost:1234"></label>
        <label class="field"><span class="name">Model</span><input type="text" data-bind="Embeddings.Model" data-kind="nulltext" value="${h(e.Model || '')}" placeholder="text-embedding-nomic-embed-text-v1.5"></label>
        <label class="field"><span class="name">Request path</span><input type="text" data-bind="Embeddings.RequestPath" data-empty="drop" value="${h(e.RequestPath && e.RequestPath !== '/v1/embeddings' ? e.RequestPath : '')}" placeholder="/v1/embeddings (default)"></label>
        ${secretField('Embeddings.ApiKey', e.ApiKey, 'API key')}
        ${numField({ bind: 'Embeddings.MaxBatchSize', value: e.MaxBatchSize, def: 64, label: 'Max batch size', min: 1, step: 1, hint: 'Utterances embedded per call at warm-up.' })}
        ${durationField({ bind: 'Embeddings.Timeout', value: e.Timeout, def: '00:00:10', label: 'Timeout', hint: 'On the hot path of every request: keep it short.' })}`}
      <label class="check wide"><input type="checkbox" data-bind="Embeddings.WarmUpOnStartup" data-kind="bool" ${e.WarmUpOnStartup !== false ? 'checked' : ''}> Warm up on start-up (embed utterances in the background)</label>
    </div>
    <div class="inline" style="margin-top:12px"><button class="btn" data-action="embeddings-test" ${adminOnly()}>Test current provider</button><span id="embeddings-result" class="small muted"></span></div>
    <p class="muted small" style="margin-top:8px">The test uses the <b>running</b> configuration: apply your changes first.</p></div>`;
};

views.gateway = () => {
  const m = state.config.Middleware || {};
  return `
    <div class="section-head"><h1>Gateway</h1><span class="muted">The front door: which requests are routed, and how client model names map.</span></div>
    <div class="card section"><div class="card-head"><h2>Mode and paths</h2></div><div class="form-grid">
      <label class="field"><span class="name">Mode</span><select data-bind="Middleware.Mode">${['Dispatch', 'Annotate'].map((x) => `<option ${x === (m.Mode || 'Annotate') ? 'selected' : ''}>${x}</option>`).join('')}</select><span class="hint">Dispatch serves requests through the connectors. Annotate only attaches the plan for your own endpoint.</span></label>
      <label class="field"><span class="name">OpenAI paths (one per line)</span><textarea data-bind="Middleware.PathPrefixes" data-kind="lines" rows="3">${h(lines(m.PathPrefixes))}</textarea><span class="hint">Chat-completions bodies. Required in Dispatch mode.</span></label>
      <label class="field"><span class="name">Anthropic paths (one per line)</span><textarea data-bind="Middleware.AnthropicPathPrefixes" data-kind="lines" rows="3" placeholder="/v1/messages">${h(lines(m.AnthropicPathPrefixes))}</textarea><span class="hint">Messages API bodies (Claude Code, Anthropic SDKs). Empty = <code>/v1/messages</code>.</span></label>
      <label class="field"><span class="name">Responses paths (one per line)</span><textarea data-bind="Middleware.ResponsesPathPrefixes" data-kind="lines" rows="3" placeholder="/v1/responses">${h(lines(m.ResponsesPathPrefixes))}</textarea><span class="hint">OpenAI Responses API bodies (Codex CLI, the Agents SDK). Empty = <code>/v1/responses</code>.</span></label>
      <div class="checks wide">
        <label class="check"><input type="checkbox" data-bind="Middleware.PinToolCallingTurns" data-kind="bool" ${m.PinToolCallingTurns !== false ? 'checked' : ''}> Pin tool-calling turns to the model that started them</label>
        <label class="check"><input type="checkbox" data-bind="Middleware.StripSystemReminders" data-kind="bool" ${m.StripSystemReminders !== false ? 'checked' : ''}> Ignore injected context blocks (&lt;system-reminder&gt; and the like) when classifying</label>
      </div>
    </div>
    ${advanced('gateway-advanced', 'Advanced: classification input, body limit, headers', `<div class="form-grid">
      <label class="field"><span class="name">Message selection</span><select data-bind="Middleware.MessageSelection">${MESSAGE_SELECTION.map((x) => `<option ${x === (m.MessageSelection || 'LastUserMessage') ? 'selected' : ''}>${x}</option>`).join('')}</select><span class="hint">Which messages are classified.</span></label>
      ${numField({ bind: 'Middleware.MaxRequestBodyBytes', value: m.MaxRequestBodyBytes, def: 8388608, label: 'Max request body (bytes)', min: 1024, step: 1024, hint: 'Agentic clients send large system prompts and tool lists; 8 MiB (the default) is safe.' })}
      <label class="field"><span class="name">Header prefix</span><input type="text" data-bind="Middleware.HeaderPrefix" data-empty="drop" value="${h(m.HeaderPrefix && m.HeaderPrefix !== 'X-Prompt-Route' ? m.HeaderPrefix : '')}" placeholder="X-Prompt-Route (default)"><span class="hint">The routing headers on every response start with it.</span></label>
      <label class="field wide"><span class="name">More injected tags (one per line)</span><textarea data-bind="Middleware.InjectedTags" data-kind="lines" rows="2" placeholder="environment_details">${h(lines(m.InjectedTags))}</textarea><span class="hint">Blocks a client adds to your messages, ignored when classifying. Built in: <code>system-reminder</code>, <code>system_reminder</code>, <code>current_datetime</code>, <code>copilot_tauri_workspace</code>.</span></label>
      <div class="checks wide">
        <label class="check"><input type="checkbox" data-bind="Middleware.EmitResponseHeaders" data-kind="bool" ${m.EmitResponseHeaders !== false ? 'checked' : ''}> Emit routing headers</label>
        <label class="check"><input type="checkbox" data-bind="Middleware.FailOpen" data-kind="bool" ${m.FailOpen !== false ? 'checked' : ''}> Fail open: use the fallback tier when classification throws</label>
      </div>
    </div>`)}</div>
    <div class="card section"><div class="card-head"><h2>Gateway keys</h2><span class="muted small">Clients on other machines must send one (<code>Authorization: Bearer</code> or <code>x-api-key</code>); without keys they are refused. The client name is shown in Activity.</span></div><div class="form-grid">
      <label class="field wide"><span class="name">Keys (client=key, one per line)</span><textarea data-bind="Middleware.GatewayKeys" data-kind="kv" rows="3" placeholder="laptop=\${env:ROUTER_KEY_LAPTOP}">${h(kv(m.GatewayKeys))}</textarea><span class="hint">Stored keys show as <code>***</code> and are kept when left as is. Use <code>\${env:…}</code> references to keep keys out of the file.</span></label>
      <div class="checks wide">
        <label class="check"><input type="checkbox" data-bind="Middleware.RequireGatewayKeyForLocalClients" data-kind="bool" ${m.RequireGatewayKeyForLocalClients ? 'checked' : ''}> Require a key from this machine too (for a local reverse proxy)</label>
        <label class="check"><input type="checkbox" data-bind="Middleware.AllowUnauthenticatedRemoteClients" data-kind="bool" ${m.AllowUnauthenticatedRemoteClients ? 'checked' : ''}> Serve other machines without a key while none is set (only behind a proxy that authenticates callers)</label>
      </div>
    </div></div>
    ${rateLimitsCard(m)}
    ${storedPromptsCard(state.config.State || {})}
    ${aliasesCard()}`;
};

/* A row's name input with its problem (a duplicate or an empty name) shown under it and tied to it. */
const rowName = (attrs, value, problem, id) => `<input type="text" ${attrs} value="${h(value)}"${problem ? ` aria-invalid="true" aria-describedby="${id}"` : ''}>${problem ? `<span class="field-error" id="${id}">${h(problem)}</span>` : ''}`;

/*
 * Model aliases, edited as rows (draft.js): two rows with the same name are flagged rather than merged into one, and
 * the order can be changed, since among the globs the first match wins.
 */
const aliasesCard = () => {
  const rows = aliasRows();
  const problems = rowProblems(rows, 'alias');
  const globs = rows.filter((r) => r.key.includes('*')).length;
  return `<div class="card section"><div class="card-head"><h2>Model aliases</h2><span class="muted small">Map the names clients send to routing aliases or catalog models. A name without <code>*</code> is matched exactly, before the globs; among the globs the first match wins, so order them from specific to general. Claude Code sends its default Opus name whether or not you chose Opus, so route Claude names as <code>auto</code>.</span><span class="spacer"></span><button class="btn small" data-action="alias-add">Add alias</button><button class="btn small" data-action="alias-claude">Add Claude Code defaults</button></div>
      <div class="table-wrap"><table><tr><th>Client model (glob)</th><th>Routes as</th><th>${globs > 1 ? 'Order' : '<span class="visually-hidden">Order</span>'}</th><th><span class="visually-hidden">Actions</span></th></tr>
        ${rows.map((r, i) => `<tr><td>${rowName(`aria-label="Client model" data-action-input="alias-key" data-index="${i}" data-row="alias-${i}" data-path="Middleware.ModelAliases.${h(r.key.trim())}"`, r.key, problems.get(i), `alias-problem-${i}`)}</td>
          <td><input type="text" aria-label="${h(`Routes ${r.key || 'this name'} as`)}" data-action-input="alias-value" data-index="${i}" value="${h(r.value)}" list="alias-targets"></td>
          <td class="nowrap">${globs > 1 && r.key.includes('*') ? `<button class="btn ghost small" data-action="alias-up" data-index="${i}" aria-label="${h(`Move ${r.key} up`)}" title="Move up: matched earlier" ${i === 0 ? 'disabled' : ''}>↑</button><button class="btn ghost small" data-action="alias-down" data-index="${i}" aria-label="${h(`Move ${r.key} down`)}" title="Move down: matched later" ${i === rows.length - 1 ? 'disabled' : ''}>↓</button>` : ''}</td>
          <td><button class="btn ghost small" data-action="alias-remove" data-index="${i}" aria-label="${h(`Remove alias ${r.key}`)}">remove</button></td></tr>`).join('') || '<tr><td colspan="4" class="muted small">No aliases. Unknown names route as <code>auto</code>.</td></tr>'}
      </table></div>
      <datalist id="alias-targets">${['auto', 'auto:cheapest', 'auto:best', 'auto:fastest', 'auto:balanced', 'auto:priority'].concat((state.config.Models || []).map((x) => x.Name)).map((x) => `<option value="${h(x)}">`).join('')}</datalist>
    </div>`;
};

/* What the activity log keeps of each prompt and for how long (draft settings), and deleting it (acts at once). */
const storedPromptsCard = (st) => {
  const r = st.Retention || {};
  const days = (key, label, def, unit) => numField({ bind: `State.Retention.${key}`, value: r[key], label, min: 1, step: 1, nullable: true, placeholder: `${def} (default)`, hint: `Default: ${def} ${unit}.` });
  return `<div class="card section"><div class="card-head"><h2>Stored prompts</h2><span class="muted small">What the activity log keeps of each prompt, and how long the state database keeps records. Retention applies at the next prune.</span></div><div class="form-grid">
      <label class="field"><span class="name">Prompt previews</span><select data-bind="State.StorePrompts">${['Redacted', 'On', 'Off'].map((x) => `<option ${x === (st.StorePrompts || 'Redacted') ? 'selected' : ''}>${x}</option>`).join('')}</select><span class="hint">Redacted: the first 160 characters with API keys and tokens masked. Off: no prompt text in activity, exports or the live view.</span></label>
      ${days('ActivityDays', 'Activity (days)', 30, 'days')}
      ${days('ActivityEntries', 'Activity (entries)', '10,000', 'entries')}
      ${days('UsageDays', 'Usage (days)', 400, 'days')}
      ${days('ConfigurationVersions', 'Configuration versions', 200, 'versions')}
    </div></div>
    ${readOnly() ? '' : `<div class="card section danger-zone"><div class="card-head"><h2>Stored activity</h2><span class="muted small">Deletes every activity entry and its prompt preview now, not through the save bar. Usage, budgets and the configuration history are kept.</span><span class="spacer"></span><button class="btn danger small" data-action="activity-purge">Delete all activity…</button></div></div>`}`;
};

/* Rate limits per gateway client ("*" for every other client), as rows (draft.js), with what each client used in the last minute. */
const rateLimitsCard = (m) => {
  const limits = limitRows();
  const problems = rowProblems(limits, 'limit');
  const seen = (state.status && state.status.gateway && state.status.gateway.rateLimits) || [];
  const num = (v) => (v == null ? '' : v);
  const used = (used, max) => (max ? `${Number(used).toLocaleString()} / ${Number(max).toLocaleString()}` : Number(used).toLocaleString());
  return `<div class="card section"><div class="card-head"><h2>Rate limits</h2><span class="muted small">Per gateway client, over a sliding minute. A client over its limit gets a 429 with <code>Retry-After</code>. <code>*</code> covers every client without its own limit, and clients without a key by address.</span><span class="spacer"></span><button class="btn small" data-action="ratelimit-add">Add limit</button></div>
      <div class="table-wrap"><table><tr><th>Client</th><th>Requests per minute</th><th>Tokens per minute</th><th><span class="visually-hidden">Actions</span></th></tr>
        ${limits.map((r, i) => { const k = r.key.trim(); const v = r.value || {}; return `<tr><td>${rowName(`aria-label="Client" data-action-input="ratelimit-key" data-index="${i}" data-row="limit-${i}" data-path="Middleware.RateLimits.${h(k)}" list="ratelimit-clients"`, r.key, problems.get(i), `limit-problem-${i}`)}</td><td><input type="number" min="1" aria-label="Requests per minute for ${h(k)}" data-action-input="ratelimit-rpm" data-index="${i}" data-path="Middleware.RateLimits.${h(k)}.RequestsPerMinute" value="${num(v.RequestsPerMinute)}" placeholder="no limit"></td><td><input type="number" min="1" aria-label="Tokens per minute for ${h(k)}" data-action-input="ratelimit-tpm" data-index="${i}" data-path="Middleware.RateLimits.${h(k)}.TokensPerMinute" value="${num(v.TokensPerMinute)}" placeholder="no limit"></td><td><button class="btn ghost small" data-action="ratelimit-remove" data-index="${i}" aria-label="${h(`Remove the limit for ${k || 'this row'}`)}">remove</button></td></tr>`; }).join('') || '<tr><td colspan="4" class="muted small">No rate limits.</td></tr>'}
      </table></div>
      <div class="form-grid"><label class="field"><span class="name">Default for other machines (requests per minute)</span><input type="number" min="0" data-bind="Middleware.RemoteRequestsPerMinute" data-kind="number" value="${m.RemoteRequestsPerMinute ?? 120}"><span class="hint">Each client on another machine that no entry above covers. 0 turns it off.</span></label></div>
      <datalist id="ratelimit-clients">${['*'].concat(Object.keys(m.GatewayKeys || {})).map((x) => `<option value="${h(x)}">`).join('')}</datalist>
      ${seen.length ? `<table><tr><th>Client</th><th>Limit</th><th>Requests, last minute</th><th>Tokens, last minute</th><th>Turned away</th></tr>
        ${seen.map((r) => `<tr><td>${h(r.client)}</td><td><code>${h(r.limit)}</code></td><td>${used(r.requests, r.requestsPerMinute)}</td><td>${used(r.tokens, r.tokensPerMinute)}</td><td>${r.limited ? `<span class="badge warn">${Number(r.limited).toLocaleString()}</span> <span class="faint small">last ${h(new Date(r.lastLimited).toLocaleTimeString())}</span>` : '<span class="muted">none</span>'}</td></tr>`).join('')}
      </table>` : ''}
    </div>`;
};

views.clients = () => {
  const s = state.status;
  // The address the Overview gives this machine (localhost rather than 127.0.0.1), unless the page came from elsewhere.
  const local = s.service && ['localhost', '127.0.0.1', '[::1]'].includes(location.hostname) ? (addresses(s.service).find((a) => a.local) || {}).url : null;
  const origin = local || location.origin;
  const anth = (s.gateway.anthropicPathPrefixes[0] || '/v1/messages');
  // Claude Code compacts before the conversation outgrows the smallest model that can carry its tools, when every such model declares its context window.
  const agentic = (state.config.Models || []).filter((m) => m.Enabled !== false && (m.Capabilities || []).some((c) => String(c).toLowerCase() === 'tools'));
  const context = agentic.length && agentic.every((m) => m.ContextWindow > 0) ? Math.min(...agentic.map((m) => m.ContextWindow)) : null;
  const snippet = (title, code, note) => `<div class="card section"><div class="card-head"><h2>${title}</h2></div>${note ? `<p class="muted small">${note}</p>` : ''}<div class="snippet"><pre><code>${h(code)}</code><button class="btn small copy" data-action="copy">Copy</button></pre></div></div>`;
  return `
    <div class="section-head"><h1>Connect clients</h1><span class="muted">Point your tools at <code>${h(origin)}</code>; keep their workflow, let the router pick the model.</span></div>
    ${s.gateway.mode !== 'Dispatch' ? '<div class="banner danger" style="margin-bottom:14px;border-radius:8px">The gateway is in <b>Annotate</b> mode: it only attaches routing information for your own endpoint. Switch to <b>Dispatch</b> under Gateway for the router to answer clients itself.</div>' : ''}
    ${snippet('Claude Code', `# Anthropic Messages API on ${origin}${anth}
export ANTHROPIC_BASE_URL=${origin}
export ANTHROPIC_AUTH_TOKEN=semantic-router   # any value: the router ignores it and never forwards it
${context ? `export CLAUDE_CODE_MAX_CONTEXT_TOKENS=${context}   # the smallest context window among your tool-capable models
` : ''}export CLAUDE_CODE_AUTO_MODE_SERVER=0
claude --model semantic-router

# One-off:
ANTHROPIC_BASE_URL=${origin} ANTHROPIC_AUTH_TOKEN=semantic-router claude --model semantic-router -p "explain this repo"`,
      `<code>semantic-router</code> is the router's own model name: Claude Code shows that the router picks the model. Other names Claude Code sends (its default Opus name, <code>haiku</code> for side tasks) go through the <a href="#/gateway">model aliases</a>; <code>@opus</code> at the start of a message asks for Opus. Tool-bearing turns go to models whose connector can carry tools: HTTP models declaring <code>tools</code>, or a CLI connector with <b>prompted</b> tool calling. ${(s.connectors.some((c) => c.type === 'Cli' && c.toolCalling === 'Prompted')) ? '' : 'None of your CLI connectors has prompted tool calling on, so agentic turns will only use HTTP models.'}`)}
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
    <div class="table-wrap"><table><tr><th>Version</th><th>When</th><th>What</th><th>Who</th><th><span class="visually-hidden">Actions</span></th></tr>
      ${hs.versions.map((v) => `<tr><td class="mono">${v.id}</td><td class="nowrap">${h(fullStamp(v.timestamp))}</td><td>${h(v.source)}${v.note ? ' <span class="faint small">' + h(v.note) + '</span>' : ''}</td><td class="small">${h(v.actor || '')}</td>
        <td class="row-actions"><button class="btn small" data-action="history-diff" data-id="${v.id}">Compare with draft</button>${readOnly() ? '' : `<button class="btn ghost small" data-action="history-rollback" data-id="${v.id}">Roll back</button>`}</td></tr>`).join('') || '<tr><td colspan="5" class="muted small">Nothing applied yet.</td></tr>'}
    </table></div>
    ${hs.diff ? `<h3 style="margin-top:14px">Version ${hs.diff.id} → draft</h3><pre class="diff">${hs.diff.html}</pre>` : ''}
  </div>`;
};
