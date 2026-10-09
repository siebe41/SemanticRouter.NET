/* Semantic Router dashboard: model reliability per kind of work (Models page card, model badges, Activity badge). */
import { api, h, readOnly, state } from './core.js';
import { render } from './shell.js';

/* GET api/reliability into state.reliability; an older router without it leaves it empty. */
export async function loadReliability() {
  if (state.reliabilityLoading) return;
  state.reliabilityLoading = true;
  try { state.reliability = await api('GET', '/reliability'); } catch { state.reliability = { enabled: false, models: [] }; }
  state.reliabilityLoading = false;
  if (state.view === 'models') render();
}

const work = (r) => (r.route ? `route ${r.route}` : `${r.capability} use`);
const day = (iso) => { const d = new Date(iso); return isNaN(d) ? '' : d.toLocaleDateString(undefined, { day: 'numeric', month: 'short' }) + ' ' + d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' }); };
const left = (r) => r.excludedUntil && new Date(r.excludedUntil) > new Date();

/* The card at the top of the Models page. */
export function reliabilityCard() {
  const r = state.reliability;
  if (!r) { loadReliability(); return ''; }
  const rows = r.models.filter((m) => m.turns > 0 || left(m)).sort((a, b) => (left(b) - left(a)) || (b.rate - a.rate));
  if (!rows.length) return '';
  return `<div class="card section">
    <div class="card-head"><h2>Reliability</h2><span class="muted small">How often each model's turns drew feedback (retried, corrected, tool errors, empty replies), per route and with tools${r.enabled ? `. A model clearly worse than the others at some work is left out of it for a while, then tried again.` : '. Leaving unreliable models out is off (<span class="mono">Middleware:Reliability</span>).'}</span></div>
    <div class="table-wrap"><table>
      <tr><th>Model</th><th>Work</th><th class="num">Turns</th><th class="num">With feedback</th><th class="num">Others</th><th></th></tr>
      ${rows.map((m) => `<tr>
        <td><b>${h(m.model)}</b></td>
        <td class="small">${h(work(m))}</td>
        <td class="num">${m.turns}</td>
        <td class="num">${Math.round(m.rate * 100)}%</td>
        <td class="num faint">${m.peerRate != null ? Math.round(m.peerRate * 100) + '%' : '—'}</td>
        <td class="small">${left(m) ? `<span class="badge warn" title="${h(m.detail || '')}">left out until ${h(day(m.excludedUntil))}</span> <span class="faint">${h(m.detail || '')}</span>${readOnly() ? '' : ` <button class="btn small" data-action="reliability-reset" data-model="${h(m.model)}" ${m.route ? `data-route="${h(m.route)}"` : `data-capability="${h(m.capability)}"`}>Offer again</button>`}` : (m.turns < r.minimumTurns ? '<span class="faint">too few turns to judge</span>' : '')}</td>
      </tr>`).join('')}
    </table></div>
  </div>`;
}

/* On a model's row: the work it is left out of. */
export function reliabilityBadges(name) {
  const r = state.reliability;
  if (!r) return '';
  return r.models.filter((m) => left(m) && m.model.toLowerCase() === String(name || '').toLowerCase())
    .map((m) => `<span class="badge warn" title="${h(m.detail || '')}">not used for ${h(work(m))}</span>`).join('');
}

/* On an activity entry: the models left out of it. */
export function unreliableBadge(e) {
  return e.unreliable ? ` <a href="#/models" class="badge warn" title="Left out: unreliable for this kind of work">skipped ${h(e.unreliable)}</a>` : '';
}
