/* Semantic Router dashboard: Feedback page: a bug, a misrouted prompt or an idea, opened as a pre-filled GitHub issue. */
import { tierOptions } from './config.js';
import { h, num, state, toast, views } from './core.js';
import { reasonLabel, stamp } from './labels.js';
import { isLocalModel } from './live.js';
import { pageActions } from './page-actions.js';
import { render } from './shell.js';

export const REPOSITORY = 'https://github.com/siebe41/SemanticRouter.NET';
export const SECURITY_REPORT = REPOSITORY + '/security/advisories/new';

// One per issue form in .github/ISSUE_TEMPLATE. The keys of fields() are the form's field ids: GitHub fills a form's
// text fields from query parameters named after them. Dropdowns and checkboxes cannot be filled, so they are asked there.
// auto: the fields filled from the router rather than typed here, shown before the issue is opened.
const KINDS = {
  misroute: { label: 'Routed to the wrong tier', template: 'misroute.yml', titlePrefix: '[Routing] ', auto: ['decision', 'answered-by', 'embeddings', 'version'] },
  bug: { label: 'Something does not work', template: 'bug_report.yml', titlePrefix: '', auto: ['activity', 'version', 'os'] },
  idea: { label: 'An idea or a change', template: 'feature_request.yml', titlePrefix: '', auto: [] },
};
const DIRECTIONS = { high: 'Too high', low: 'Too low', route: 'Wrong route' };

/* Opens the form for an Activity entry: a failed request as a bug, anything else as a misroute. */
export function reportEntry(e) {
  state.report = { ...blankReport(), kind: e.outcome === 'failed' ? 'bug' : 'misroute', entryId: e.id, entry: e };
  location.hash = '#/feedback';
}

export function blankReport() {
  return { kind: 'misroute', entryId: null, direction: 'high', expectedTier: null, title: '', text: '', prompt: '' };
}

// The entry Report was used on is kept, so the form still has it once newer requests push it out of the loaded activity.
const entryFor = (r) => (r.entryId == null || r.kind === 'idea' ? null : state.activity.find((e) => e.id === r.entryId) || (r.entry && r.entry.id === r.entryId ? r.entry : null));

/* What the router decided for one request, as the "decision line" the misroute form asks for. Never the prompt, the
   project, memory note titles or the error detail, which can carry private text. */
export function decisionLine(e) {
  const lines = [
    `Reason: ${e.reason}${e.cached ? ' (reused a recent decision)' : ''}`,
    `Tier: ${e.tier}`,
    `Route: ${e.route || '(none)'}${e.similarity != null ? ` · similarity ${num(e.similarity, 3)}` : ''}`,
    `Complexity: ${num(e.complexity, 3)}`,
  ];
  if (e.callout) lines.push(`Callout: ${e.callout}`);
  if (e.profile) lines.push(`Profile: ${e.profile}`);
  if (e.selection) lines.push(`Selection: ${e.selection}`);
  if (e.sticky) lines.push('Sticky: stayed on the conversation\'s model');
  if (e.learned != null) lines.push(`Learned change: #${e.learned}`);
  if (e.unreliable) lines.push(`Left out as unreliable: ${e.unreliable}`);
  lines.push(`Planned model: ${e.plannedModel || '(none)'}`);
  lines.push(`Served by: ${e.servedBy || '(none)'}${e.attempt > 1 ? ` · attempt ${e.attempt}` : ''}${e.degraded ? ' · degraded' : ''} · ${e.outcome}`);
  lines.push(`Request: ${[e.protocol, e.client && `client ${e.client}`, e.requestedModel && `model ${e.requestedModel}`, e.hasTools && 'tools', e.toolContinuation && 'tool result', e.localOnly && 'local only', `~${e.estimatedInputTokens} tokens`].filter(Boolean).join(' · ')}`);
  const feedback = (state.feedback || {})[e.id] || [];
  if (feedback.length) lines.push(`Feedback: ${feedback.join(', ')}`);
  if (e.grade != null) lines.push(`Judge grade: ${e.grade}/5`);
  lines.push(`Activity entry: #${e.id} at ${e.timestamp}`);
  return lines.join('\n');
}

