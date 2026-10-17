/* Semantic Router dashboard: Evaluate page: evaluation set, calibration, route health and embedding comparison. */
import { H, PAD, W, niceMax } from './charts.js';
import { API, adminOnly, api, h, num, readOnly, state, tierName, toast, views } from './core.js';
import { plural, reasonHtml, tierChip } from './labels.js';
import { render } from './shell.js';

/* Evaluate: run labelled prompts through the configuration, calibrate thresholds, check routes, compare embeddings. */
export const pct = (v) => (v * 100).toFixed(v === 1 || v === 0 ? 0 : 1) + '%';
export async function loadEvaluationSet() {
  try {
    const [r, judge] = await Promise.all([api('GET', '/evaluation/set'), api('GET', '/judge/agreement').catch(() => null)]);
    state.evaluation.judge = judge;
    state.evaluation.location = r.location; state.evaluation.count = r.count;
    state.evaluation.text = r.cases.map((c) => JSON.stringify(Object.fromEntries(Object.entries({ prompt: c.prompt, tier: c.tier, route: c.route, localOnly: c.localOnly || undefined, note: c.note }).filter(([, v]) => v != null)))).join('\n');
    state.evaluation.savedText = state.evaluation.text; // what the router holds: typed changes beyond it are unsaved
  } catch (e) { state.evaluation.text = ''; state.evaluation.savedText = null; toast(e.message, 'danger'); }
  if (state.view === 'evaluate') render();
}

export function sweepChart(c, total) {
  const pts = c.sweep;
  const iw = W - PAD.l - PAD.r; const ih = H - PAD.t - PAD.b;
  const max = niceMax(Math.max(1, ...pts.map((p) => Math.max(p.tooLow, p.tooHigh))));
  const x = (t) => PAD.l + t * iw; const y = (v) => PAD.t + ih - v / max * ih;
  const path = (key) => pts.filter((p) => p.threshold >= 0).map((p, i) => (i ? 'L' : 'M') + x(p.threshold).toFixed(1) + ' ' + y(p[key]).toFixed(1)).join('');
  let g = '';
  [0, 0.5, 1].forEach((f) => { const yy = PAD.t + ih - f * ih; g += `<line class="grid-line" x1="${PAD.l}" x2="${W - PAD.r}" y1="${yy}" y2="${yy}"/><text class="axis" x="${PAD.l - 6}" y="${yy + 3}" text-anchor="end">${Math.round(max * f)}</text>`; });
  [0, 0.25, 0.5, 0.75, 1].forEach((t) => { g += `<text class="axis" x="${x(t)}" y="${H - 6}" text-anchor="middle">${t}</text>`; });
  const marker = (t, label, dy) => `<line class="marker" x1="${x(t)}" x2="${x(t)}" y1="${PAD.t}" y2="${PAD.t + ih}"/><text class="end-label" x="${x(t) + 4}" y="${PAD.t + 10 + dy}">${h(label)} ${t.toFixed(2)}</text>`;
  const last = pts[pts.length - 1];
  // The two end labels sit next to their lines' last points, at least a line of text apart when the points are close.
  let low = y(last.tooLow) + 4; let high = y(last.tooHigh) - 4;
  if (Math.abs(low - high) < 13) {
    const mid = (low + high) / 2; const lowFirst = last.tooLow >= last.tooHigh; // the larger count is drawn higher
    low = mid + (lowFirst ? -6.5 : 6.5); high = mid + (lowFirst ? 6.5 : -6.5);
  }
  // Both stay inside the plot, moved together so they keep their distance.
  const shift = Math.min(0, PAD.t + ih + 4 - Math.max(low, high)) || Math.max(0, PAD.t + 8 - Math.min(low, high));
  const clamp = (v) => v + shift;
  const hits = pts.filter((p) => p.threshold >= 0).map((p) => `<rect class="hit" x="${x(p.threshold) - iw / 200}" y="${PAD.t}" width="${iw / 100}" height="${ih}" data-tip="${h(`threshold ${p.threshold.toFixed(2)}\n${p.correct} of ${total} right · ${p.tooLow} too low · ${p.tooHigh} too high`)}"/>`).join('');
  return `<svg class="chart" viewBox="0 0 ${W} ${H}" role="img" aria-label="Misroutes by default similarity threshold">${g}
    <path class="line-2" d="${path('tooLow')}"/><path class="line-1" d="${path('tooHigh')}"/>
    <circle class="dot-2" cx="${x(last.threshold)}" cy="${y(last.tooLow)}" r="4"/><text class="end-label" x="${x(last.threshold) + 8}" y="${clamp(low)}">too low</text>
    <circle class="dot-1" cx="${x(last.threshold)}" cy="${y(last.tooHigh)}" r="4"/><text class="end-label" x="${x(last.threshold) + 8}" y="${clamp(high)}">too high</text>
    ${marker(c.current.threshold, 'current', 0)}${c.recommended.threshold !== c.current.threshold ? marker(c.recommended.threshold, 'recommended', 14) : ''}${hits}</svg>`;
}

