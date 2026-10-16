/* Semantic Router dashboard: Overview and Activity pages, the prompt tester result, the decision detail and the label form. */
import { fmtMs, trendsCard } from './charts.js';
import { tierOptions } from './config.js';
import { $, adminOnly, h, metricsOnly, num, readOnly, state, views } from './core.js';
import { digestCard, noticesCard } from './digest.js';
import { endpointsCard } from './endpoints.js';
import { openAttr } from './fields.js';
import { count, fullStamp, plural, reasonHtml, reasonLabel, sourceLabel, stamp, tierChip, usd } from './labels.js';
import { learnedBadge, learnedCard } from './learned.js';
import { isLocalModel } from './live.js';
import { pageActions } from './page-actions.js';
import { unreliableBadge } from './reliability.js';
import { runsOn, serviceCard } from './service.js';
import { gatewayError, setupCard, setupShowing } from './setup.js';
import { render } from './shell.js';

views.overview = () => {
  const s = state.status;
  const a = s.activity;
  const byModel = Object.entries(a.byModel).sort((x, y) => y[1] - x[1]);
  const byTier = Object.entries(a.byTier);
  const max = Math.max(1, ...byModel.map((x) => x[1]), ...byTier.map((x) => x[1]));
  const bars = (rows, label, alt) => rows.length ? `<div class="bars">${rows.map(([k, v]) => `<div class="bar-row"><span class="nowrap" title="${h(k)}">${label ? label(k) : h(k)}</span><div class="bar${alt ? ' alt' : ''}"><i style="width:${Math.round(v / max * 100)}%"></i></div><span class="num">${count(v)}</span></div>`).join('')}</div>` : '<div class="muted small">No requests yet.</div>';
  const setup = setupShowing();

  // What people act on first (setup, decisions, the week, the models, what just happened), then how it runs.
  return `
    <div class="section-head"><h1>Overview</h1><span class="muted">What the router is doing right now.</span></div>
    ${setupCard()}
    ${noticesCard()}
    ${digestCard() || countersRow(s)}
    ${modelsCard(s)}
    ${metricsOnly() ? '' : `<div class="card section">
      <div class="card-head"><h2>Recent activity</h2><span class="spacer"></span><a href="#/activity" class="small">all activity →</a></div>
      ${renderActivityTable(state.activity.slice(0, 8))}
    </div>
    ${testerCard(s)}`}
    ${serviceCard(setup)}
    ${endpointsCard()}
    ${trendsCard()}
    ${learnedCard()}
    <div class="grid cols-2 section">
      <div class="card"><div class="card-head"><h2>Served by</h2><span class="muted small">last ${plural(a.total, 'request')}</span></div>${bars(byModel)}</div>
      <div class="card"><div class="card-head"><h2>Required tier</h2><span class="muted small">last ${plural(a.total, 'request')}</span></div>${bars(byTier, (k) => tierChip(parseInt(k, 10)), true)}</div>
    </div>
    ${aboutCard(s)}`;
};

/* Without a digest (an older router): the counters from the status. */
function countersRow(s) {
  const a = s.activity;
  return `<div class="grid cols-4 section">
      <div class="card stat"><span class="value">${count(a.total)}</span><span class="label">prompts routed (the last ${count(Math.min(a.total, 500))})</span></div>
      <div class="card stat"><span class="value">${count(a.served)}</span><span class="label">served${a.failed ? ` · ${count(a.failed)} failed` : ''}</span></div>
      <div class="card stat"><span class="value">${count(a.degraded)}</span><span class="label">served below the tier they needed</span></div>
      <div class="card stat"><span class="value">${count(s.routes.length)}</span><span class="label">${plural(s.routes.length, 'route')} · ${plural(s.routes.reduce((n, r) => n + r.utterances, 0), 'utterance')}</span></div>
    </div>`;
}

