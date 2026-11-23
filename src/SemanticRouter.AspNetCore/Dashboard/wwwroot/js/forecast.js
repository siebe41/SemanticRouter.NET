/* Semantic Router dashboard: the quota forecast (Overview): when each subscription and budget runs out at this pace. */
import { api, h, readOnly, state } from './core.js';
import { plural, stamp, tierChip, usd } from './labels.js';
import { compact } from './live.js';

/* How often the Overview asks again: the forecast moves by the hour, not by the second. */
const REFRESH_MS = 60_000;

/* GET api/forecast into state.forecast; an older router without it leaves it null. */
export async function loadForecast(force) {
  const f = state.forecast;
  if (!force && f.at && Date.now() - f.at < REFRESH_MS) return;
  f.at = Date.now();
  try { f.data = await api('GET', '/forecast'); } catch { f.data = null; }
}

const STATES = {
  Hot: ['danger', 'runs out before the reset'],
  Cold: ['info', 'room to spare'],
  Ok: ['ok', 'on pace'],
  Limited: ['warn', 'out of its limit'],
  Spent: ['warn', 'spent'],
  Unknown: ['', 'no forecast yet'],
};
const badge = (status, budget) => { const [kind, label] = STATES[status] || STATES.Unknown; return `<span class="badge ${kind}">${h(budget && status === 'Hot' ? 'spent before the period ends' : label)}</span>`; };
const amount = (v, unit) => (v == null ? '—' : unit === 'Usd' ? usd(v) : unit === 'Requests' ? plural(Math.round(v), 'request') : `${compact(Math.round(v))} tokens`);
const pct = (v) => Math.round((v || 0) * 100) + '%';
const meter = (share, status) => `<div class="meter${status === 'Hot' || share >= 1 ? ' danger' : status === 'Limited' || status === 'Spent' || share >= 0.8 ? ' warn' : ''}" role="img" aria-label="${pct(share)} used"><i style="width:${Math.min(100, Math.round(share * 100))}%"></i></div>`;

/* Where the limit comes from, and how sure the forecast is of it. */
function limitNote(c) {
  const l = c.limit;
  if (l.source === 'configured') return 'limit set by hand (Usage:Quotas)';
  if (l.source === 'learned') return `limit learned from ${plural(l.cycles, 'cycle')} (${l.confidence} confidence)`;
  return c.limitHits ? `${plural(c.limitHits, 'limit hit')} recorded, no whole cycle yet` : 'no limit hit recorded yet';
}

function runsOut(c) {
  if (c.status === 'Limited') return c.limitedUntil ? `out until ${h(stamp(c.limitedUntil))}` : 'out now';
  if (!c.runsOutAt) return c.limit.value != null && c.used != null ? '<span class="faint">not at this pace</span>' : '—';
  const band = c.runsOutEarliest && c.runsOutLatest ? ` <span class="faint small" title="The back-test's error over ${plural(c.backtest.cycles, 'past cycle')}">(${h(stamp(c.runsOutEarliest))} – ${h(stamp(c.runsOutLatest))})</span>` : '';
  return `${h(stamp(c.runsOutAt))}${band}`;
}

/* Running hot: what burns it, and where it could go. Running cold: what could move up to it. */
function suggestions(c, whatIf) {
  const lines = [];
  if (c.status === 'Hot' && c.routes.length) {
    lines.push(`Driving it: ${c.routes.map((r) => `<b>${h(r.key)}</b> ${pct(r.share)}${r.moveTo ? ` <span class="faint">(${r.moveTo.tier < r.moveTo.fromTier ? `tier ${r.moveTo.tier} would send it to` : 'could go to'} ${h(r.moveTo.model)}${r.moveTo.local ? ', local' : ''})</span>` : ''}`).join(', ')}.`);
    if (c.clients.length > 1 || (c.clients[0] && c.clients[0].key !== '(no key)')) lines.push(`Clients: ${c.clients.map((x) => `${h(x.key)} ${pct(x.share)}`).join(', ')}.`);
  }
  if (c.status === 'Cold' && c.shareAtReset != null) {
    const spare = `About ${pct(1 - c.shareAtReset)} unused before ${h(stamp(c.nextReset))}`;
    lines.push(c.upgrades.length
      ? `${spare}; these routes could move up: ${c.upgrades.map((u) => `<b>${h(u.route)}</b> ${tierChip(u.tier)} → ${tierChip(u.moveTo.tier)} <span class="faint">(${plural(u.requests, 'request')} last week${u.fits ? '' : ', more than fits'})</span>`).join(', ')}.`
      : `${spare}.`);
  }
  if (!lines.length) return '';
  return `<div class="small muted">${lines.join(' ')}${whatIf ? ` <a href="${h(whatIf)}">Check a change on What if →</a>` : ''}</div>`;
}

export function forecastCard() {
  const d = state.forecast.data;
  if (!d || (!d.connectors.length && !d.budgets.length)) return '';
  // What if replays the draft: only for those who can change it.
  const whatIf = readOnly() ? null : d.whatIf;
  const rows = d.connectors.map((c) => `<tr data-connector="${h(c.connector)}">
      <td><b>${h(c.connector)}</b>${c.subscription ? ' <span class="badge">subscription</span>' : ''}<div class="faint small">${h(limitNote(c))}</div></td>
      <td>${badge(c.status)}</td>
      <td>${c.used != null && c.limit.value ? `${meter(c.used / c.limit.value, c.status)}<div class="small muted">${amount(c.used, c.unit)} of ${amount(c.limit.value, c.unit)}</div>` : `<span class="small muted">${c.used != null ? amount(c.used, c.unit) : '—'}</span>`}</td>
      <td class="small">${runsOut(c)}</td>
      <td class="small">${c.nextReset ? h(stamp(c.nextReset)) : '<span class="faint">not known</span>'}</td>
    </tr>${suggestions(c, whatIf) || c.notes.length ? `<tr class="sub"><td colspan="5">${suggestions(c, whatIf) || `<div class="small faint">${h(c.notes[0])}</div>`}</td></tr>` : ''}`);
  const budgets = d.budgets.map((b) => `<tr data-budget="${h(b.name)}">
      <td><b>${h(b.name)}</b> <span class="badge">budget</span><div class="faint small">${h(b.covers)} · per ${h(b.period.toLowerCase())}</div></td>
      <td>${badge(b.status, true)}</td>
      <td>${meter(b.used / b.limit, b.status)}<div class="small muted">${amount(b.used, b.unit)} of ${amount(b.limit, b.unit)}</div></td>
      <td class="small">${b.runsOutAt ? h(stamp(b.runsOutAt)) : '<span class="faint">not this period</span>'}</td>
      <td class="small">${h(stamp(b.periodEnd))}</td>
    </tr>`);
  return `<div class="card section" id="forecast" role="region" aria-label="Quota forecast">
    <div class="card-head"><h2>Quota forecast</h2><span class="muted small">When each subscription and budget runs out at the current pace</span><span class="spacer"></span>${whatIf ? `<a href="${h(whatIf)}" class="small">what if →</a>` : ''}</div>
    <div class="table-wrap"><table class="forecast">
      <tr><th>Limit</th><th>State</th><th>This cycle</th><th>Runs out</th><th>Resets</th></tr>
      ${rows.join('')}${budgets.join('')}
    </table></div>
  </div>`;
}