export function evaluationResults(r) {
  const ev = state.evaluation;
  const tiersAxis = r.tiers;
  const maxCell = Math.max(1, ...r.confusion.flat());
  const confusion = `<div class="table-wrap plain"><table class="matrix confusion"><tr><th>expected ↓ / routed →</th>${tiersAxis.map((t) => `<th class="num">T${t}</th>`).join('')}</tr>
    ${r.confusion.map((row, i) => `<tr><th>T${tiersAxis[i]}</th>${row.map((n, j) => `<td class="cell ${n ? (i === j ? 'hit-ok' : j < i ? 'hit-low' : 'hit-high') : ''}" style="--share:${(n / maxCell).toFixed(2)}" title="${plural(n, 'prompt')} labelled T${tiersAxis[i]} routed to T${tiersAxis[j]}">${n || '<span class="faint">·</span>'}</td>`).join('')}</tr>`).join('')}</table></div>`;
  const routes = state.config.Routes || [];
  const misroutes = r.misroutes.map((m, i) => {
    const open = ev.addFor === i;
    return `<tr><td class="small">${h(m.prompt)}${m.localOnly && !m.keptLocal ? ' <span class="badge danger">left local-only</span>' : ''}</td>
      <td>${tierChip(m.expectedTier)}${m.expectedRoute ? ` <span class="small muted">${h(m.expectedRoute)}</span>` : ''}</td>
      <td>${tierChip(m.actualTier)} ${m.actualTier < m.expectedTier ? '<span class="badge warn">too low</span>' : m.actualTier > m.expectedTier ? '<span class="badge info">too high</span>' : ''}<div class="small muted">${reasonHtml(m.reason)}${m.actualRoute ? ' · ' + h(m.actualRoute) : ''}</div></td>
      <td class="small">${m.topScores.map((x) => `${h(x.route)} <span class="faint">${num(x.similarity, 3)}</span>`).join('<br>') || '<span class="faint">no embedding (complexity)</span>'}</td>
      <td>${readOnly() ? '' : open
        ? `<div class="inline"><select id="add-route">${routes.map((x) => `<option value="${h(x.Name)}" ${x.Tier === m.expectedTier ? 'selected' : ''}>${h(x.Name)} (T${x.Tier})</option>`).join('')}</select><button class="btn primary small" data-action="eval-add-save" data-index="${i}">Add</button><button class="btn ghost small" data-action="eval-add-cancel">×</button></div>`
        : `<button class="btn ghost small" data-action="eval-add-open" data-index="${i}" title="Add this prompt as an utterance to a route">Add to route…</button>`}</td></tr>`;
  }).join('');
  const c = r.calibration;
  return `<div class="grid cols-4 section">
      <div class="card stat"><span class="value">${pct(r.accuracy)}</span><span class="label">${r.correct} of ${plural(r.total, 'prompt')} routed to their tier</span></div>
      <div class="card stat"><span class="value">${r.tooLow}</span><span class="label">too low: a weaker model than the prompt needs (costs quality)</span></div>
      <div class="card stat"><span class="value">${r.tooHigh}</span><span class="label">too high: a stronger model than needed (costs money)</span></div>
      <div class="card stat"><span class="value">${r.routeLabelled ? pct(r.routeCorrect / r.routeLabelled) : '–'}</span><span class="label">${r.routeLabelled ? `of ${plural(r.routeLabelled, 'prompt')} with an expected route matched it` : 'no prompt names an expected route'}${r.localOnlyMissed ? ` · <b>${plural(r.localOnlyMissed, 'local-only prompt')} could leave the machine</b>` : ''}</span></div>
    </div>
    <div class="grid cols-2 section">
      <div class="card"><div class="card-head"><h2>By expected tier</h2></div><div class="table-wrap plain"><table><tr><th>Tier</th><th class="num">Prompts</th><th class="num">Right</th><th style="width:40%"></th></tr>
        ${r.perTier.map((t) => `<tr><td>${h(tierName(t.tier))}</td><td class="num">${t.total}</td><td class="num">${pct(t.accuracy)}</td><td><div class="meter ${t.accuracy < 0.5 ? 'danger' : t.accuracy < 0.8 ? 'warn' : ''}" role="meter" aria-valuenow="${Math.round(t.accuracy * 100)}" aria-valuemin="0" aria-valuemax="100" aria-label="tier ${t.tier} accuracy"><i style="width:${Math.round(t.accuracy * 100)}%"></i></div></td></tr>`).join('')}</table></div></div>
      <div class="card"><div class="card-head"><h2>Confusion</h2><span class="muted small">Below the diagonal: routed too low. Above: too high.</span></div>${confusion}</div>
    </div>
    ${c ? `<div class="card section"><div class="card-head"><h2>Threshold calibration</h2><span class="muted small">Every default threshold from 0 to 1, on this set.</span><span class="spacer"></span>
        ${readOnly() ? '' : `<button class="btn small" data-action="eval-use-calibration" ${c.recommended.threshold === c.current.threshold && !c.routes.length ? 'disabled' : ''}>Use recommended values</button>`}</div>
      <div class="grid cols-2">
        <div><div class="legend"><span><i class="key" style="background:var(--series-2)"></i>too low</span><span><i class="key" style="background:var(--series-1)"></i>too high</span></div>${sweepChart(c, r.total)}</div>
        <div><table><tr><th><span class="visually-hidden">Setting</span></th><th class="num">Threshold</th><th class="num">Right</th><th class="num">Too low</th><th class="num">Too high</th></tr>
          <tr><td>Current</td><td class="num">${c.current.threshold.toFixed(2)}</td><td class="num">${c.current.correct}</td><td class="num">${c.current.tooLow}</td><td class="num">${c.current.tooHigh}</td></tr>
          <tr><td><b>Recommended</b></td><td class="num"><b>${c.recommended.threshold.toFixed(2)}</b></td><td class="num">${c.recommended.correct}</td><td class="num">${c.recommended.tooLow}</td><td class="num">${c.recommended.tooHigh}</td></tr></table>
          ${c.routes.length ? `<h3 style="margin-top:12px">Route thresholds</h3><table><tr><th>Route</th><th class="num">Now</th><th class="num">Suggested</th><th class="num">Right</th></tr>${c.routes.map((x) => `<tr><td>${h(x.route)}</td><td class="num">${x.current == null ? 'default' : x.current.toFixed(2)}</td><td class="num">${x.recommended == null ? 'default' : x.recommended.toFixed(2)}</td><td class="num">${x.correctBefore} → ${x.correctAfter}</td></tr>`).join('')}</table><p class="small muted">With these: ${c.correctWithRoutes} of ${r.total} right.</p>` : '<p class="small muted" style="margin-top:8px">No per-route threshold does better than the default on this set.</p>'}
          <p class="small muted">Ties go to fewer prompts routed too low, then to the value closest to the current one. A small set overfits: add prompts before trusting a large change.</p></div>
      </div>
      <details style="margin-top:8px"><summary class="small muted">Show the sweep as a table</summary><table><tr><th class="num">Threshold</th><th class="num">Right</th><th class="num">Too low</th><th class="num">Too high</th></tr>${c.sweep.filter((p, i) => i % 5 === 0 || p.threshold === c.current.threshold || p.threshold === c.recommended.threshold).map((p) => `<tr><td class="num">${p.threshold.toFixed(2)}</td><td class="num">${p.correct}</td><td class="num">${p.tooLow}</td><td class="num">${p.tooHigh}</td></tr>`).join('')}</table></details>
    </div>` : ''}
    <div class="card section"><div class="card-head"><h2>Misrouted prompts</h2><span class="muted small">${r.misroutes.length} of ${plural(r.total, 'prompt')} · embedded in ${num(r.embeddingMilliseconds, 0)} ms with ${h(r.embeddings)}</span></div>
      ${r.misroutes.length ? `<div class="table-wrap"><table><tr><th>Prompt</th><th>Expected</th><th>Routed</th><th>Best routes</th><th><span class="visually-hidden">Actions</span></th></tr>${misroutes}</table></div>` : '<div class="muted small">Every prompt was routed to its tier.</div>'}</div>`;
}