/* Embedding provider, model and the threshold that applied to the entry's route. */
function embeddingsLine(e) {
  const em = state.status.embeddings || {};
  const route = e && e.route ? (state.config.Routes || []).find((r) => r.Name === e.route) : null;
  const threshold = route && route.SimilarityThreshold != null ? `${route.SimilarityThreshold} (route ${route.Name})` : state.config.DefaultSimilarityThreshold;
  return [em.provider, em.model, threshold != null ? `threshold ${threshold}` : ''].filter(Boolean).join(', ');
}

const versionLine = () => state.status.libraryVersion || '';
const osLine = () => { const sv = state.status.service || {}; return [sv.operatingSystem, sv.runtime].filter(Boolean).join(', '); };

/* A local-only prompt answered outside this machine is a security problem: it goes to private reporting, not an issue. */
const leakedLocal = (e) => !!(e && e.localOnly && e.servedBy && !isLocalModel(e.servedBy));

function defaultTitle(r, e) {
  if (r.kind !== 'misroute' || !e) return '';
  return `${DIRECTIONS[r.direction] || ''}: tier ${e.tier}${e.route ? ' via ' + e.route : ''} → ${e.servedBy || e.plannedModel || 'no model'}`;
}

/* The form's field ids and the values GitHub fills them with. Empty values are left out. */
export function issueFields(r) {
  const e = entryFor(r);
  const summary = r.title.trim() || defaultTitle(r, e);
  const title = summary ? KINDS[r.kind].titlePrefix + summary : '';
  if (r.kind === 'misroute') {
    // The tier picker starts on the routed tier; it says something only once moved off it.
    const t = r.expectedTier != null && (!e || r.expectedTier !== e.tier) ? (state.status.tiers || []).find((x) => x.level === r.expectedTier) : null;
    const tier = t ? `Tier ${t.level}${t.name ? ` (${t.name})` : ''}.` : '';
    return {
      title,
      decision: e ? decisionLine(e) : '',
      'answered-by': e ? e.servedBy || '' : '',
      expected: [tier, r.text.trim()].filter(Boolean).join(' '),
      embeddings: embeddingsLine(e),
      prompt: r.prompt.trim(),
      version: versionLine(),
    };
  }
  if (r.kind === 'bug') {
    return {
      title,
      'what-happened': r.text.trim(),
      activity: e ? decisionLine(e) : '',
      version: versionLine(),
      os: osLine(),
    };
  }
  return { title, problem: r.text.trim() };
}

export function issueUrl(r) {
  const query = new URLSearchParams({ template: KINDS[r.kind].template });
  Object.entries(issueFields(r)).forEach(([k, v]) => { if (v) query.set(k, v); });
  return `${REPOSITORY}/issues/new?${query}`;
}

export function openIssue() {
  const r = state.report;
  if (r.kind === 'misroute' && !entryFor(r)) { toast('Pick the request that was misrouted.', 'danger'); return; }
  if (leakedLocal(entryFor(r))) { toast('Report this one privately: see the note above.', 'danger'); return; }
  window.open(issueUrl(r), '_blank', 'noopener');
}

export function usePreview() {
  const e = entryFor(state.report);
  if (!e || !e.promptPreview) return;
  state.report.prompt = e.promptPreview;
  render();
}

const entryLabel = (e) => `#${e.id} · ${stamp(e.timestamp)} · T${e.tier} ${reasonLabel(e.reason)}${e.route ? ' ' + e.route : ''} → ${e.servedBy || e.plannedModel || 'no model'}`;

