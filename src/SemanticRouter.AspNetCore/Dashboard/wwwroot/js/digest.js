/* Semantic Router dashboard: the weekly digest and what needs a decision (Overview). */
import { api, h, state } from './core.js';
import { int, usd } from './usage.js';

/* GET api/digest into state.digest; an older router without it leaves it null. */
export async function loadDigest() {
  try { state.digest = await api('GET', '/digest'); } catch { state.digest = null; }
}

const pct = (v) => Math.round((v || 0) * 100) + '%';
const plural = (n, word) => `${int(n)} ${word}${n === 1 ? '' : 's'}`;
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

/* The last 7 days, as on the Usage page. */
export function digestCard() {
  const d = state.digest;
  if (!d) return '';
  const signals = Object.entries(d.signalsByKind || {}).sort((a, b) => b[1] - a[1]).map(([k, v]) => `${int(v)} ${SIGNALS[k] || k}`).join(', ');
  const plan = d.subscriptionValueUsd ? ` · <span title="Not spent: subscription use at the provider's API prices">${usd(d.subscriptionValueUsd)} of subscription use at API prices</span>` : '';
  const saved = d.referenceModel ? `<div class="card stat"><span class="value">${usd(d.savedUsd)}</span><span class="label">saved vs ${h(d.referenceModel)} · spent ${usd(d.costUsd)}${plan}</span></div>`
    : `<div class="card stat"><span class="value">${usd(d.costUsd)}</span><span class="label">spent (set <span class="mono">Usage:ReferenceModel</span> to see savings)${plan}</span></div>`;
  return `<div class="section digest">
    <div class="card-head"><h2>This week</h2><span class="small muted">${h(new Date(d.since).toLocaleDateString(undefined, { day: 'numeric', month: 'short' }))} – today</span><span class="spacer"></span><a href="#/usage" class="small">usage →</a></div>
    <div class="grid cols-4">
      <div class="card stat"><span class="value">${int(d.prompts)}</span><span class="label">prompts routed · ${int(d.served)} served</span></div>
      <div class="card stat"><span class="value">${pct(d.localShare)}</span><span class="label">served locally (${int(d.local)})</span></div>
      ${saved}
      <div class="card stat"><span class="value">${int(d.escalated + d.heldByConversation)}</span><span class="label">raised a tier${d.degraded ? ` · ${int(d.degraded)} degraded` : ''}${d.failed ? ` · ${int(d.failed)} failed` : ''}</span></div>
    </div>
    <div class="small muted" style="margin-top:8px">${d.signals ? `Signs a decision fell short: ${h(signals)}.` : 'No signs that a decision fell short.'}
      ${d.learned.length ? ` <a href="#/learned">Router learned ${plural(d.learned.length, 'thing')}</a>: ${d.learned.slice(0, 3).map((c) => h(c.summary) + (c.state === 'Undone' ? ' (undone)' : '')).join('; ')}${d.learned.length > 3 ? '; …' : ''}.` : ''}</div>
  </div>`;
}
