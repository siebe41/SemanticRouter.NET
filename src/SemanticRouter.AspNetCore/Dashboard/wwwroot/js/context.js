/* Semantic Router dashboard: context cost. Where the input tokens of requests go (system prompt, tools, injected blocks,
   history, tool results, images, the new ask), what that context cost, and what to trim. Used by the Usage page and the
   Activity page's request detail. */
import { api, h, state } from './core.js';
import { pct } from './evaluate.js';
import { count, plural, usd } from './labels.js';
import { pageActions } from './page-actions.js';
import { render } from './shell.js';

/* The parts of a request's input, in the order they are listed when sizes tie. */
export const PARTS = [
  ['system', 'System prompt', 'systemTokens'],
  ['tools', 'Tool definitions', 'toolDefinitionTokens'],
  ['injected', 'Injected blocks', 'injectedTokens'],
  ['history', 'History', 'historyTokens'],
  ['toolResults', 'Tool results', 'toolResultTokens'],
  ['images', 'Images', 'imageTokens'],
  ['prompt', 'New ask', 'promptTokens'],
];
const TITLES = {
  'fixed-overhead': 'large fixed overhead',
  'large-system-prompt': 'large system prompt',
  'large-tool-definitions': 'many tools',
  'injected-context': 'injected context',
  'large-tool-result': 'large tool result',
  'repeated-content': 'repeated content',
  'long-history': 'long history',
  'near-context-window': 'near the context window',
  uncached: 'no cache hits',
};
export const WARNING_FINDINGS = ['fixed-overhead', 'near-context-window', 'uncached'];

/* "12.4k" for a quick read; the exact count is in the tooltip. */
const short = (n) => (n >= 10000 ? `${Math.round(n / 1000)}k` : n >= 1000 ? `${(n / 1000).toFixed(1)}k` : String(n || 0));

export const findingBadge = (code) => `<span class="badge ${WARNING_FINDINGS.includes(code) ? 'warn' : 'info'}">${h(TITLES[code] || code)}</span>`;

/* One bar per part, largest first; share of the request (or period) on the right. */
function bars(rows, total) {
  const max = Math.max(1, ...rows.map((r) => r.tokens));
  return `<div class="bars">${rows.filter((r) => r.tokens > 0).sort((a, b) => b.tokens - a.tokens).map((r) => `<div class="bar-row context-row" title="${h(`${r.label}: ${count(r.tokens)} tokens${r.extra ? ' · ' + r.extra : ''}`)}">
    <span>${h(r.label)}</span><div class="bar"><i style="width:${Math.round((r.tokens / max) * 100)}%"></i></div><span class="num">${total > 0 ? pct(r.tokens / total) : '–'}</span></div>`).join('')}</div>`;
}

/* The Activity page's request detail: where this request's input went, and its findings. */
export function contextDetail(c) {
  if (!c || !c.totalTokens) return '';
  const parts = PARTS.map(([, label, key]) => [label, c[key]]).filter(([, n]) => n > 0).sort((a, b) => b[1] - a[1])
    .map(([label, n]) => `<span title="${h(`${count(n)} tokens`)}">${h(label.toLowerCase())} ${short(n)}</span>`).join(' · ');
  const extra = [c.toolCount ? plural(c.toolCount, 'tool') + (c.unusedToolDefinitionTokens ? `, ${short(c.unusedToolDefinitionTokens)} never called` : '') : '', c.repeatedTokens ? `${short(c.repeatedTokens)} repeated` : '',
    c.inputCostUsd ? `input ${usd(c.inputCostUsd, true)}` : c.inputApiValueUsd ? `input ${usd(c.inputApiValueUsd, true)} at API prices` : ''].filter(Boolean).join(' · ');
  return `<span class="small">~${short(c.totalTokens)}: ${parts}</span>${extra ? `<div class="faint small">${extra}</div>` : ''}${(c.findings || []).length ? `<div>${c.findings.map(findingBadge).join(' ')}</div>` : ''}`;
}