function modelsCard(s) {
  const enabled = s.models.filter((m) => m.enabled).length;
  return `<div class="card section">
    <div class="card-head"><h2>Models</h2><span class="muted small">${enabled} of ${plural(s.models.length, 'model')} enabled · ${plural(s.routes.length, 'route')}</span><span class="spacer"></span>${s.models.some((m) => m.coolingDownUntil) ? '<button class="btn small" data-action="reset-health">Clear cooldowns</button>' : ''}${readOnly() ? '' : '<a href="#/models" class="small">edit →</a>'}</div>
    ${s.models.length ? `<div class="table-wrap"><table>
      <tr><th>Model</th><th>Tier</th><th>Connector</th><th>State</th></tr>
      ${s.models.map((m) => `<tr>
        <td><span class="${m.enabled ? '' : 'muted'}">${h(m.name)}</span>${m.isLocal ? ' <span class="badge info">local</span>' : ''}<div class="small faint mono">${h(m.model || '')}</div></td>
        <td>${tierChip(m.tier)}</td>
        <td class="small">${h(m.connector)} <span class="faint">${h(m.connectorType || '')}</span></td>
        <td>${!m.enabled ? '<span class="badge">disabled</span>' : (m.coolingDownUntil ? `<span class="badge warn">cooling down</span> <span class="small muted">until ${h(stamp(m.coolingDownUntil))}</span>` : '<span class="badge ok">ready</span>')}</td>
      </tr>`).join('')}
    </table></div>` : '<p class="muted small">No models yet.</p>'}
  </div>`;
}

/* The machine and the running configuration: useful now and then, so folded away. */
function aboutCard(s) {
  return `<details class="card section about" ${openAttr('about')}><summary><h2>About this router</h2><span class="muted small">where it runs, its configuration and embeddings</span></summary>
    <div class="grid cols-2" style="margin-top:12px">
      <div><h3>Runs on</h3>${runsOn()}</div>
      <div><h3>Runtime</h3>
        <table>
          <tr><th>Configuration</th><td>v${h(s.version)} · ${h(sourceLabel(s.source))} <span class="faint small" title="${h(fullStamp(s.createdAt))}">${h(stamp(s.createdAt))}</span></td></tr>
          <tr><th>Store</th><td class="mono small break">${s.store ? h(s.store) : '<span class="muted">none: changes apply until the router restarts</span>'}</td></tr>
          <tr><th>Embeddings</th><td>${h(s.embeddings.provider)} ${s.embeddings.model ? '· <span class="mono">' + h(s.embeddings.model) + '</span>' : ''} ${s.embeddings.endpoint ? '<span class="faint small">' + h(s.embeddings.endpoint) + '</span>' : ''}
            ${s.warmedUp ? '<span class="badge ok">index ready</span>' : (s.warmUpError ? `<span class="badge danger" title="${h(s.warmUpError)}">warm-up failed</span>` : '<span class="badge info">warming up</span>')}
            ${s.embeddings.index ? `<div class="faint small">${plural(s.embeddings.index.utterances, 'utterance')} embedded in ${fmtMs(s.embeddings.index.buildMilliseconds)} · ${count(s.embeddings.index.dimensions)} dimensions</div>` : ''}</td></tr>
          ${s.embeddings.cache ? `<tr><th>Classification cache</th><td>${s.embeddings.cache.hits + s.embeddings.cache.misses ? `<b>${Math.round(s.embeddings.cache.hitRate * 100)}%</b> of prompts reused a recent decision (${count(s.embeddings.cache.hits)} of ${count(s.embeddings.cache.hits + s.embeddings.cache.misses)})` : '<span class="muted">no prompts yet</span>'} <span class="faint small">· ${count(s.embeddings.cache.entries)} of ${count(s.embeddings.cache.capacity)} kept</span></td></tr>` : ''}
          <tr><th>Gateway</th><td>${h(s.gateway.mode)} · OpenAI on <span class="mono">${h(s.gateway.pathPrefixes.join(', ') || '(all JSON POSTs)')}</span> · Anthropic on <span class="mono">${h(s.gateway.anthropicPathPrefixes.join(', '))}</span> · Responses on <span class="mono">${h(s.gateway.responsesPathPrefixes.join(', '))}</span></td></tr>
          <tr><th>Strategy</th><td>${h(s.gateway.strategy)}</td></tr>
          <tr><th>Connectors</th><td>${s.connectors.map((c) => `<span class="badge" title="${h(c.target || '')}">${h(c.name)} <span class="faint">${h(c.type)}${c.preset ? '/' + h(c.preset) : ''}</span></span>`).join(' ') || '<span class="muted">none</span>'}</td></tr>
        </table>
      </div>
    </div>
  </details>`;
}

