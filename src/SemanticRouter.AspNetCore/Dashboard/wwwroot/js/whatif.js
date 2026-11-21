/* Semantic Router dashboard: What if page: replay recent requests and the evaluation set through the draft. */
import { adminOnly, api, h, isDirty, readOnly, state, views } from './core.js';
import { draftChanges } from './draft.js';
import { count, plural, reasonHtml, stamp, tierChip } from './labels.js';
import { pageActions } from './page-actions.js';
import { render } from './shell.js';

const DAYS = [1, 7, 30, 90];
const usd = (v) => (Math.abs(v) < 0.005 ? '$0' : (v < 0 ? '−$' : '$') + Math.abs(v).toFixed(2));
const signed = (v) => (Math.abs(v) < 0.005 ? '±$0' : (v > 0 ? '+' : '') + usd(v));
const delta = (before, after) => (after === before ? '<span class="faint">·</span>' : `${after > before ? '+' : '−'}${count(Math.abs(after - before))}`);

Object.assign(pageActions, {
  async 'replay-run'() {
    const r = state.replay; r.busy = true; r.errors = null; render();
    try {
      r.report = await api('POST', `/replay?days=${r.days}`, state.config);
      r.draftChanges = draftChanges(); r.shownDays = r.days;
    } catch (e) {
      r.report = null;
      r.errors = e.data && e.data.errors ? e.data.errors : [e.message];
    }
    r.busy = false; render();
  },
});

/* One decision, as a cell: tier, route or the reason there was none, and the model first in line. */
function decision(d) {
  return `${tierChip(d.tier)} <span class="small">${d.route ? h(d.route) : reasonHtml(d.reason)}</span>
    <div class="small muted">${d.model ? h(d.model) : '<span class="badge danger">no model</span>'}${d.subscription ? ' <span class="badge">subscription</span>' : ''}</div>`;
}

