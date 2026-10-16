/* Semantic Router dashboard: the weekly digest and what needs a decision (Overview). */
import { api, h, metricsOnly, state } from './core.js';
import { count as int, plural, usd } from './labels.js';

/* GET api/digest into state.digest; an older router without it leaves it null. */
export async function loadDigest() {
  try { state.digest = await api('GET', '/digest'); } catch { state.digest = null; }
}

const pct = (v) => Math.round((v || 0) * 100) + '%';
const SIGNALS = { Retried: 'retried', Corrected: 'corrected', Overridden: 'overridden', ToolErrors: 'tool errors', EmptyReply: 'empty replies', Judged: 'graded low' };

/* Only what a person needs to decide; nothing when all is well. */
export function noticesCard() {
  const d = state.digest;
  if (!d || !d.notices.length) return '';
  return `<div class="card section notices" role="region" aria-label="Needs a decision">
    <div class="card-head"><h2>Needs a decision</h2><span class="badge warn">${d.notices.length}</span><span class="spacer"></span>${d.notify ? '' : '<span class="small muted">notifications are off (<span class="mono">Notifications:Enabled</span>)</span>'}</div>
    ${d.notices.map((n) => `<div class="notice" data-kind="${h(n.kind)}"><div class="inline"><b>${h(n.title)}</b><span class="spacer"></span><a href="${h(n.link)}" class="small">decide →</a></div><div class="small muted">${h(n.detail)}</div></div>`).join('')}
  </div>`;
}

/* The last 7 days, as on the Usage page: the Overview's one row of counters. */
export function digestCard() {
  const d = state.digest;
  if (!d) return '';
  const signals = Object.entries(d.signalsByKind || {}).sort((a, b) => b[1] - a[1]).map(([k, v]) => `${int(v)} ${SIGNALS[k] || k}`).join(', ');
  const plan = d.subscriptionValueUsd ? ` · <span title="Not spent: subscription use at the provider's API prices">${usd(d.subscriptionValueUsd)} of subscription use at API prices</span>` : '';
  const reference = metricsOnly() ? 'a reference model on Usage' : '<a href="#/usage" data-action="goto-field" data-view="usage" data-field="Usage.ReferenceModel">set a reference model</a>';
  const saved = d.referenceModel ? `<div class="card stat"><span class="value">${usd(d.savedUsd)}</span><span class="label">saved vs ${h(d.referenceModel)} · spent ${usd(d.costUsd)}${plan}</span></div>`
    : `<div class="card stat"><span class="value">${usd(d.costUsd)}</span><span class="label">spent · ${reference} to see what routing saved${plan}</span></div>`;
  const served = [`${int(d.served)} served`, d.degraded ? `<span title="Answered by a model below the tier they needed (no model at that tier could)">${int(d.degraded)} below their tier</span>` : '', d.failed ? `${int(d.failed)} failed` : ''].filter(Boolean).join(' · ');
  return `<div class="section digest">
    <div class="card-head"><h2>This week</h2><span class="small muted">${h(new Date(d.since).toLocaleDateString(undefined, { day: 'numeric', month: 'short' }))} – today</span><span class="spacer"></span><a href="#/usage" class="small">usage →</a></div>
    <div class="grid cols-4">
      <div class="card stat"><span class="value">${int(d.prompts)}</span><span class="label">prompts routed · ${served}</span></div>
      <div class="card stat"><span class="value">${pct(d.localShare)}</span><span class="label">served locally (${int(d.local)})</span></div>
      ${saved}
      <div class="card stat"><span class="value">${int(d.escalated + d.heldByConversation)}</span><span class="label">raised a tier: for complexity, a retry or the conversation</span></div>
    </div>
    <div class="small muted" style="margin-top:8px">${d.signals ? `Signs a decision fell short: ${h(signals)}.` : 'No signs that a decision fell short.'}
      ${d.learned.length ? ` <a href="#/learned">Router learned ${plural(d.learned.length, 'thing')}</a>: ${d.learned.slice(0, 3).map((c) => h(c.summary) + (c.state === 'Undone' ? ' (undone)' : '')).join('; ')}${d.learned.length > 3 ? '; …' : ''}.` : ''}</div>
  </div>`;
}