const MODELS = ['auto', 'auto:cheapest', 'auto:best', 'auto:fastest', 'auto:balanced', 'auto:priority'];

function testerCard(s) {
  const t = state.tester;
  return `<div class="card section tester" id="tester">
      <div class="card-head"><h2>Try a prompt</h2><span class="muted small">Classify calls no model. Send goes through the gateway for real${s.gateway.mode !== 'Dispatch' ? ' (Annotate mode: your endpoint answers)' : ''}.</span></div>
      <textarea id="tester-prompt" aria-label="Prompt to try" placeholder="Paste a prompt…">${h(t.prompt)}</textarea>
      <div class="inline" style="margin-top:8px">
        <select id="tester-model" aria-label="Model" style="width:220px">
          ${MODELS.concat(s.models.map((m) => m.name)).map((m) => `<option ${m === t.model ? 'selected' : ''}>${h(m)}</option>`).join('')}
        </select>
        <button class="btn primary" data-action="tester-classify" ${adminOnly(t.busy)}>Classify</button>
        <button class="btn" data-action="tester-send" ${adminOnly(t.busy)}>Send</button>
        ${t.busy ? '<span class="muted small">working…</span>' : ''}
      </div>
      ${t.result ? renderClassification(t.result) : ''}
      ${t.reply ? `<div class="result-line">${t.reply.headers.map(([k, v]) => `<span class="badge">${h(k.replace('X-Prompt-Route', 'route'))}: ${h(v)}</span>`).join('')}<span class="muted small">${count(t.reply.ms)} ms</span></div><div class="reply">${h(t.reply.text)}</div>${replyHint(t.reply)}` : ''}
    </div>`;
}

/* A failed Send says why and where to fix it, instead of only the gateway's status line. */
function replyHint(reply) {
  const err = gatewayError(reply);
  if (!err) return '';
  const s = state.status;
  const PAGES = { overview: 'the setup steps', models: 'Models', connectors: 'Connectors', gateway: 'Gateway settings' };
  const link = err.page && PAGES[err.page] ? ` <a href="#/${err.page}"${err.page === 'overview' ? ' data-action="setup-scroll"' : ''}>Open ${PAGES[err.page]}</a>` : '';
  // The router's own message (a 400 with code no_model_available) already says why and where: show it, linked.
  if (err.code === 'no_model_available') return `<p class="small next-step">${h(err.message.replace(/\s*https?:\/\/\S+#\/\S+/, '').trim())}${link}</p>`;
  const where = setupShowing() ? 'Follow the steps in <a href="#/overview" data-action="setup-scroll">Set up SemanticRouter</a> above.'
    : '<a href="#/models">Models</a> (enabled, tier, capabilities) and <a href="#/connectors">Connectors</a> (Check) are where to look.';
  if (!s.models.some((x) => x.enabled)) return `<p class="small next-step">No model is enabled, so nothing can answer. ${where}</p>`;
  if (err.status === 401 || err.status === 403) return '<p class="small next-step">The gateway refused the request: see <a href="#/gateway">Gateway → Settings</a> (gateway keys).</p>';
  if (err.status === 429) return '<p class="small next-step">Over a rate limit: see <a href="#/gateway">Gateway → Settings</a> (rate limits).</p>';
  return `<p class="small next-step">No model could answer this one. ${where} The <a href="#/activity">Activity</a> entry has the details.</p>`;
}