function movesTable(moves, evaluation) {
  if (!moves.length) return '';
  return `<div class="table-wrap"><table><thead><tr><th>${evaluation ? 'Prompt' : 'Request'}</th>${evaluation ? '<th>Labelled</th>' : ''}<th>Now</th><th>With the draft</th>${evaluation ? '' : '<th class="num">Cost</th>'}</tr></thead><tbody>
    ${moves.map((m) => `<tr><td class="small">${h(m.prompt)}${m.at ? `<div class="faint">${m.id ? `<a href="#/activity">#${m.id}</a> · ` : ''}${h(stamp(m.at))}${m.client ? ' · ' + h(m.client) : ''}</div>` : ''}</td>
      ${evaluation ? `<td>${tierChip(m.expectedTier)}</td>` : ''}<td>${decision(m.before)}</td><td>${decision(m.after)}</td>
      ${evaluation ? '' : `<td class="num small">${m.costBeforeUsd == null ? '<span class="faint">no tokens</span>' : `${usd(m.costBeforeUsd)} → ${usd(m.costAfterUsd)}`}</td>`}</tr>`).join('')}
    </tbody></table></div>`;
}

function results(r) {
  const q = r.requests;
  const skipped = [
    q.promptTruncated && `${plural(q.promptTruncated, 'request')} with a prompt longer than its kept preview`,
    q.promptNotKept && `${plural(q.promptNotKept, 'request')} without a kept prompt`,
    q.notClassified && `${plural(q.notClassified, 'request')} the classifier did not route (a requested model, a tool result, a callout, a client side request)`,
    q.beyondLimit && `${plural(q.beyondLimit, 'older request')} beyond the most recent 2,000`,
  ].filter(Boolean);
  const cost = r.cost;
  const summary = `<div class="card section"><div class="card-head"><h2>Recent requests</h2><span class="muted small">last ${plural(state.replay.shownDays, 'day')} · ${plural(r.embedded, 'text')} embedded</span></div>
    ${q.replayed === 0 ? `<div class="empty-note small"><b>Nothing to replay.</b> A request can be replayed when its whole prompt is kept: <code>State:StorePrompts</code> keeps the first 160 characters (Redacted or On). ${skipped.length ? 'In this window: ' + h(skipped.join('; ')) + '.' : ''}</div>` : `
    <div class="grid cols-2" style="margin-bottom:12px">
      <div class="card stat"><span class="value">${count(r.moved)}</span><span class="label">of ${count(q.replayed)} would move</span></div>
      <div class="card stat"><span class="value">${signed(cost.deltaUsd)}</span><span class="label">estimated cost (${usd(cost.beforeUsd)} → ${usd(cost.afterUsd)})</span></div>
    </div>
    ${skipped.length ? `<p class="small muted">Not replayed: ${h(skipped.join('; '))}.</p>` : ''}
    ${cost.unpriced ? `<p class="small muted">${plural(cost.unpriced, 'replayed request')} had no token counts and ${cost.unpriced === 1 ? 'is' : 'are'} left out of the cost.</p>` : ''}
    <div class="grid cols-2">
      <div class="table-wrap plain"><table><thead><tr><th>Tier</th><th class="num">Now</th><th class="num">With the draft</th><th class="num">Change</th></tr></thead><tbody>
        ${r.tiers.map((t) => `<tr><td>${tierChip(t.tier)}</td><td class="num">${count(t.before)}</td><td class="num">${count(t.after)}</td><td class="num">${delta(t.before, t.after)}</td></tr>`).join('')}</tbody></table></div>
      <div class="table-wrap plain"><table><thead><tr><th>Connector</th><th class="num">Now</th><th class="num">With the draft</th><th class="num">Change</th></tr></thead><tbody>
        ${r.connectors.map((c) => `<tr><td>${h(c.connector)}${c.subscription ? ' <span class="badge">subscription</span>' : ''}</td><td class="num">${count(c.before)}</td><td class="num">${count(c.after)}</td><td class="num">${delta(c.before, c.after)}</td></tr>`).join('')}</tbody></table></div>
    </div>
    ${r.moved ? `<h3 style="margin-top:16px">Requests that would move${r.moves.length < r.moved ? ` (the ${r.moves.length} most recent)` : ''}</h3>${movesTable(r.moves, false)}` : ''}`}
  </div>`;
  const ev = r.evaluation;
  const evaluation = ev ? `<div class="card section"><div class="card-head"><h2>Evaluation set</h2><span class="muted small">${plural(ev.cases, 'labelled prompt')}</span></div>
    <div class="grid cols-2" style="margin-bottom:12px">
      <div class="card stat"><span class="value">${count(ev.correctBefore)} → ${count(ev.correctAfter)}</span><span class="label">routed to the tier they are labelled with</span></div>
      <div class="card stat"><span class="value">${count(ev.moved)}</span><span class="label">would move</span></div>
    </div>${movesTable(ev.moves, true)}</div>` : '';
  const notes = (r.notes || []).length ? `<div class="card section"><ul class="small">${r.notes.map((n) => `<li>${h(n)}</li>`).join('')}</ul></div>` : '';
  return summary + evaluation + notes;
}

views.whatif = () => {
  const r = state.replay;
  const changes = draftChanges();
  return `
    <div class="section-head"><h1>What if</h1><span class="muted">Replay recent requests and the evaluation set through your draft and see what would move. No model is called, only the embedding provider.</span></div>
    <div class="card section">
      <p class="small">${isDirty() ? `Your draft has ${plural(changes, 'unapplied change')}.` : 'Your draft has no changes: a replay should move nothing. Edit tiers, routes, thresholds or models on the other pages first.'}
        Each request is routed as a first turn, without the conversation's floor, sticky model, retries or cooldowns, on both sides. When the result looks right, use <b>Apply</b> in the top bar: the change is kept in the configuration history and can be rolled back.</p>
      <div class="inline">
        <label class="small inline-field"><span>Requests from the last</span><select id="replay-days" style="width:auto">${DAYS.map((d) => `<option value="${d}" ${r.days === d ? 'selected' : ''}>${plural(d, 'day')}</option>`).join('')}</select></label>
        <button class="btn primary small" data-action="replay-run" ${adminOnly(r.busy)}>${r.busy ? 'Replaying…' : 'Replay the draft'}</button>
        ${readOnly() ? '<span class="muted small">Replaying needs the admin role.</span>' : ''}
        ${r.report && r.draftChanges !== changes ? '<span class="badge warn">The draft changed since this replay</span>' : ''}
      </div>
      ${r.errors ? `<div class="empty-note small"><b>The draft can't be replayed:</b><ul>${r.errors.map((e) => `<li>${h(e)}</li>`).join('')}</ul></div>` : ''}
    </div>
    ${r.report ? results(r.report) : ''}`;
};
