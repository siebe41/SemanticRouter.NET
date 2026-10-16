/* Semantic Router dashboard: Usage page: tokens and cost by model, connector, route, client or profile, and budgets. */
import { api, connectorNames, h, metricsOnly, state, toast, views } from './core.js';
import { pct } from './evaluate.js';
import { numField, timeZoneList } from './fields.js';
import { count as int, plural, stamp, usd } from './labels.js';
import { pageActions } from './page-actions.js';
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
// One money and count format for every page (labels.js); kept under these names for the modules that import them here.
export { usd, count as int } from './labels.js';

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
    <td class="small muted">resets ${h(stamp(b.periodEnd))}</td></tr>`;
};
const FEEDBACK_KINDS = [['Retried', 'Retried'], ['Corrected', 'Corrected'], ['Overridden', 'Overridden'], ['ToolErrors', 'Tool errors'], ['EmptyReply', 'Empty'], ['Judged', 'Graded low']];

/* Signs a model was chosen too low (retries, corrections, callouts, failing tools), per route and per model. One line
   when there are none. */
function feedbackCard(f) {
  if (!f) return '';
  const intro = 'Signs a decision was not good enough: the prompt sent again, the answer corrected, a model chosen with a callout, failing tool calls.';
  if (!f.feedback) return `<div class="card section flat-line"><b>Feedback</b> <span class="muted small">${f.decisions ? `No signs a decision fell short in ${plural(f.decisions, 'decision')}.` : 'No decisions in this window yet.'}</span> <span class="faint small" title="${h(intro)}">What counts?</span></div>`;
  const rows = (groups) => groups.map((g) => `<tr><td>${h(g.key)}</td><td class="num">${int(g.decisions)}</td><td class="num">${int(g.withFeedback)}</td><td class="num">${pct(g.rate)}</td>${FEEDBACK_KINDS.map(([k]) => `<td class="num">${int((g.byKind || {})[k])}</td>`).join('')}</tr>`).join('');
  const head = (what) => `<tr><th>${what}</th><th class="num">Decisions</th><th class="num">With feedback</th><th class="num">Rate</th>${FEEDBACK_KINDS.map(([, label]) => `<th class="num">${label}</th>`).join('')}</tr>`;
  return `<div class="card section"><div class="card-head"><h2>Feedback</h2><span class="muted small">${intro} ${int(f.feedback)} of ${plural(f.decisions, 'decision')}.</span></div>
    <div class="table-wrap"><table class="wrap-head">${head('Route')}${rows(f.byRoute)}</table></div><div class="table-wrap" style="margin-top:12px"><table class="wrap-head">${head('Model')}${rows(f.byModel)}</table></div>
  </div>`;
}

const BY = { model: 'Model', connector: 'Connector', route: 'Route', client: 'Client', profile: 'Profile', location: 'Local or cloud', day: 'Day' };
const SCOPES = [['Model', 'Model'], ['Connector', 'Connector'], ['Profile', 'Profile']];
const scopeOf = (b) => SCOPES.map(([k]) => k).find((k) => b[k]) || 'Model';

/* The budget editor: each budget covers one model, connector or profile (the router asks for exactly one). */
function budgetsEditor() {
  const c = state.config;
  const budgets = (c.Usage || {}).Budgets || [];
  const targets = { Model: (c.Models || []).map((m) => m.Name), Connector: connectorNames(), Profile: Object.keys(c.Profiles || {}) };
  return `<h3 style="margin-top:14px">Budgets</h3>
    <div class="table-wrap"><table class="budgets"><tr><th>Name</th><th>Covers</th><th>Period</th><th class="num">Max tokens</th><th class="num">Max USD</th><th></th></tr>
      ${budgets.map((b, i) => {
        const scope = scopeOf(b);
        const options = targets[scope];
        return `<tr>
        <td><input type="text" aria-label="Budget name" data-bind="Usage.Budgets.${i}.Name" value="${h(b.Name || '')}"></td>
        <td><div class="scope" role="radiogroup" aria-label="What budget ${h(b.Name || i + 1)} covers">${SCOPES.map(([k, label]) => `<label class="check small"><input type="radio" name="budget-scope-${i}" data-budget-scope="${k}" data-index="${i}" ${k === scope ? 'checked' : ''} ${targets[k].length ? '' : 'disabled'}> ${label}</label>`).join('')}</div>
          <select aria-label="${h(scope)} the budget covers" data-bind="Usage.Budgets.${i}.${scope}" data-kind="nulltext">${b[scope] ? '' : '<option value="" selected disabled>Pick one…</option>'}${options.map((x) => `<option ${x === b[scope] ? 'selected' : ''}>${h(x)}</option>`).join('')}${b[scope] && !options.includes(b[scope]) ? `<option selected>${h(b[scope])}</option>` : ''}</select></td>
        <td><select aria-label="Period" data-bind="Usage.Budgets.${i}.Period">${['Day', 'Week', 'Month'].map((p) => `<option ${p === (b.Period || 'Day') ? 'selected' : ''}>${p}</option>`).join('')}</select></td>
        <td><input type="number" min="0" step="1000" aria-label="Max tokens" data-bind="Usage.Budgets.${i}.MaxTokens" data-kind="number" data-nullable="1" value="${b.MaxTokens ?? ''}" placeholder="no limit"></td>
        <td><input type="number" min="0" step="0.01" aria-label="Max USD" data-bind="Usage.Budgets.${i}.MaxCostUsd" data-kind="float" data-nullable="1" value="${b.MaxCostUsd ?? ''}" placeholder="no limit"></td>
        <td><button class="btn danger small" data-action="budget-remove" data-index="${i}">Remove</button></td></tr>`;
      }).join('') || '<tr><td colspan="6" class="muted small">No budgets.</td></tr>'}
    </table></div>
    <div class="inline" style="margin-top:8px"><button class="btn small" data-action="budget-new" ${(c.Models || []).length || connectorNames().length ? '' : 'disabled title="Add a model or a connector first"'}>Add budget</button><span class="small muted">Each budget needs a limit in tokens, dollars or both.</span></div>`;
}

views.usage = () => {
  const u = state.usage; const d = u.data;
  if (!d) { loadUsage(); return '<div class="section-head"><h1>Usage</h1></div><div class="muted">Loading…</div>'; }
  const t = d.totals; const max = Math.max(1e-9, ...d.groups.map((g) => g.totals.costUsd || 0)); const maxTok = Math.max(1, ...d.groups.map((g) => g.totals.inputTokens + g.totals.outputTokens + g.totals.cacheReadTokens + g.totals.cacheWriteTokens));
  const byCost = d.groups.some((g) => g.totals.costUsd > 0);
  const seg = (key, values, label) => `<span class="seg" role="group" aria-label="${label}">${values.map(([v, text]) => `<button class="${u[key] === v ? 'on' : ''}" data-action="usage-set" data-key="${key}" data-value="${v}" aria-pressed="${u[key] === v}">${text}</button>`).join('')}</span>`;
  const usage = state.config.Usage || {};
  const reference = metricsOnly() ? 'a reference model' : '<a href="#/usage" data-action="goto-field" data-view="usage" data-field="Usage.ReferenceModel">set a reference model</a>';
  return `
    <div class="section-head"><h1>Usage</h1><span class="muted">Tokens the backends reported, and cost at the prices in the model catalog.</span><span class="spacer"></span>
      ${seg('window', [['day', 'Today'], ['week', 'This week'], ['month', 'This month'], ['7d', '7 days'], ['30d', '30 days']], 'Window')}</div>
    <div class="grid cols-4 section">
      <div class="card stat"><span class="value">${int(t.inputTokens + t.outputTokens + t.cacheReadTokens + t.cacheWriteTokens)}</span><span class="label">tokens in ${plural(t.requests, 'request')} · ${int(t.inputTokens)} in · ${int(t.outputTokens)} out · ${int(t.cacheReadTokens)} cache read · ${int(t.cacheWriteTokens)} cache write · <span title="Prompt tokens served from a prompt cache">${hitRate(t)} cache hits</span></span></div>
      <div class="card stat"><span class="value">${usd(t.costUsd)}</span><span class="label">spent, at catalog prices (subscriptions and local models count as 0)</span></div>
      <div class="card stat"><span class="value">${usd(t.subscriptionValueUsd || 0)}</span><span class="label" title="Not spent: what the requests your subscriptions answered would have cost on the provider's API, for models with API prices set">subscription use at API prices · included in your plan</span></div>
      <div class="card stat"><span class="value">${d.referenceModel ? usd(t.referenceCostUsd - t.costUsd) : '–'}</span><span class="label">${d.referenceModel ? 'saved compared with sending everything to ' + h(d.referenceModel) : `${reference} to see what routing saved`}</span></div>
    </div>
    ${feedbackCard(u.feedback)}
    ${d.budgets.length ? `<div class="card section"><div class="card-head"><h2>Budgets</h2><span class="muted small">A spent budget's models are not tried until it resets.</span></div><div class="table-wrap"><table>${d.budgets.map(budgetRow).join('')}</table></div></div>` : ''}
    <div class="card section"><div class="card-head"><h2>By ${h((BY[u.by] || u.by).toLowerCase())}</h2><span class="spacer"></span>${seg('by', Object.entries(BY), 'Group by')}</div>
      <div class="table-wrap"><table class="wrap-head usage-table"><tr><th>${h(BY[u.by] || u.by)}</th><th class="num">Requests</th><th class="num">Input</th><th class="num">Output</th><th class="num">Cache read</th><th class="num">Cache write</th><th class="num" title="Prompt tokens served from a prompt cache">Cache hits</th><th class="num">Cost</th><th class="num" title="Subscription use at API prices: not spent">At API prices</th><th class="share"><span class="sr-only">Share</span></th></tr>
      ${d.groups.map((g) => { const tk = g.totals.inputTokens + g.totals.outputTokens + g.totals.cacheReadTokens + g.totals.cacheWriteTokens; const share = byCost ? g.totals.costUsd / max : tk / maxTok; return `<tr><td>${h(g.key)}</td><td class="num">${int(g.totals.requests)}</td><td class="num">${int(g.totals.inputTokens)}</td><td class="num">${int(g.totals.outputTokens)}</td><td class="num">${int(g.totals.cacheReadTokens)}</td><td class="num">${int(g.totals.cacheWriteTokens)}</td><td class="num">${hitRate(g.totals)}</td><td class="num">${usd(g.totals.costUsd)}</td><td class="num">${g.totals.subscriptionValueUsd ? usd(g.totals.subscriptionValueUsd) : '–'}</td><td class="share"><div class="bar" title="${byCost ? 'share of cost' : 'share of tokens'}"><i style="width:${Math.round(share * 100)}%"></i></div></td></tr>`; }).join('') || '<tr><td colspan="10" class="muted small">Nothing recorded in this window. Usage appears once backends report tokens (streamed OpenAI pass-through replies only when the client asks for <code>stream_options.include_usage</code>).</td></tr>'}
      </table></div></div>
    ${metricsOnly() ? '' : `<div class="card section"><div class="card-head"><h2>Settings</h2><span class="muted small">Part of the configuration: apply them with the save bar.</span></div><div class="form-grid">
      <label class="field"><span class="name">Reference model</span><select data-bind="Usage.ReferenceModel" data-kind="nulltext"><option value="">(none)</option>${(state.config.Models || []).map((m) => `<option ${m.Name === usage.ReferenceModel ? 'selected' : ''}>${h(m.Name)}</option>`).join('')}</select><span class="hint">Its prices are the yardstick for "saved".</span></label>
      ${numField({ bind: 'Usage.WarnAtPercent', value: usage.WarnAtPercent, def: 80, label: 'Warn at (%)', min: 1, max: 100, step: 1, hint: 'A budget this far used shows a warning.' })}
      <label class="field"><span class="name">Time zone for periods</span><input type="text" data-bind="Usage.TimeZone" data-kind="nulltext" value="${h(usage.TimeZone || '')}" placeholder="this machine's" list="usage-time-zones"><span class="hint">When a day, week or month starts.</span></label>
    </div>${timeZoneList('usage-time-zones')}
    ${budgetsEditor()}
    </div>`}`;
};

/* A new budget covers the first model (or connector) with a $5 a day limit, so it is valid as soon as it is added. */
Object.assign(pageActions, {
  'budget-new'() {
    const c = state.config;
    const usage = c.Usage = c.Usage || {};
    const budgets = usage.Budgets = usage.Budgets || [];
    let n = budgets.length + 1;
    while (budgets.some((b) => (b.Name || '').toLowerCase() === `budget-${n}`)) n++;
    const model = (c.Models || [])[0];
    budgets.push({ Name: `budget-${n}`, Model: model ? model.Name : null, Connector: model ? null : connectorNames()[0] || null, Profile: null, Period: 'Day', MaxTokens: null, MaxCostUsd: 5 });
    render();
  },
});

// A budget's scope: choosing Model, Connector or Profile clears the other two and picks the first of its kind.
document.addEventListener('change', (ev) => {
  const el = ev.target;
  if (!el.dataset || !el.dataset.budgetScope) return;
  const b = state.config.Usage.Budgets[+el.dataset.index];
  const first = { Model: (state.config.Models || []).map((m) => m.Name), Connector: connectorNames(), Profile: Object.keys(state.config.Profiles || {}) }[el.dataset.budgetScope][0] || null;
  SCOPES.forEach(([k]) => { b[k] = k === el.dataset.budgetScope ? b[k] || first : null; });
  render();
});