export function renderClassification(r) {
  const none = !r.models.length;
  return `
    <div class="result-line">
      ${tierChip(r.requiredTier)}
      <span class="badge">${reasonHtml(r.reason)}</span>
      ${r.callout ? `<span class="badge accent" title="Routed as the inline callout asks; classified without it">${h(r.callout)}</span>` : ''}
      ${r.route ? `<span class="badge">route ${h(r.route)} · ${num(r.similarity, 3)}</span>` : ''}
      <span class="badge">complexity ${num(r.complexity.score, 3)}</span>
      ${r.localOnly ? '<span class="badge warn">local only</span>' : ''}
      ${(r.requiredCapabilities || []).map((c) => `<span class="badge info">${h(c)}</span>`).join('')}
      <span class="muted small">${num(r.elapsedMilliseconds, 1)} ms</span>
    </div>
    <div class="grid cols-2">
      <div><h3>Models, in order</h3><ul class="candidates">${r.models.map((m, i) => `<li><span class="faint mono">${i + 1}.</span> <b>${h(m.model)}</b> ${tierChip(m.tier)} <span class="muted small">${h(m.connector)} · ${spend(m)}</span>${m.meetsRequiredTier ? '' : '<span class="badge warn">below tier</span>'}</li>`).join('') || '<li class="muted">no eligible model</li>'}</ul>
        ${none ? `<p class="small next-step">${state.status.models.some((x) => x.enabled)
          ? `No enabled model reaches tier ${h(r.requiredTier)}${(r.requiredCapabilities || []).length ? ` with ${h(r.requiredCapabilities.join(', '))}` : ''}${r.localOnly ? ' on this machine' : ''}. Add or enable one on <a href="#/models">Models</a>, or allow answers below the tier under <a href="#/routing">Rules</a>.`
          : 'No model is enabled. Add one: see the setup steps at the top of the Overview, or <a href="#/connectors">Connectors</a>.'}</p>` : ''}</div>
      <div><h3>Routes</h3><ul class="candidates">${r.routes.map((x) => `<li><b>${h(x.route)}</b> ${tierChip(x.tier)} <span class="muted small">${num(x.similarity, 3)}</span></li>`).join('') || '<li class="muted">no routes</li>'}</ul>
        <div class="small muted" style="margin-top:6px">signals: length ${num(r.complexity.lengthSignal, 2)} · code ${num(r.complexity.codeSignal, 2)} · structure ${num(r.complexity.structureSignal, 2)} · reasoning ${num(r.complexity.reasoningSignal, 2)} · ~${count(r.complexity.estimatedTokens)} tokens</div></div>
    </div>`;
}

/* The route column: the route and its score, or the best score when no route matched. */
const routeCell = (e) => (e.route ? `${h(e.route)}${e.similarity != null ? ` <span class="faint">${num(e.similarity, 2)}</span>` : ''}`
  : e.similarity != null ? `<span class="muted">no route</span> <span class="faint">(best ${num(e.similarity, 2)})</span>` : '<span class="faint">—</span>');

const ICONS = {
  label: '<svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M3 12V4h8l10 10-8 8z"/><circle cx="7.5" cy="8.5" r="1.3"/></svg>',
  report: '<svg class="icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M5 21V4M5 4h11l-2 4 2 4H5"/></svg>',
};

/*
 * With actions (the Activity page), each entry gets Report (a pre-filled GitHub issue) and, for the admin role, Label,
 * and a click or Enter on the row opens the whole decision.
 */
