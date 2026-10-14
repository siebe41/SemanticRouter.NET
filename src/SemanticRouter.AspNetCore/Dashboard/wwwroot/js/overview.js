/* Semantic Router dashboard: Overview and Activity pages, the prompt tester result and the label form. */
import { fmtMs, trendsCard } from './charts.js';
import { tierOptions } from './config.js';
import { h, num, readOnly, state, tierName, views, when } from './core.js';
import { digestCard, noticesCard } from './digest.js';
import { endpointsCard } from './endpoints.js';
import { learnedBadge, learnedCard } from './learned.js';
import { unreliableBadge } from './reliability.js';
import { serviceCard } from './service.js';
import { isLocalModel } from './live.js';
import { int, usd } from './usage.js';

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
    ${noticesCard()}
    ${serviceCard()}
    ${digestCard()}
    <div class="grid cols-4 section">
      <div class="card stat"><span class="value">${a.total}</span><span class="label">routed (last ${a.total >= 500 ? '500' : a.total})</span></div>
      <div class="card stat"><span class="value">${a.served}</span><span class="label">served · ${a.failed} failed · ${a.degraded} degraded</span></div>
      <div class="card stat"><span class="value">${s.models.filter((m) => m.enabled).length}<span class="muted" style="font-size:14px"> / ${s.models.length}</span></span><span class="label">models enabled</span></div>
      <div class="card stat"><span class="value">${s.routes.length}</span><span class="label">routes · ${s.routes.reduce((n, r) => n + r.utterances, 0)} utterances</span></div>
    </div>
    ${endpointsCard()}
    ${trendsCard()}
    ${learnedCard()}

    <div class="grid cols-2 section">
      <div class="card">
        <div class="card-head"><h2>Models</h2><span class="spacer"></span>${s.models.some((m) => m.coolingDownUntil) ? '<button class="btn small" data-action="reset-health">Clear cooldowns</button>' : ''}</div>
        <table>
          <tr><th>Model</th><th>Tier</th><th>Connector</th><th>State</th></tr>
          ${s.models.map((m) => `<tr>
            <td><span class="${m.enabled ? '' : 'muted'}">${h(m.name)}</span>${m.isLocal ? ' <span class="badge info">local</span>' : ''}<div class="small faint mono">${h(m.model || '')}</div></td>
            <td><span class="tier" title="${h(tierName(m.tier))}">T${m.tier}</span></td>
            <td class="small">${h(m.connector)} <span class="faint">${h(m.connectorType || '')}</span></td>
            <td>${!m.enabled ? '<span class="badge">disabled</span>' : (m.coolingDownUntil ? `<span class="badge warn">cooling down</span> <span class="small muted">until ${h(new Date(m.coolingDownUntil).toLocaleString(undefined, { weekday: 'short', hour: '2-digit', minute: '2-digit' }))}</span>` : '<span class="badge ok">ready</span>')}</td>
          </tr>`).join('')}
        </table>
      </div>
      <div class="card">
        <div class="card-head"><h2>Runtime</h2></div>
        <table>
          <tr><th>Configuration</th><td>v${s.version} from <b>${h(s.source)}</b> · ${h(when(s.createdAt))}</td></tr>
          <tr><th>Store</th><td class="mono small">${s.store ? h(s.store) : '<span class="muted">none (changes apply in memory only)</span>'}</td></tr>
          <tr><th>Embeddings</th><td>${h(s.embeddings.provider)} ${s.embeddings.model ? '· <span class="mono">' + h(s.embeddings.model) + '</span>' : ''} ${s.embeddings.endpoint ? '<span class="faint small">' + h(s.embeddings.endpoint) + '</span>' : ''}
            ${s.warmedUp ? '<span class="badge ok">index ready</span>' : (s.warmUpError ? `<span class="badge danger" title="${h(s.warmUpError)}">warm-up failed</span>` : '<span class="badge info">warming up</span>')}
            ${s.embeddings.index ? `<div class="faint small">${s.embeddings.index.utterances} utterances embedded in ${fmtMs(s.embeddings.index.buildMilliseconds)} · ${s.embeddings.index.dimensions} dimensions</div>` : ''}</td></tr>
          ${s.embeddings.cache ? `<tr><th>Classification cache</th><td>${s.embeddings.cache.hits + s.embeddings.cache.misses ? `<b>${Math.round(s.embeddings.cache.hitRate * 100)}%</b> of prompts reused a recent decision (${int(s.embeddings.cache.hits)} of ${int(s.embeddings.cache.hits + s.embeddings.cache.misses)})` : '<span class="muted">no prompts yet</span>'} <span class="faint small">· ${s.embeddings.cache.entries} of ${s.embeddings.cache.capacity} kept</span></td></tr>` : ''}
          <tr><th>Gateway</th><td>${h(s.gateway.mode)} · OpenAI on <span class="mono">${h(s.gateway.pathPrefixes.join(', ') || '(all JSON POSTs)')}</span> · Anthropic on <span class="mono">${h(s.gateway.anthropicPathPrefixes.join(', '))}</span> · Responses on <span class="mono">${h(s.gateway.responsesPathPrefixes.join(', '))}</span></td></tr>
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
      <textarea id="tester-prompt" aria-label="Prompt to try" placeholder="Paste a prompt…">${h(t.prompt)}</textarea>
      <div class="inline" style="margin-top:8px">
        <select id="tester-model" aria-label="Model" style="width:220px">
          ${['auto', 'auto:cheapest', 'auto:best', 'auto:fastest', 'auto:balanced', 'auto:priority'].concat(s.models.map((m) => m.name)).map((m) => `<option ${m === t.model ? 'selected' : ''}>${h(m)}</option>`).join('')}
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