/* How often the judge's grades agree with the hand labels, and the weight that gives its labels in tuning. */
export function judgeCard() {
  const j = state.evaluation.judge;
  const enabled = ((state.config.Middleware || {}).Judge || {}).Enabled;
  if (!j || (!enabled && !j.pairs)) return '';
  const weight = (v) => Number(v).toFixed(2);
  const effect = !j.pairs ? `Judge labels count at ${weight(j.configuredWeight)} until ${j.minimumPairs} graded answers have a hand label.`
    : j.pairs < j.minimumPairs ? `Judge labels count at ${weight(j.configuredWeight)} <span class="faint" title="Tuning:JudgeWeight">(the judge weight)</span>; the agreement scales it from ${j.minimumPairs} pairs.`
    : `Judge labels count at <b>${weight(j.effectiveWeight)}</b>: <span title="Tuning:JudgeWeight">the judge weight</span> ${weight(j.configuredWeight)} × ${pct(j.rate)} agreement.`;
  return `<div class="card section"><div class="card-head"><h2>Judge agreement</h2><span class="muted small">Graded answers whose prompt is also in the evaluation set: a low grade agrees when the label is above the tier that answered, a good grade when it is at or below it.</span></div>
    <div class="small">${j.pairs ? `<b>${j.agreeing} of ${j.pairs}</b> agree (${pct(j.rate)}) · low grades confirmed ${j.lowAgreeing} of ${j.lowGrades} · good grades confirmed ${j.goodAgreeing} of ${j.goodGrades}` : 'No graded answer has a hand label yet. Label graded prompts on the <a href="#/activity">Activity</a> page (the tag button on a row).'}</div>
    <div class="small muted" style="margin-top:4px">${effect}</div>
    ${j.recent.length ? `<table style="margin-top:10px"><tr><th>Request</th><th>Model</th><th class="num">Grade</th><th class="num">Answered tier</th><th class="num">Label</th><th><span class="visually-hidden">Judge and label</span></th></tr>${j.recent.map((p) => `<tr><td>#${p.entryId}</td><td>${h(p.model || '—')}</td><td class="num">${p.grade}/5</td><td class="num">${p.answeredTier}</td><td class="num">${p.labelTier}</td><td>${p.agrees ? '<span class="badge ok">agrees</span>' : '<span class="badge warn">disagrees</span>'}</td></tr>`).join('')}</table>` : ''}
  </div>`;
}