export function renderActivityTable(entries, actions) {
  if (!entries.length) return '<div class="muted small">No requests routed yet. Point a client at the gateway or use “Try a prompt”.</div>';
  return `<div class="table-wrap"><table class="activity-table">
    <tr><th>Time</th><th>Prompt</th><th>Tier</th><th>Reason</th><th>Route</th><th>Served by</th><th class="num">ms</th>${actions ? '<th><span class="sr-only">Actions</span></th>' : ''}</tr>
    ${entries.map((e) => `<tr title="${h(e.detail || '')}"${actions ? ` data-entry-id="${e.id}" tabindex="0" class="clickable${state.activityDetail === e.id ? ' selected' : ''}"` : ''}>
      <td class="nowrap small">${h(stamp(e.timestamp))}</td>
      <td class="prompt-cell"><span class="small">${e.promptPreview ? h(e.promptPreview) : '<span class="faint">prompt not stored</span>'}</span><div class="faint small">${e.client ? '<span class="badge info">' + h(e.client) + '</span> ' : ''}${h(e.protocol)} ${h(e.requestedModel || '')}${e.hasTools ? ' · tools' : ''}${e.toolContinuation ? ' · tool result' : ''}${e.localOnly ? ' · local only' : ''} · ~${count(e.estimatedInputTokens)} tok${e.inputTokens != null || e.outputTokens != null ? ` · ${count(e.inputTokens)} in / ${count(e.outputTokens)} out` : ''}${e.cacheReadTokens ? ` · <span title="Prompt tokens served from the backend's prompt cache">${count(e.cacheReadTokens)} cached</span>` : ''}${e.cacheCreationTokens ? ` · <span title="Prompt tokens written to the backend's prompt cache">${count(e.cacheCreationTokens)} to cache</span>` : ''}${e.costUsd ? ' · ' + usd(e.costUsd) : ''}${e.budget ? ' · <span class="badge warn">budget ' + h(e.budget) + '</span>' : ''}${e.dropped ? ' · <span class="badge warn" title="Not carried to the serving backend">dropped ' + h(e.dropped) + '</span>' : ''}${e.profile ? ' · <span class="badge accent" title="Routing profile">' + h(e.profile) + '</span>' : ''}${e.project ? ' · <span class="mono project" title="' + h('Project: ' + e.project) + '">' + h(e.project) + '</span>' : ''}${e.signals ? ' · <span class="badge warn" title="Kept local because the conversation contains this">local: ' + h(e.signals) + '</span>' : ''}${e.promptedToolCalls ? ` · <span class="badge" title="Tool calls read out of the model's text (prompted tool calling), not returned by a native tool API">${plural(e.promptedToolCalls, 'prompted call')}</span>` : ''}${e.notes ? ' · <span class="badge ' + (/malformed/.test(e.notes) ? 'warn' : 'info') + '" title="Noted by the connector">' + h(e.notes) + '</span>' : ''}${memoryBadge(e)}</div></td>
      <td>${tierChip(e.tier)}</td>
      <td class="small">${reasonHtml(e.reason)}${e.cached ? ' <span class="badge" title="Reused a recent decision: no embedding call">cached</span>' : ''}${learnedBadge(e)}</td>
      <td class="small">${routeCell(e)}</td>
      <td class="small served">${e.outcome === 'failed' ? '<span class="badge danger">failed</span>' : e.outcome === 'rate-limited' ? `<span class="badge warn" title="${h(e.detail || '')}">rate limited</span>` : e.servedBy ? `<b>${h(e.servedBy)}</b>${e.attempt > 1 ? ` <span class="badge warn">try ${e.attempt}</span>` : ''}${e.degraded ? ' <span class="badge warn">degraded</span>' : ''}` : `<span class="muted">${h(e.plannedModel || '—')}</span> <span class="faint">planned</span>`}${e.sticky ? ' <span class="badge" title="Stayed on the conversation\'s model: the one that answered its last turn, or the one with its warm prompt cache">sticky</span>' : ''}${e.selection ? ` <span class="badge" title="${h(e.selection)}">${h(e.selection.split(':')[0])}</span>` : ''}${e.callout ? ` <span class="badge accent" title="Chosen with an inline callout">${h(e.callout)}</span>` : ''}${unreliableBadge(e)}${feedbackBadges(e)}${gradeBadge(e)}</td>
      <td class="num small">${e.totalMilliseconds != null ? count(Math.round(e.totalMilliseconds)) : num(e.classificationMilliseconds, 1)}${e.timeToFirstByteMilliseconds != null && e.totalMilliseconds != null ? `<div class="faint" title="Time to the first byte of the reply">first ${count(Math.round(e.timeToFirstByteMilliseconds))}</div>` : ''}</td>
      ${actions ? `<td class="nowrap row-icons">${e.toolContinuation ? '' : entryButtons(e)}</td>` : ''}
    </tr>`).join('')}
  </table></div>`;
}

function entryButtons(e) {
  return `${readOnly() ? '' : `<button class="btn ghost small icon-btn" data-action="label-start" data-id="${e.id}" aria-label="Label: say which tier this prompt should have had" title="Label: say which tier this prompt should have had">${ICONS.label}</button>`}<button class="btn ghost small icon-btn" data-action="report-entry" data-id="${e.id}" aria-label="Report this decision on GitHub" title="${e.outcome === 'failed' ? 'Report this failure as a GitHub issue' : 'Report a misroute as a GitHub issue, with this decision filled in and the prompt left out'}">${ICONS.report}</button>`;
}

/* What a model in the tester's list uses up: nothing (local, and whether it is loaded), quota (a free model elsewhere, such as a subscription), or money. */
function spend(m) {
  if (m.estimatedCostUsd > 0) return usd(m.estimatedCostUsd, true);
  if (!m.isLocal) return '<span title="Free per request, but it uses up the subscription\'s quota">subscription</span>';
  return m.loaded === true ? 'local · <span class="badge ok" title="In memory now: answers without waiting for a load">loaded</span>'
    : m.loaded === false ? 'local · <span title="Not in memory: the first request waits for it to load">not loaded</span>' : 'local';
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
  return ` <a href="#/memory" class="badge ${cloud && text ? 'signal' : 'info'}" title="${h(title)}">${plural(notes.length, 'note')}${cloud ? ' → cloud' : ''}</a>`;
}