export function renderClassification(r) {
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

export function renderActivityTable(entries, labels) {
  if (!entries.length) return '<div class="muted small">No requests routed yet. Point a client at the gateway or use “Try a prompt”.</div>';
  return `<div class="table-wrap"><table>
    <tr><th>Time</th><th>Prompt</th><th>Tier</th><th>Reason</th><th>Route</th><th>Served by</th><th class="num">ms</th>${labels ? '<th></th>' : ''}</tr>
    ${entries.map((e) => `<tr title="${h(e.detail || '')}">
      <td class="nowrap small">${h(when(e.timestamp))}</td>
      <td><span class="small">${h(e.promptPreview)}</span><div class="faint small">${e.client ? '<span class="badge info">' + h(e.client) + '</span> ' : ''}${h(e.protocol)} ${h(e.requestedModel || '')}${e.hasTools ? ' · tools' : ''}${e.toolContinuation ? ' · tool result' : ''}${e.localOnly ? ' · local only' : ''} · ~${e.estimatedInputTokens} tok${e.inputTokens != null || e.outputTokens != null ? ` · ${int(e.inputTokens)} in / ${int(e.outputTokens)} out` : ''}${e.cacheReadTokens ? ` · <span title="Prompt tokens served from the backend's prompt cache">${int(e.cacheReadTokens)} cached</span>` : ''}${e.cacheCreationTokens ? ` · <span title="Prompt tokens written to the backend's prompt cache">${int(e.cacheCreationTokens)} to cache</span>` : ''}${e.costUsd ? ' · ' + usd(e.costUsd) : ''}${e.budget ? ' · <span class="badge warn">budget ' + h(e.budget) + '</span>' : ''}${e.dropped ? ' · <span class="badge warn" title="Not carried to the serving backend">dropped ' + h(e.dropped) + '</span>' : ''}${e.profile ? ' · <span class="badge accent" title="Routing profile">' + h(e.profile) + '</span>' : ''}${e.project ? ' · <span class="mono" title="Project">' + h(e.project) + '</span>' : ''}${e.signals ? ' · <span class="badge warn" title="Kept local because the conversation contains this">local: ' + h(e.signals) + '</span>' : ''}${e.notes ? ' · <span class="badge ' + (/malformed/.test(e.notes) ? 'warn' : 'info') + '" title="Noted by the connector">' + h(e.notes) + '</span>' : ''}${memoryBadge(e)}</div></td>
      <td><span class="tier">T${e.tier}</span></td>
      <td class="small">${h(e.reason)}${e.cached ? ' <span class="badge" title="Reused a recent decision: no embedding call">cached</span>' : ''}${learnedBadge(e)}</td>
      <td class="small">${h(e.route || '—')}${e.similarity != null ? ` <span class="faint">${num(e.similarity, 2)}</span>` : ''}</td>
      <td class="small">${e.outcome === 'failed' ? '<span class="badge danger">failed</span>' : e.outcome === 'rate-limited' ? `<span class="badge warn" title="${h(e.detail || '')}">rate limited</span>` : e.servedBy ? `<b>${h(e.servedBy)}</b>${e.attempt > 1 ? ` <span class="badge warn">try ${e.attempt}</span>` : ''}${e.degraded ? ' <span class="badge warn">degraded</span>' : ''}` : `<span class="muted">${h(e.plannedModel || '—')}</span> <span class="faint">planned</span>`}${e.sticky ? ' <span class="badge" title="Stayed on the model that answered the conversation\'s last turn">sticky</span>' : ''}${e.selection ? ` <span class="badge" title="${h(e.selection)}">${h(e.selection.split(':')[0])}</span>` : ''}${e.callout ? ` <span class="badge accent" title="Chosen with an inline callout">${h(e.callout)}</span>` : ''}${unreliableBadge(e)}${feedbackBadges(e)}${gradeBadge(e)}</td>
      <td class="num small">${e.totalMilliseconds != null ? num(e.totalMilliseconds, 0) : num(e.classificationMilliseconds, 1)}${e.timeToFirstByteMilliseconds != null && e.totalMilliseconds != null ? `<div class="faint" title="Time to the first byte of the reply">first ${num(e.timeToFirstByteMilliseconds, 0)}</div>` : ''}</td>
      ${labels ? `<td>${e.toolContinuation ? '' : `<button class="btn ghost small" data-action="label-open" data-id="${e.id}" title="Say which tier this prompt should have had">Label</button>`}</td>` : ''}
    </tr>`).join('')}
  </table></div>`;
}

const FEEDBACK = {
  Retried: ['retried', 'The same prompt was sent again soon after this answer'],
  Corrected: ['corrected', 'The next message corrected this answer'],
  Overridden: ['overridden', 'The next message chose a model with a callout'],
  ToolErrors: ['tool errors', 'Tool calls kept failing, or the model wrote malformed tool calls'],
  EmptyReply: ['empty reply', 'The model answered with nothing'],
  Judged: ['graded low', 'The judge graded this answer low'],
};

/* Signs, noticed afterwards, that this decision was not good enough. */
export function feedbackBadges(e) {
  return ((state.feedback || {})[e.id] || []).filter((k) => !(k === 'Judged' && e.grade != null)).map((k) => { const [label, title] = FEEDBACK[k] || [k, k]; return ` <span class="badge warn" title="${h(title)}">${h(label)}</span>`; }).join('');
}

/* The judge's grade of a sampled answer (Middleware:Judge): 1 wrong … 5 excellent. */
export function gradeBadge(e) {
  if (e.grade == null) return '';
  const judge = (state.config && state.config.Middleware && state.config.Middleware.Judge) || {};
  const failing = e.grade <= (judge.FailingGrade || 2);
  return ` <span class="badge ${failing ? 'warn' : e.grade >= 4 ? 'ok' : ''}" title="${h('Graded by the judge' + (e.gradeNote ? ': ' + e.gradeNote : '') + (failing ? '. Learned from as a sign this needed a tier more.' : ''))}">graded ${e.grade}/5</span>`;
}

export function memoryBadge(e) {
  const notes = e.memory || [];
  if (!notes.length) return '';
  const cloud = (e.sentTo || []).some((n) => !isLocalModel(n));
  const text = notes.filter((r) => r.kind === 'Content').length;
  const title = notes.map((r) => `${r.title} (${r.kind === 'Content' ? 'text' : 'mentioned'})`).join(', ');
  return ` <a href="#/memory" class="badge ${cloud && text ? 'signal' : 'info'}" title="${h(title)}">${notes.length} note${notes.length === 1 ? '' : 's'}${cloud ? ' → cloud' : ''}</a>`;
}

function judgeNote() {
  const j = state.config && state.config.Middleware && state.config.Middleware.Judge;
  if (!j || !j.Enabled || !j.Model) return '';
  return ` The judge (<span class="mono">${h(j.Model)}</span>) grades ${+(j.SampleRate * 100).toFixed(1)}% of the answers from tier ${j.MaxTier} and below, at most ${j.DailyLimit} a day.`;
}

views.activity = () => {
  const f = state.activityFilter;
  const entries = state.activity.filter((e) => f === 'all' || (f === 'failed' ? e.outcome === 'failed' : f === 'degraded' ? e.degraded : f === 'tools' ? e.hasTools : f.startsWith('profile:') ? e.profile === f.slice(8) : true));
  const profiles = [...new Set(state.activity.map((e) => e.profile).filter(Boolean).concat(Object.keys((state.config && state.config.Profiles) || {})))];
  return `
    <div class="section-head"><h1>Activity</h1><span class="muted">The last ${state.activity.length} routed requests (newest first).${judgeNote()}</span><span class="spacer"></span>
      <select id="activity-filter" aria-label="Show" style="width:150px">${['all', 'failed', 'degraded', 'tools'].map((x) => `<option value="${x}" ${x === f ? 'selected' : ''}>${x}</option>`).join('')}${profiles.map((x) => `<option value="profile:${h(x)}" ${'profile:' + x === f ? 'selected' : ''}>profile ${h(x)}</option>`).join('')}</select>
      <label class="check"><input type="checkbox" id="auto-refresh" ${state.autoRefresh ? 'checked' : ''}> auto-refresh</label>
      <button class="btn small" data-action="refresh">Refresh</button></div>
    ${state.label ? labelForm() : ''}
    ${renderActivityTable(entries, !readOnly())}`;
};

/* Label an activity entry: add it to the evaluation set and, optionally, as an utterance to a route. */
export function labelForm() {
  const l = state.label;
  const routes = state.config.Routes || [];
  const routeOptions = (value, blank) => (blank ? `<option value="">${blank}</option>` : '') + routes.map((r) => `<option value="${h(r.Name)}" ${r.Name === value ? 'selected' : ''}>${h(r.Name)} (T${r.Tier})</option>`).join('');
  return `<div class="card section" id="label-form"><div class="card-head"><h2>Label this prompt</h2><span class="muted small">Routed to tier ${l.routedTier}${l.routedRoute ? ' via ' + h(l.routedRoute) : ''}.${l.truncated ? ' The activity log keeps the first 160 characters of a prompt: complete or shorten it below.' : ''}</span></div>
    <div class="form-grid">
      <label class="field wide"><span class="name">Prompt</span><textarea rows="2" data-state="label.prompt">${h(l.prompt)}</textarea></label>
      <label class="field"><span class="name">Should have been tier</span><select data-state="label.tier" data-kind="number">${tierOptions(l.tier)}</select></label>
      <label class="field"><span class="name">Expected route (optional)</span><select data-state="label.route">${routeOptions(l.route, '(any)')}</select></label>
      <label class="check wide"><input type="checkbox" data-state="label.toSet" data-kind="bool" ${l.toSet ? 'checked' : ''}> Add to the evaluation set${l.localOnly ? ' (marked local-only: left out of exports)' : ''}</label>
      <label class="check wide"><input type="checkbox" data-state="label.toRoute" data-kind="bool" ${l.toRoute ? 'checked' : ''}> Also add it as an utterance, so prompts like it get this tier from now on</label>
      ${l.toRoute ? `<label class="field"><span class="name">Route</span><select data-state="label.utteranceRoute">${routeOptions(l.utteranceRoute, '(new route…)')}</select></label>
        ${l.utteranceRoute ? '' : `<label class="field"><span class="name">New route name</span><input type="text" data-state="label.newRoute" value="${h(l.newRoute || '')}" placeholder="e.g. cooking"><span class="hint">Created with tier ${l.tier}.</span></label>`}` : ''}
    </div>
    <div class="inline" style="margin-top:10px"><button class="btn primary small" data-action="label-save">Save</button><button class="btn ghost small" data-action="label-cancel">Cancel</button></div></div>`;
}