views.feedback = () => {
  if (!state.report) state.report = blankReport();
  const r = state.report;
  const e = entryFor(r);
  const entries = state.activity.filter((x) => !x.toolContinuation).slice(0, 50);
  if (e && !entries.includes(e)) entries.unshift(e);
  const fields = issueFields(r);
  const filled = KINDS[r.kind].auto.filter((k) => fields[k]).map((k) => [k, fields[k]]);
  const textLabel = { misroute: 'What the prompt really needed, and why', bug: 'What happened', idea: 'What you are trying to do, and what gets in the way' }[r.kind];
  const placeholder = { misroute: 'e.g. It is a one-line rename; the local coder model answers it fine.', bug: 'What you did, what you expected, and what happened instead.', idea: '' }[r.kind];

  return `
    <div class="section-head"><h1>Feedback</h1><span class="muted">Opens a pre-filled issue on GitHub, where you read it over and submit it. Nothing is sent from the router.</span></div>
    <div class="card section" id="feedback-form">
      <div class="form-grid">
        <label class="field"><span class="name">About</span><select data-state="report.kind">${Object.entries(KINDS).map(([k, v]) => `<option value="${k}" ${k === r.kind ? 'selected' : ''}>${h(v.label)}</option>`).join('')}</select></label>
        ${r.kind === 'misroute' ? `<label class="field"><span class="name">What went wrong</span><select data-state="report.direction">${Object.entries(DIRECTIONS).map(([k, v]) => `<option value="${k}" ${k === r.direction ? 'selected' : ''}>${v}</option>`).join('')}</select></label>
          <label class="field"><span class="name">Should have been tier</span><select data-state="report.expectedTier" data-kind="number">${tierOptions(r.expectedTier != null ? r.expectedTier : e ? e.tier : 1)}</select></label>` : ''}
        ${r.kind !== 'idea' ? `<label class="field wide"><span class="name">Request${r.kind === 'bug' ? ' (optional)' : ''}</span><select data-state="report.entryId" data-kind="number" data-nullable="1">
          <option value="" ${e ? '' : 'selected'}>${r.kind === 'bug' ? '(not about one request)' : 'Pick it from Activity…'}</option>
          ${entries.map((x) => `<option value="${x.id}" ${x === e ? 'selected' : ''}>${h(entryLabel(x))}</option>`).join('')}</select>
          <span class="hint">Or use <b>Report</b> next to the entry on <a href="#/activity">Activity</a>.</span></label>` : ''}
        <label class="field wide"><span class="name">Title</span><input type="text" data-state="report.title" value="${h(r.title)}" placeholder="${h(defaultTitle(r, e) || 'A short summary')}"></label>
        <label class="field wide"><span class="name">${textLabel}</span><textarea rows="3" data-state="report.text" placeholder="${h(placeholder)}">${h(r.text)}</textarea></label>
        ${r.kind === 'misroute' ? `<label class="field wide"><span class="name">Prompt or paraphrase (optional)</span><textarea rows="2" data-state="report.prompt" placeholder="Left out unless you add it. A paraphrase that keeps the length and shape is enough.">${h(r.prompt)}</textarea>
</label>
          <div class="wide small faint">Issues are public. Never paste keys, file contents or private text.${e && e.promptPreview ? ` <button class="btn ghost small" type="button" data-action="report-preview">Use the stored preview</button>` : ''}</div>` : ''}
      </div>
      ${leakedLocal(e) ? `<div class="card" role="alert" style="margin-top:12px;border-color:var(--danger)">This prompt was marked local-only but answered by <b>${h(e.servedBy)}</b>, which is not a local model. That is a security problem: <a href="${SECURITY_REPORT}" target="_blank" rel="noopener">report it privately</a> instead of in a public issue.</div>` : ''}
      ${filled.length ? `<h3 style="margin-top:14px">Filled in for you</h3>
        <table class="small">${filled.map(([k, v]) => `<tr><th class="nowrap">${h(k)}</th><td><pre class="mono" style="margin:0;white-space:pre-wrap">${h(v)}</pre></td></tr>`).join('')}</table>
        <p class="faint small">${r.kind === 'misroute' ? 'No prompt text, project or memory notes. ' : ''}GitHub asks the rest (how you run it, the client) on the form.</p>` : ''}
      <div class="inline" style="margin-top:12px">
        <button class="btn primary" type="button" data-action="report-open">Open the issue on GitHub</button>
        ${e ? '<button class="btn small" type="button" data-action="report-copy">Copy the decision line</button>' : ''}
        <button class="btn ghost small" type="button" data-action="report-reset">Clear</button>
      </div>
    </div>
    <p class="faint small">Security problems go through <a href="${SECURITY_REPORT}" target="_blank" rel="noopener">private reporting</a>, never a public issue. All issues: <a href="${REPOSITORY}/issues" target="_blank" rel="noopener">github.com/siebe41/SemanticRouter.NET/issues</a>.</p>`;
};

// The dispatcher in app.js reads every page's handlers through this name (see page-actions.js).
export const reportActions = Object.assign(pageActions, {
  'report-entry'(el) { const e = state.activity.find((x) => String(x.id) === el.dataset.id); if (e) reportEntry(e); },
  'report-open'() { openIssue(); },
  'report-preview'() { usePreview(); },
  'report-copy'() { const e = entryFor(state.report); if (e) navigator.clipboard.writeText(decisionLine(e)).then(() => toast('Copied.', 'ok')); },
  'report-reset'() { state.report = blankReport(); render(); },
});