function judgeNote() {
  const j = state.config && state.config.Middleware && state.config.Middleware.Judge;
  if (!j || !j.Enabled || !j.Model) return '';
  return ` The judge (<span class="mono">${h(j.Model)}</span>) grades ${+(j.SampleRate * 100).toFixed(1)}% of the answers from tier ${j.MaxTier} and below, at most ${count(j.DailyLimit)} a day.`;
}

const FILTERS = [['all', 'Everything'], ['failed', 'Failed'], ['degraded', 'Degraded'], ['tools', 'With tools'], ['prompted calls', 'Prompted tool calls']];
const matches = (e, q) => !q || [e.promptPreview, e.route, e.servedBy, e.plannedModel, e.requestedModel, e.client, e.profile, reasonLabel(e.reason), e.reason]
  .some((v) => v && String(v).toLowerCase().includes(q));

views.activity = () => {
  const f = state.activityFilter;
  const q = (state.activitySearch || '').trim().toLowerCase();
  const entries = state.activity.filter((e) => f === 'all' || (f === 'failed' ? e.outcome === 'failed' : f === 'degraded' ? e.degraded : f === 'tools' ? e.hasTools : f === 'prompted calls' ? e.promptedToolCalls > 0 : f.startsWith('profile:') ? e.profile === f.slice(8) : true))
    .filter((e) => matches(e, q));
  const profiles = [...new Set(state.activity.map((e) => e.profile).filter(Boolean).concat(Object.keys((state.config && state.config.Profiles) || {})))];
  const detail = state.activityDetail != null ? state.activity.find((e) => e.id === state.activityDetail) : null;
  return `
    <div class="section-head"><h1>Activity</h1><span class="muted">The last ${plural(state.activity.length, 'routed request')}, newest first. Click a row for the whole decision.${judgeNote()}</span></div>
    <div class="toolbar section">
      <label class="inline-field"><span>Show</span><select id="activity-filter">${FILTERS.map(([x, label]) => `<option value="${x}" ${x === f ? 'selected' : ''}>${label}</option>`).join('')}${profiles.map((x) => `<option value="profile:${h(x)}" ${'profile:' + x === f ? 'selected' : ''}>Profile ${h(x)}</option>`).join('')}</select></label>
      <label class="inline-field grow"><span>Search</span><input type="search" id="activity-search" value="${h(state.activitySearch || '')}" placeholder="prompt, route, model, client"></label>
      <span class="spacer"></span>
      <label class="check"><input type="checkbox" id="auto-refresh" ${state.autoRefresh ? 'checked' : ''}> Auto-refresh</label>
      <button class="btn small" data-action="refresh">Refresh</button></div>
    ${state.label ? labelForm() : ''}
    ${detail ? decisionDetail(detail) : ''}
    ${q && !entries.length ? `<div class="muted small">Nothing in the last ${plural(state.activity.length, 'request')} matches “${h(state.activitySearch.trim())}”.</div>` : renderActivityTable(entries, true)}`;
};