/* The Usage page's card: the period's input by part, its cost, the findings and the largest requests, and the analyzer. */
export function contextCard(s) {
  const intro = 'Every request sends its whole context again: the system prompt, the tools and the history. This shows where the input tokens went and what made them large.';
  const analyzer = analyzeForm();
  if (!s || !s.requests) {
    return `<div class="card section"><div class="card-head"><h2>Context</h2><span class="muted small">${h(intro)}</span></div>
      <div class="muted small">${s ? 'No analyzed requests in this window yet.' : 'Context analysis is not available.'}</div>${analyzer}</div>`;
  }
  const byCost = s.costUsd > 0; const byValue = !byCost && s.apiValueUsd > 0;
  const money = (p) => (byCost ? usd(p.costUsd) : byValue ? usd(p.apiValueUsd) : '–');
  const parts = s.components.filter((p) => p.tokens > 0).sort((a, b) => b.tokens - a.tokens);
  const findings = s.findings.map((f) => `<tr><td>${findingBadge(f.code)}<div class="small muted">${h(f.advice)}</div></td>
      <td class="num">${count(f.requests)}</td><td class="num">${pct(f.share)}</td><td class="num">${f.costUsd ? usd(f.costUsd) : '–'}</td>
      <td class="small"><a href="#/activity" data-action="context-example" data-id="${f.example}">#${f.example}</a></td></tr>`).join('');
  const heaviest = s.heaviest.map((r) => `<tr><td class="small"><a href="#/activity" data-action="context-example" data-id="${r.id}">#${r.id}</a></td><td class="small">${h(r.model || '')}</td>
      <td class="prompt-cell small">${r.promptPreview ? h(r.promptPreview) : '<span class="faint">prompt not stored</span>'}</td><td>${contextDetail(r.context)}</td></tr>`).join('');
  return `<div class="card section" id="context-card"><div class="card-head"><h2>Context</h2><span class="muted small">${h(intro)}</span></div>
    <div class="grid cols-2">
      <div><div class="small muted" style="margin-bottom:6px">${plural(s.requests, 'request')} · ~${short(Math.round(s.totalTokens / s.requests))} input tokens each${byCost ? ` · input cost ${usd(s.costUsd)}` : byValue ? ` · input ${usd(s.apiValueUsd)} at API prices` : ''}</div>
        ${bars(parts.map((p) => ({ label: p.label, tokens: p.tokens, extra: `~${count(p.averageTokens)} per request${byCost || byValue ? ' · ' + money(p) : ''}` })), s.totalTokens)}
        ${s.unusedToolTokens || s.repeatedTokens ? `<div class="small faint" style="margin-top:8px">${[s.unusedToolTokens ? `${short(s.unusedToolTokens)} tokens of tools that were never called` : '', s.repeatedTokens ? `${short(s.repeatedTokens)} tokens of content sent twice in the same request` : ''].filter(Boolean).join(' · ')}</div>` : ''}</div>
      <div class="table-wrap"><table class="wrap-head"><tr><th>Part</th><th class="num">Per request</th><th class="num">Share</th><th class="num" title="Each request's input cost split by the size of its parts">${byValue ? 'At API prices' : 'Cost'}</th></tr>
        ${parts.map((p) => `<tr><td>${h(p.label)}</td><td class="num">${count(p.averageTokens)}</td><td class="num">${pct(p.share)}</td><td class="num">${money(p)}</td></tr>`).join('')}</table></div>
    </div>
    ${findings ? `<h3 style="margin-top:14px">What made the context large</h3><div class="table-wrap"><table class="wrap-head"><tr><th>Finding and what to do</th><th class="num">Requests</th><th class="num">Share</th><th class="num" title="The input cost of the requests it came up in">Their input cost</th><th>Latest</th></tr>${findings}</table></div>`
      : '<div class="small muted" style="margin-top:10px">No request in this window crossed a threshold (Usage:Context).</div>'}
    ${heaviest ? `<details style="margin-top:12px"><summary class="small">The largest requests</summary><div class="table-wrap"><table class="wrap-head"><tr><th>Request</th><th>Model</th><th>Prompt</th><th>Context</th></tr>${heaviest}</table></div></details>` : ''}
    ${analyzer}</div>`;
}

