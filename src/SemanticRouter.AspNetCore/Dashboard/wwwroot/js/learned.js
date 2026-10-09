/* Semantic Router dashboard: "Router learned" (automatic tuning): the page, its Overview card and the Activity badge. */
import { api, h, readOnly, state, views } from './core.js';

/* GET api/tuning into state.tuning; an older router without tuning leaves it null. */
export async function loadTuning() {
  try { state.tuning = await api('GET', '/tuning?limit=100'); } catch { state.tuning = null; }
}

const STATES = {
  Applied: ['ok', 'applied'],
  Suggested: ['info', 'suggested'],
  Undone: ['', 'undone'],
  Dismissed: ['', 'dismissed'],
  Expired: ['', 'expired'],
};

const day = (iso) => { const d = new Date(iso); return isNaN(d) ? '' : d.toLocaleDateString(undefined, { day: 'numeric', month: 'short' }) + ' ' + d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' }); };
const rate = (n, of) => (of ? Math.round((n / of) * 100) + '%' : '—');

function effect(e) {
  if (!e) return '';
  const span = e.hours >= 48 ? `${Math.round(e.hours / 24)} days` : `${e.hours} h`;
  return `<div class="small muted">Since then (${span} each side): feedback on ${rate(e.feedbackAfter, e.decisionsAfter)} of ${e.decisionsAfter} decisions, was ${rate(e.feedbackBefore, e.decisionsBefore)} of ${e.decisionsBefore}${e.rerouted ? ` · routed ${e.rerouted} decision${e.rerouted === 1 ? '' : 's'} differently` : ''}.</div>`;
}

function change(c, compact) {
  const [kind, label] = STATES[c.state] || ['', c.state.toLowerCase()];
  const buttons = readOnly() ? '' : c.state === 'Applied'
    ? `<button class="btn small" data-action="tuning-undo" data-id="${c.id}" title="Put this one setting back; other changes stay">Undo</button>`
    : c.state === 'Suggested'
      ? `<button class="btn small primary" data-action="tuning-apply" data-id="${c.id}">Apply</button> <button class="btn small ghost" data-action="tuning-dismiss" data-id="${c.id}">Dismiss</button>`
      : '';
  const done = c.undoneAt ? ` · ${c.state === 'Dismissed' ? 'dismissed' : 'undone'} ${h(day(c.undoneAt))}${c.actor ? ' by ' + h(c.actor) : ''}` : '';
  return `<div class="learned-item" id="learned-${c.id}">
    <div class="inline"><b>${h(c.summary)}</b> <span class="badge ${kind}">${h(label)}</span><span class="spacer"></span>${buttons}</div>
    <div class="small faint">${h(day(c.timestamp))}${c.version ? ` · configuration version ${c.version}` : ''}${done}</div>
    ${compact ? '' : `<p class="small">${h(c.why)}</p>
    ${c.examples.length ? `<div class="small muted">For example: ${c.examples.map((x) => `<span class="mono">“${h(x)}”</span>`).join(', ')}</div>` : ''}
    ${effect(c.effect)}`}
  </div>`;
}

function status(t) {
  const mode = t.paused ? '<span class="badge warn">paused</span>' : t.mode === 'Apply' ? '<span class="badge ok">applies changes</span>' : t.mode === 'Suggest' ? '<span class="badge info">suggests changes</span>' : '<span class="badge">off</span>';
  return `${mode} <span class="small muted">${t.lastRun ? 'last run ' + h(day(t.lastRun)) : 'not run yet'}${t.nextRun ? ' · next ' + h(day(t.nextRun)) : ''}</span>`;
}

views.learned = () => {
  const t = state.tuning;
  if (!t) return '<div class="section-head"><h1>Router learned</h1></div><div class="card muted">This router does not tune itself (no tuning API).</div>';
  const shown = t.changes.filter((c) => state.learnedFilter === 'all' || ['Applied', 'Suggested'].includes(c.state));
  return `
    <div class="section-head"><h1>Router learned</h1><span class="muted">Thresholds and utterances the router changed by itself, why, and what happened since. Each one can be undone.</span></div>
    <div class="card section">
      <div class="inline">${status(t)}<span class="spacer"></span>
        ${readOnly() ? '' : `<button class="btn small" data-action="tuning-run" ${t.mode === 'Off' || t.paused ? 'disabled' : ''}>Run now</button>
        <button class="btn small" data-action="${t.paused ? 'tuning-resume' : 'tuning-pause'}">${t.paused ? 'Resume learning' : 'Pause learning'}</button>
        <button class="btn small danger" data-action="tuning-undo-all" ${t.changes.some((c) => c.state === 'Applied') ? '' : 'disabled'}>Undo all</button>`}
      </div>
      ${t.lastResult ? `<p class="small muted">${h(t.lastResult)}</p>` : ''}
      <p class="small faint">It learns from the <a href="#/evaluate">evaluation set</a> and from feedback (retries, corrections), in steps of at most a few hundredths a day, and only when no labelled prompt routes worse. An undone change is not made again for a while (30 days by default). Settings: <span class="mono">Tuning</span> on the <a href="#/json">JSON</a> page.</p>
    </div>
    <div class="card section">
      <div class="card-head"><h2>Changes</h2><span class="spacer"></span>
        <select data-state="learnedFilter" aria-label="Show" style="width:auto"><option value="active" ${state.learnedFilter !== 'all' ? 'selected' : ''}>applied and suggested</option><option value="all" ${state.learnedFilter === 'all' ? 'selected' : ''}>everything</option></select>
      </div>
      ${shown.length ? shown.map((c) => change(c, false)).join('') : '<div class="muted small">Nothing learned yet.</div>'}
    </div>`;
};

/* The Overview card: what was learned this week. */
export function learnedCard() {
  const t = state.tuning;
  if (!t || (!t.changes.length && t.mode === 'Off')) return '';
  const recent = t.changes.filter((c) => c.state === 'Applied' || c.state === 'Suggested').slice(0, 3);
  const headline = t.thisWeek ? `Router learned ${t.thisWeek} thing${t.thisWeek === 1 ? '' : 's'} this week` : 'Router learned';
  return `<div class="card section">
    <div class="card-head"><h2>${h(headline)}</h2>${t.suggestions ? ` <span class="badge info">${t.suggestions} waiting</span>` : ''}<span class="spacer"></span>${status(t)} <a href="#/learned" class="small">all →</a></div>
    ${recent.length ? recent.map((c) => change(c, true)).join('') : `<div class="muted small">${h(t.lastResult || 'Nothing learned yet.')}</div>`}
  </div>`;
}

/* On an activity entry routed differently because of a learned change. */
export function learnedBadge(e) {
  return e.learned ? ` <a href="#/learned" class="badge accent" title="${h(e.learnedNote || '')}">learned</a>` : '';
}