/* The whole decision for one request: what was asked, how it was classified, which model answered and what followed. */
function decisionDetail(e) {
  const row = (label, value) => (value === '' || value == null ? '' : `<tr><th>${label}</th><td>${value}</td></tr>`);
  const tokens = [e.inputTokens != null ? `${count(e.inputTokens)} in` : `~${count(e.estimatedInputTokens)} in (estimated)`, e.outputTokens != null ? `${count(e.outputTokens)} out` : '', e.cacheReadTokens ? `${count(e.cacheReadTokens)} read from cache` : '', e.cacheCreationTokens ? `${count(e.cacheCreationTokens)} written to cache` : ''].filter(Boolean).join(' · ');
  const timing = [e.classificationMilliseconds != null ? `classified in ${num(e.classificationMilliseconds, 1)} ms` : '', e.timeToFirstByteMilliseconds != null ? `first byte after ${count(Math.round(e.timeToFirstByteMilliseconds))} ms` : '', e.totalMilliseconds != null ? `done after ${count(Math.round(e.totalMilliseconds))} ms` : ''].filter(Boolean).join(' · ');
  const feedback = ((state.feedback || {})[e.id] || []).map((k) => (FEEDBACK[k] || [k])[0]).join(', ');
  return `<div class="card section decision" id="activity-detail" role="region" aria-label="${h(`Request ${e.id}`)}" tabindex="-1">
    <div class="card-head"><h2>Request #${e.id}</h2><span class="muted small">${h(fullStamp(e.timestamp))}</span><span class="spacer"></span>${e.toolContinuation ? '' : entryButtons(e)}<button class="btn ghost small" data-action="activity-detail-close">Close</button></div>
    <div class="grid cols-2">
      <table class="kv"><tr><th colspan="2" class="group">Request</th></tr>
        ${row('Prompt', e.promptPreview ? `<span class="break">${h(e.promptPreview)}</span>` : '<span class="faint">not stored</span>')}
        ${row('Client', [e.client && `<span class="badge info">${h(e.client)}</span>`, h(e.protocol), e.requestedModel && `model <span class="mono">${h(e.requestedModel)}</span>`].filter(Boolean).join(' · '))}
        ${row('Carries', [e.hasTools && 'tools', e.toolContinuation && 'a tool result', e.localOnly && 'local only'].filter(Boolean).join(', '))}
        ${row('Project', e.project ? `<span class="mono break">${h(e.project)}</span>` : '')}
        ${row('Profile', e.profile ? `<span class="badge accent">${h(e.profile)}</span>` : '')}
        ${row('Tokens', tokens)}
        ${row('Cost', e.costUsd ? usd(e.costUsd, true) : '')}
        ${row('Memory', memoryBadge(e))}
      </table>
      <table class="kv"><tr><th colspan="2" class="group">Decision</th></tr>
        ${row('Tier', tierChip(e.tier))}
        ${row('Why', `${h(reasonLabel(e.reason))} <span class="faint small mono">${h(e.reason)}</span>${e.cached ? ' <span class="badge">reused a recent decision</span>' : ''}${learnedBadge(e)}`)}
        ${row('Route', routeCell(e))}
        ${row('Complexity', e.complexity != null ? num(e.complexity, 3) : '')}
        ${row('Callout', e.callout ? `<span class="badge accent">${h(e.callout)}</span>` : '')}
        ${row('Kept local by', e.signals ? h(e.signals) : '')}
        ${row('Selection', e.selection ? h(e.selection) : '')}
        ${row('Planned model', e.plannedModel ? h(e.plannedModel) : '')}
        ${row('Answered by', e.servedBy ? `<b>${h(e.servedBy)}</b>${e.attempt > 1 ? ` on try ${e.attempt}` : ''}${e.degraded ? ' <span class="badge warn">below the tier it needed</span>' : ''}${e.sticky ? ' <span class="badge">stayed on the conversation\'s model</span>' : ''}` : '')}
        ${row('Outcome', `${h(e.outcome || '')}${e.detail ? `<div class="small muted break">${h(e.detail)}</div>` : ''}`)}
        ${row('Sent to', (e.sentTo || []).map(h).join(', '))}
        ${row('Skipped', e.unreliable ? `${h(e.unreliable)} (unreliable for this work)` : '')}
        ${row('Not carried', e.dropped ? h(e.dropped) : '')}
        ${row('Connector noted', e.notes ? h(e.notes) : '')}
        ${row('Timing', timing)}
        ${row('Feedback', feedback)}
        ${row('Judge', e.grade != null ? `${e.grade}/5${e.gradeNote ? ` · ${h(e.gradeNote)}` : ''}` : '')}
      </table>
    </div></div>`;
}

