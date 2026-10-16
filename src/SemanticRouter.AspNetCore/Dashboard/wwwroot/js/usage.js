/* Semantic Router dashboard: Usage page: tokens and cost by model, connector, route, client or profile, and budgets. */
import { api, connectorNames, h, metricsOnly, state, toast, views } from './core.js';
import { pct } from './evaluate.js';
import { render } from './shell.js';

/* Usage: what each model, connector, route or client used and cost, and the budgets. */
export async function loadUsage() {
  // A change while a load is under way loads again when it finishes, so the last one wins.
  if (state.usage.loading) { state.usage.again = true; return; }
  state.usage.loading = true;
  state.usage.again = false;
  const days = { day: 1, week: 7, month: 30, '7d': 7, '30d': 30 }[state.usage.window] || 7;
  try {
    const [usage, feedback] = await Promise.all([
      api('GET', `/usage?window=${state.usage.window}&by=${state.usage.by}`),
      api('GET', `/feedback/summary?days=${days}`).catch(() => null),
    ]);
    state.usage.data = usage;
    state.usage.feedback = feedback;
  } catch (e) { toast(e.message, 'danger'); }
  state.usage.loading = false;
  if (state.usage.again) { loadUsage(); return; }
  if (state.view === 'usage') render();
}
export const usd = (v) => '$' + Number(v || 0).toFixed(v && Math.abs(v) < 1 ? 4 : 2);
export const int = (v) => Number(v || 0).toLocaleString();

/** Share of prompt tokens served from a prompt cache: reads over everything the prompts sent. */
export const hitRate = (t) => {
  const prompt = t.inputTokens + t.cacheReadTokens + t.cacheWriteTokens;
  return prompt > 0 && (t.cacheReadTokens || t.cacheWriteTokens) ? pct(t.cacheReadTokens / prompt) : '–';
};
export const budgetRow = (b) => {
  const pct = Math.min(100, b.percent);
  const cls = b.exceeded ? 'danger' : b.warning ? 'warn' : '';
  const limits = [b.maxTokens != null ? `${int(b.tokensUsed)} of ${int(b.maxTokens)} tokens` : null, b.maxCostUsd != null ? `${usd(b.costUsedUsd)} of ${usd(b.maxCostUsd)}` : null].filter(Boolean).join(' · ');
  return `<tr><td><b>${h(b.name)}</b><div class="small faint">${h(b.covers)} · per ${h(String(b.period).toLowerCase())}</div></td>
    <td style="min-width:180px"><div class="meter ${cls}" role="meter" aria-valuenow="${b.percent}" aria-valuemin="0" aria-valuemax="100" aria-label="${h(b.name)}"><i style="width:${pct}%"></i></div></td>
    <td class="small">${b.percent}% ${b.exceeded ? '<span class="badge danger">spent</span>' : b.warning ? '<span class="badge warn">near limit</span>' : '<span class="badge ok">ok</span>'}<div class="faint">${h(limits)}</div></td>
    <td class="small muted">resets ${h(new Date(b.periodEnd).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }))}</td></tr>`;
};
const FEEDBACK_KINDS = [['Retried', 'Retried'], ['Corrected', 'Corrected'], ['Overridden', 'Overridden'], ['ToolErrors', 'Tool errors'], ['EmptyReply', 'Empty'], ['Judged', 'Graded low']];

/* Signs a model was chosen too low (retries, corrections, callouts, failing tools), per route and per model. */
function feedbackCard(f) {
  if (!f) return '';
  const rows = (groups) => groups.map((g) => `<tr><td>${h(g.key)}</td><td class="num">${int(g.decisions)}</td><td class="num">${int(g.withFeedback)}</td><td class="num">${pct(g.rate)}</td>${FEEDBACK_KINDS.map(([k]) => `<td class="num">${int((g.byKind || {})[k])}</td>`).join('')}</tr>`).join('');
  const head = (what) => `<tr><th>${what}</th><th>Decisions</th><th>With feedback</th><th>Rate</th>${FEEDBACK_KINDS.map(([, label]) => `<th>${label}</th>`).join('')}</tr>`;
  return `<div class="card section"><div class="card-head"><h2>Feedback</h2><span class="muted small">Signs a decision was not good enough: the prompt sent again, the answer corrected, a model chosen with a callout, failing tool calls. ${int(f.feedback)} of ${int(f.decisions)} decisions.</span></div>
    ${f.decisions ? `<div class="table-wrap"><table class="wrap-head">${head('Route')}${rows(f.byRoute)}</table></div><div class="table-wrap" style="margin-top:12px"><table class="wrap-head">${head('Model')}${rows(f.byModel)}</table></div>` : '<div class="muted small">No decisions in this window yet.</div>'}
  </div>`;
}