/* Paste a request body (from a client's log or a proxy) to see its parts, what to trim, and what each model would charge. */
function analyzeForm() {
  const u = state.usage; const r = u.contextReport;
  return `<details style="margin-top:12px" ${u.contextText || r ? 'open' : ''}><summary class="small">Analyze a request</summary>
    <div class="small muted" style="margin:6px 0">Paste a request body in any protocol (chat completions, Messages or Responses). It is analyzed here and sent to no model.</div>
    <textarea rows="5" class="mono" aria-label="Request body (JSON)" data-state="usage.contextText" placeholder='{"model": "…", "system": "…", "tools": […], "messages": […]}'>${h(u.contextText || '')}</textarea>
    <div class="inline" style="margin-top:8px"><button class="btn small" data-action="context-analyze" ${u.contextBusy ? 'disabled' : ''}>${u.contextBusy ? 'Analyzing…' : 'Analyze'}</button>${r ? '<button class="btn ghost small" data-action="context-clear">Clear</button>' : ''}</div>
    ${r ? reportView(r) : ''}</details>`;
}

function reportView({ protocol, report }) {
  const b = report.breakdown;
  const items = (list, what) => (list.length ? `<div class="small"><b>${what}:</b> ${list.map((t) => `${h(t.name)} <span class="faint">${short(t.tokens)}</span>`).join(', ')}</div>` : '');
  const models = report.models.map((m) => `<tr><td>${h(m.model)}${m.isLocal ? ' <span class="badge">local</span>' : ''}</td>
      <td class="num">${m.inputCostUsd ? usd(m.inputCostUsd, true) : m.apiValueUsd ? `<span title="At API prices: included in the subscription">${usd(m.apiValueUsd, true)}*</span>` : '$0'}</td>
      <td class="num">${m.cachedInputCostUsd != null ? usd(m.cachedInputCostUsd, true) : '–'}</td>
      <td class="num">${m.windowShare != null ? `<span class="${m.windowShare >= 0.8 ? 'warn-text' : ''}">${m.windowShare > 1 ? 'does not fit' : pct(m.windowShare)}</span> of ${short(m.contextWindow)}` : '–'}</td></tr>`).join('');
  return `<div class="grid cols-2" style="margin-top:12px">
    <div><div class="small muted" style="margin-bottom:6px">${h(protocol)} request · ~${count(b.totalTokens)} input tokens · ${plural(b.turns, 'turn')}${b.toolCount ? ' · ' + plural(b.toolCount, 'tool') : ''}</div>
      ${bars(PARTS.map(([, label, key]) => ({ label, tokens: b[key] })), b.totalTokens)}
      <div style="margin-top:8px">${items(report.largestTools.slice(0, 5), 'Largest tools')}${items(report.unusedTools.slice(0, 8), 'Never called')}${items(report.largestToolResults.slice(0, 5), 'Largest tool results')}</div></div>
    <div>${report.findings.length ? report.findings.map((f) => `<div class="small" style="margin-bottom:8px">${findingBadge(f.code)} ${h(f.detail)}<div class="muted">${h(f.advice)}</div></div>`).join('') : '<div class="small muted">Nothing crosses a threshold: this context is lean.</div>'}</div>
  </div>
  ${models ? `<div class="table-wrap" style="margin-top:10px"><table class="wrap-head"><tr><th>Model</th><th class="num" title="The input at the full price, paid again on every request that sends this context">Input per request</th><th class="num" title="With everything before the latest turn read from the prompt cache">With caching</th><th class="num">Context window</th></tr>${models}</table></div>
    ${report.models.some((m) => !m.inputCostUsd && m.apiValueUsd) ? '<div class="small faint">* a subscription: what the input would cost at its API prices.</div>' : ''}` : ''}`;
}

Object.assign(pageActions, {
  async 'context-analyze'() {
    const u = state.usage;
    let body;
    try { body = JSON.parse(u.contextText || ''); } catch (e) { throw new Error(`That is not JSON: ${e.message}`); }
    u.contextBusy = true; render();
    try { u.contextReport = await api('POST', '/context/analyze', body); } finally { u.contextBusy = false; render(); }
  },
  'context-clear'() { state.usage.contextText = ''; state.usage.contextReport = null; render(); },
  // Opens the request on the Activity page, when it is still among the recent requests there.
  'context-example'(el) { state.activityDetail = +el.dataset.id; location.hash = '#/activity'; },
});