/* Label an activity entry: add it to the evaluation set and, optionally, as an utterance to a route. */
export function labelForm() {
  const l = state.label;
  const routes = state.config.Routes || [];
  const routeOptions = (value, blank) => (blank ? `<option value="">${blank}</option>` : '') + routes.map((r) => `<option value="${h(r.Name)}" ${r.Name === value ? 'selected' : ''}>${h(r.Name)} (T${r.Tier})</option>`).join('');
  const same = l.tier != null && l.tier === l.routedTier;
  return `<div class="card section" id="label-form"><div class="card-head"><h2>Label this prompt</h2><span class="muted small">Routed to ${tierChip(l.routedTier)}${l.routedRoute ? ' via ' + h(l.routedRoute) : ''}.${l.truncated ? ' The activity log keeps the first 160 characters of a prompt: complete or shorten it below.' : ''}</span></div>
    <div class="form-grid">
      <label class="field wide"><span class="name">Prompt</span><textarea rows="2" data-state="label.prompt">${h(l.prompt)}</textarea></label>
      <label class="field"><span class="name">Should have been tier</span><select id="label-tier" data-state="label.tier" data-kind="number" required aria-describedby="label-tier-hint">${tierOptions(l.tier, 'Pick a tier…')}</select>
        <span class="hint" id="label-tier-hint">${same ? '<span class="warn-text">The tier it was routed to: this records the decision as right.</span>' : 'The tier this prompt needed.'}</span></label>
      <label class="field"><span class="name">Expected route (optional)</span><select data-state="label.route">${routeOptions(l.route, '(any)')}</select></label>
      <label class="check wide"><input type="checkbox" data-state="label.toSet" data-kind="bool" ${l.toSet ? 'checked' : ''}> Add to the evaluation set${l.localOnly ? ' (marked local-only: left out of exports)' : ''}</label>
      <label class="check wide"><input type="checkbox" data-state="label.toRoute" data-kind="bool" ${l.toRoute ? 'checked' : ''}> Also add it as an utterance, so prompts like it get this tier from now on</label>
      ${l.toRoute ? `<label class="field"><span class="name">Route</span><select data-state="label.utteranceRoute">${routeOptions(l.utteranceRoute, '(new route…)')}</select></label>
        ${l.utteranceRoute ? '' : `<label class="field"><span class="name">New route name</span><input type="text" data-state="label.newRoute" value="${h(l.newRoute || '')}" placeholder="e.g. cooking"><span class="hint">Created with ${l.tier == null ? 'the tier you pick' : 'tier ' + h(l.tier)}.</span></label>`}` : ''}
    </div>
    <div class="inline" style="margin-top:10px"><button class="btn primary small" data-action="label-submit">Save</button><button class="btn ghost small" data-action="label-cancel">Cancel</button></div></div>`;
}

/* ---------------------------------------------------------------------------------------------
   Page actions and wiring
   --------------------------------------------------------------------------------------------- */
const shared = async () => (await import('./actions.js')).actions; // at click time, once every module is loaded

function openDetail(id) {
  state.activityDetail = state.activityDetail === id ? null : id;
  render();
  const panel = $('#activity-detail');
  if (panel) { panel.scrollIntoView({ block: 'nearest' }); panel.focus({ preventScroll: true }); }
}

Object.assign(pageActions, {
  // Label starts with no tier picked: the routed tier as a default made "Save" record the decision as right.
  async 'label-start'(el) {
    (await shared())['label-open'](el);
    if (state.label) { state.label.tier = null; render(); const form = $('#label-form'); if (form) form.scrollIntoView({ block: 'nearest' }); }
  },
  async 'label-submit'() {
    if (state.label && state.label.tier == null) {
      $('#label-tier').focus();
      throw new Error('Pick the tier this prompt should have had.');
    }
    await (await shared())['label-save']();
  },
  'activity-detail-close'() { state.activityDetail = null; render(); },
  'setup-scroll'() { const card = $('#setup'); if (card) card.scrollIntoView({ block: 'start' }); },
});

// A row of the Activity table opens its decision, by click or Enter; links, buttons and selecting text do not.
document.addEventListener('click', (ev) => {
  const row = ev.target.closest && ev.target.closest('tr[data-entry-id]');
  if (!row || ev.target.closest('a, button, input, select, textarea, label')) return;
  if (String(window.getSelection && window.getSelection()).trim()) return;
  openDetail(+row.dataset.entryId);
});
document.addEventListener('keydown', (ev) => {
  if (ev.key !== 'Enter' || !ev.target.matches || !ev.target.matches('tr[data-entry-id]')) return;
  ev.preventDefault();
  openDetail(+ev.target.dataset.entryId);
});
let searchTimer = null;
document.addEventListener('input', (ev) => {
  if (ev.target.id !== 'activity-search') return;
  state.activitySearch = ev.target.value;
  clearTimeout(searchTimer);
  searchTimer = setTimeout(() => { if (state.view === 'activity') render(); }, 150);
});