views.usage = () => {
  const u = state.usage; const d = u.data;
  if (!d) { loadUsage(); return '<div class="section-head"><h1>Usage</h1></div><div class="muted">Loading…</div>'; }
  const t = d.totals; const max = Math.max(1e-9, ...d.groups.map((g) => g.totals.costUsd || 0)); const maxTok = Math.max(1, ...d.groups.map((g) => g.totals.inputTokens + g.totals.outputTokens + g.totals.cacheReadTokens + g.totals.cacheWriteTokens));
  const byCost = d.groups.some((g) => g.totals.costUsd > 0);
  const seg = (key, values) => `<span class="seg" role="group">${values.map(([v, label]) => `<button class="${u[key] === v ? 'on' : ''}" data-action="usage-set" data-key="${key}" data-value="${v}">${label}</button>`).join('')}</span>`;
  return `
    <div class="section-head"><h1>Usage</h1><span class="muted">Tokens the backends reported, and cost at the prices in the model catalog.</span><span class="spacer"></span>
      ${seg('window', [['day', 'Today'], ['week', 'This week'], ['month', 'This month'], ['7d', '7 days'], ['30d', '30 days']])}</div>
    <div class="grid cols-4 section">
      <div class="card stat"><span class="value">${int(t.inputTokens + t.outputTokens + t.cacheReadTokens + t.cacheWriteTokens)}</span><span class="label">tokens in ${int(t.requests)} requests · ${int(t.inputTokens)} in · ${int(t.outputTokens)} out · ${int(t.cacheReadTokens)} cache read · ${int(t.cacheWriteTokens)} cache write · <span title="Prompt tokens served from a prompt cache">${hitRate(t)} cache hits</span></span></div>
      <div class="card stat"><span class="value">${usd(t.costUsd)}</span><span class="label">spent, at catalog prices (subscriptions and local models count as 0)</span></div>
      <div class="card stat"><span class="value">${usd(t.subscriptionValueUsd || 0)}</span><span class="label" title="Not spent: what the requests your subscriptions answered would have cost on the provider's API, for models with API prices set">subscription use at API prices · included in your plan</span></div>
      <div class="card stat"><span class="value">${d.referenceModel ? usd(t.referenceCostUsd - t.costUsd) : '–'}</span><span class="label">${d.referenceModel ? 'saved compared with sending everything to ' + h(d.referenceModel) : 'set Usage → reference model to estimate savings'}</span></div>
    </div>
    ${feedbackCard(u.feedback)}
    ${d.budgets.length ? `<div class="card section"><div class="card-head"><h2>Budgets</h2><span class="muted small">A spent budget's models are not tried until it resets.</span></div><table>${d.budgets.map(budgetRow).join('')}</table></div>` : ''}
    <div class="card section"><div class="card-head"><h2>By ${h(u.by)}</h2><span class="spacer"></span>${seg('by', [['model', 'Model'], ['connector', 'Connector'], ['route', 'Route'], ['client', 'Client'], ['profile', 'Profile'], ['location', 'Local or cloud'], ['day', 'Day']])}</div>
      <table><tr><th>${h(u.by)}</th><th>Requests</th><th>Input</th><th>Output</th><th>Cache read</th><th>Cache write</th><th title="Prompt tokens served from a prompt cache">Cache hits</th><th>Cost</th><th title="Subscription use at API prices: not spent">At API prices</th><th style="width:25%"></th></tr>
      ${d.groups.map((g) => { const tk = g.totals.inputTokens + g.totals.outputTokens + g.totals.cacheReadTokens + g.totals.cacheWriteTokens; const share = byCost ? g.totals.costUsd / max : tk / maxTok; return `<tr><td>${h(g.key)}</td><td class="num">${int(g.totals.requests)}</td><td class="num">${int(g.totals.inputTokens)}</td><td class="num">${int(g.totals.outputTokens)}</td><td class="num">${int(g.totals.cacheReadTokens)}</td><td class="num">${int(g.totals.cacheWriteTokens)}</td><td class="num">${hitRate(g.totals)}</td><td class="num">${usd(g.totals.costUsd)}</td><td class="num">${g.totals.subscriptionValueUsd ? usd(g.totals.subscriptionValueUsd) : '–'}</td><td><div class="bar" title="${byCost ? 'share of cost' : 'share of tokens'}"><i style="width:${Math.round(share * 100)}%"></i></div></td></tr>`; }).join('') || '<tr><td colspan="10" class="muted small">Nothing recorded in this window. Usage appears once backends report tokens (streamed OpenAI pass-through replies only when the client asks for <code>stream_options.include_usage</code>).</td></tr>'}
      </table></div>
    ${metricsOnly() ? '' : `<div class="card section"><div class="card-head"><h2>Settings</h2><span class="muted small">Saved with the configuration.</span></div><div class="form-grid">
      <label class="field"><span class="name">Reference model</span><select data-bind="Usage.ReferenceModel" data-kind="nulltext"><option value="">(none)</option>${(state.config.Models || []).map((m) => `<option ${m.Name === (state.config.Usage || {}).ReferenceModel ? 'selected' : ''}>${h(m.Name)}</option>`).join('')}</select><span class="hint">Its prices are the yardstick for "saved".</span></label>
      <label class="field"><span class="name">Warn at (%)</span><input type="number" min="1" max="100" data-bind="Usage.WarnAtPercent" data-kind="number" value="${(state.config.Usage || {}).WarnAtPercent || 80}"></label>
      <label class="field"><span class="name">Time zone for periods</span><input type="text" data-bind="Usage.TimeZone" data-kind="nulltext" value="${h((state.config.Usage || {}).TimeZone || '')}" placeholder="this machine's"></label>
    </div>
    <h3 style="margin-top:14px">Budgets</h3>
    <table><tr><th>Name</th><th>Model</th><th>or connector</th><th>or profile</th><th>Period</th><th>Max tokens</th><th>Max USD</th><th></th></tr>
      ${((state.config.Usage || {}).Budgets || []).map((b, i) => `<tr>
        <td><input type="text" data-bind="Usage.Budgets.${i}.Name" value="${h(b.Name || '')}"></td>
        <td><select data-bind="Usage.Budgets.${i}.Model" data-kind="nulltext"><option value=""></option>${(state.config.Models || []).map((m) => `<option ${m.Name === b.Model ? 'selected' : ''}>${h(m.Name)}</option>`).join('')}</select></td>
        <td><select data-bind="Usage.Budgets.${i}.Connector" data-kind="nulltext"><option value=""></option>${connectorNames().map((c) => `<option ${c === b.Connector ? 'selected' : ''}>${h(c)}</option>`).join('')}</select></td>
        <td><select data-bind="Usage.Budgets.${i}.Profile" data-kind="nulltext"><option value=""></option>${Object.keys(state.config.Profiles || {}).map((x) => `<option ${x === b.Profile ? 'selected' : ''}>${h(x)}</option>`).join('')}</select></td>
        <td><select data-bind="Usage.Budgets.${i}.Period">${['Day', 'Week', 'Month'].map((p) => `<option ${p === (b.Period || 'Day') ? 'selected' : ''}>${p}</option>`).join('')}</select></td>
        <td><input type="number" min="0" data-bind="Usage.Budgets.${i}.MaxTokens" data-kind="number" data-nullable="1" value="${b.MaxTokens ?? ''}"></td>
        <td><input type="number" min="0" step="0.01" data-bind="Usage.Budgets.${i}.MaxCostUsd" data-kind="float" data-nullable="1" value="${b.MaxCostUsd ?? ''}"></td>
        <td><button class="btn ghost small" data-action="budget-remove" data-index="${i}">remove</button></td></tr>`).join('') || '<tr><td colspan="8" class="muted small">No budgets.</td></tr>'}
    </table>
    <div class="inline" style="margin-top:8px"><button class="btn small" data-action="budget-add">Add budget</button></div>
    </div>`}`;
};