export function healthCard() {
  const hr = state.evaluation.health;
  const kinds = { FewUtterances: ['info', 'few utterances'], DuplicateUtterances: ['warn', 'duplicate'], ConflictingUtterances: ['danger', 'conflict'], Overlap: ['warn', 'overlap'] };
  return `<div class="card section"><div class="card-head"><h2>Route health</h2><span class="muted small">Duplicates, utterances shared between routes, routes so close that ambiguity escalation fires constantly, thin routes.</span><span class="spacer"></span><button class="btn small" data-action="eval-health">${hr ? 'Check again' : 'Check routes'}</button></div>
    ${!hr ? '' : `${hr.findings.length ? `<ul class="findings">${hr.findings.map((f) => `<li><span class="badge ${kinds[f.kind][0]}">${kinds[f.kind][1]}</span> ${h(f.message)}</li>`).join('')}</ul>` : `<div class="small"><span class="badge ok">ok</span> No problems in ${plural(hr.utteranceCount, 'utterance')}.</div>`}
      ${hr.overlaps.length ? `<details style="margin-top:8px"><summary class="small muted">Closest route pairs</summary><table><tr><th>Routes</th><th class="num">Centroid similarity</th><th class="num">Ambiguous utterances</th></tr>${hr.overlaps.slice(0, 8).map((o) => `<tr><td>${h(o.route)} · ${h(o.otherRoute)}</td><td class="num">${num(o.centroidSimilarity, 3)}</td><td class="num">${pct(o.ambiguousFraction)}</td></tr>`).join('')}</table></details>` : ''}`}
  </div>`;
}

export function compareCard() {
  const f = state.evaluation.form; const cmp = state.evaluation.compare;
  const side = (s) => s.error ? `<span class="badge danger">failed</span> <span class="small">${h(s.error)}</span>` : `${pct(s.report.accuracy)} <span class="small muted">at the current threshold · ${pct(s.report.calibration.recommended.accuracy)} at ${s.report.calibration.recommended.threshold.toFixed(2)} · ${num(s.report.embeddingMilliseconds, 0)} ms</span>`;
  return `<div class="card section"><div class="card-head"><h2>Compare embedding models</h2><span class="muted small">Run the set with another embedding provider, each at its own best threshold. Nothing is changed.</span></div>
    <div class="form-grid">
      <label class="field"><span class="name">Provider</span><select data-state="evaluation.form.provider">${['OpenAICompatible', 'Hashing'].map((p) => `<option ${p === f.provider ? 'selected' : ''}>${p}</option>`).join('')}</select></label>
      ${f.provider === 'Hashing' ? `<label class="field"><span class="name">Dimensions</span><input type="number" min="16" data-state="evaluation.form.dimensions" data-kind="number" value="${f.dimensions}"></label>` : `
      <label class="field"><span class="name">Endpoint</span><input type="text" data-state="evaluation.form.endpoint" value="${h(f.endpoint)}" placeholder="http://localhost:11434"></label>
      <label class="field"><span class="name">Model</span><input type="text" data-state="evaluation.form.model" value="${h(f.model)}" placeholder="nomic-embed-text"></label>
      <label class="field"><span class="name">API key</span><input type="text" data-state="evaluation.form.apiKey" value="${h(f.apiKey)}" placeholder="optional, or \${env:…}"></label>`}
    </div>
    <div class="inline" style="margin-top:10px"><button class="btn small" data-action="eval-compare" ${adminOnly()}>Compare</button></div>
    ${cmp ? `<table style="margin-top:10px"><tr><th><span class="visually-hidden">Run</span></th><th>Embeddings</th><th>Accuracy</th></tr><tr><td>Current</td><td class="small">${h(cmp.current.embeddings)}</td><td>${side(cmp.current)}</td></tr><tr><td>Candidate</td><td class="small">${h(cmp.candidate.embeddings)}</td><td>${side(cmp.candidate)}</td></tr></table>${(cmp.warnings || []).map((w) => `<div class="small muted">${h(w)}</div>`).join('')}` : ''}
  </div>`;
}

/* Run needs prompts: the buttons follow the text as it is typed (data-needs-set), not only at the next render. */
const runGate = (empty, busy) => adminOnly(busy) || `data-needs-set ${empty ? 'disabled title="Add labelled prompts first"' : ''}`;
document.addEventListener('input', (ev) => {
  if (ev.target.id !== 'evaluation-text') return;
  const empty = !ev.target.value.trim();
  document.querySelectorAll('[data-needs-set]').forEach((b) => { b.disabled = empty; b.title = empty ? 'Add labelled prompts first' : ''; });
});

views.evaluate = () => {
  const ev = state.evaluation;
  if (ev.text == null) { loadEvaluationSet(); return '<div class="section-head"><h1>Evaluate routing</h1></div><div class="muted">Loading…</div>'; }
  const empty = !(ev.text || '').trim();
  return `
    <div class="section-head"><h1>Evaluate routing</h1><span class="muted">Run labelled prompts through the running configuration. No model is called, only the embedding provider.</span></div>
    <div class="card section"><div class="card-head"><h2>Evaluation set</h2><span class="muted small">${plural(ev.count, 'prompt')} · ${ev.location ? 'kept in <code class="break">' + h(ev.location) + '</code>' : 'kept in memory (lost on restart)'}</span><span class="spacer"></span>
        <a class="btn small" href="${API}/evaluation/set/export" download>Download</a></div>
      ${empty ? `<div class="empty-note small"><b>No labelled prompts yet.</b> Type them below, one JSON object per line (as in the grey example), then <b>Save set</b> and <b>Run</b>. Faster: on <a href="#/activity">Activity</a>, the tag button on a request labels it with the tier it should have had.</div>` : ''}
      <textarea id="evaluation-text" aria-label="Evaluation set (JSON Lines)" rows="8" spellcheck="false" data-state="evaluation.text" placeholder='{"prompt":"summarize this email","tier":1,"route":"rewrite-summarize"}'>${h(ev.text)}</textarea>
      <div class="hint small muted">One JSON object per line: <code>prompt</code>, the <code>tier</code> it should get, and optionally the <code>route</code> it should match and <code>localOnly</code>. Local-only prompts are left out of the download.</div>
      <div class="inline" style="margin-top:10px">
        <button class="btn primary small" data-action="eval-run" data-calibrate="1" ${runGate(empty, ev.busy)}>${ev.busy ? 'Running…' : 'Run and calibrate'}</button>
        <button class="btn small" data-action="eval-run" ${runGate(empty, ev.busy)}>Run</button>
        ${readOnly() ? '' : '<button class="btn small" data-action="eval-save">Save set</button>'}
        <span class="muted small">Threshold ${state.config.DefaultSimilarityThreshold} · ${plural((state.config.Routes || []).length, 'route')} · ${h((state.config.Embeddings || {}).Provider || '')}</span></div>
    </div>
    ${ev.report ? evaluationResults(ev.report) : ''}
    ${judgeCard()}
    ${healthCard()}
    ${compareCard()}`;
};
