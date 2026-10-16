/* Semantic Router dashboard: endpoints, where each connector sends requests and how it is doing (Overview card, top bar, Connectors page). */
import { fmtMs } from './charts.js';
import { h, num, readOnly, state } from './core.js';

const STATES = {
  ok: ['ok', 'ok'],
  degraded: ['warn', 'degraded'],
  failing: ['danger', 'failing'],
  down: ['danger', 'down'],
  idle: ['', 'idle'],
  unused: ['', 'unused'],
};
const CHECKS = { Ok: 'ok', Warning: 'warn', Failed: 'danger' };

const endpoints = () => (state.status && state.status.endpoints) || [];
const tone = (e) => (STATES[e.state] || [''])[0];
const at = (iso) => {
  const d = new Date(iso);
  if (isNaN(d)) return '';
  const time = d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
  return d.toDateString() === new Date().toDateString() ? time : d.toLocaleDateString(undefined, { weekday: 'short', day: 'numeric', month: 'short' }) + ' ' + time;
};
export const ago = (iso) => {
  const s = Math.max(0, (Date.now() - new Date(iso)) / 1000);
  if (isNaN(s)) return '';
  return s < 45 ? 'just now' : s < 3600 ? Math.round(s / 60) + ' min ago' : s < 86400 ? Math.round(s / 3600) + ' h ago' : Math.round(s / 86400) + ' d ago';
};

/* The endpoint the latest request went to. */
export function currentEndpoint() {
  return endpoints().filter((e) => e.lastUsed).sort((a, b) => new Date(b.lastUsed) - new Date(a.lastUsed))[0] || null;
}

export function stateBadge(e) {
  const [cls, label] = STATES[e.state] || ['', e.state];
  return `<span class="badge ${cls}" title="${h(e.stateNote || '')}">${h(label)}</span>`;
}

/* Where requests go: the host, or the program on this machine and whom it talks to. */
function where(e) {
  const place = (e.local ? ' <span class="badge info" title="Prompts stay on this machine">local</span>' : '') +
    (e.scheme === 'http' && !e.local ? ' <span class="badge warn" title="Requests leave this machine without TLS: prompts and keys can be read on the network">unencrypted</span>' : '');
  if (e.host) return `<span class="mono">${h(e.host)}</span>${place}`;
  if (e.program) return `<span class="mono break">${h(e.program)}</span>${place}<div class="faint small">runs on this machine${e.via ? ', sends to ' + h(e.via) : ''}</div>`;
  return '<span class="muted">no address set</span>';
}

/* What is behind the state: since when it fails, cooldowns, usage limits, the last error. */
function details(e) {
  const out = [];
  const cooling = e.modelsCoolingDown ? `cooling down until ${h(at(e.cooldownUntil))}` : '';
  if (e.state === 'down') out.push(cooling);
  else if (e.state === 'failing' && e.failingSince) out.push(`failing since ${h(at(e.failingSince))}`);
  else if (e.stateNote && e.state !== 'ok' && !(cooling && e.state === 'degraded')) out.push(h(e.stateNote));
  if (cooling && e.state !== 'down') out.push(`${e.modelsCoolingDown} of ${e.models} models ${cooling}`);
  if (e.limitDays) out.push(`usage limit hit on ${e.limitDays} day${e.limitDays === 1 ? '' : 's'} this week`);
  if (e.lastFailure) out.push(`last failure ${h(ago(e.lastFailure))}${e.lastError ? `: <span title="${h(e.lastError)}">${h(e.lastError.length > 90 ? e.lastError.slice(0, 89) + '…' : e.lastError)}</span>` : ''}`);
  return out.length ? `<div class="faint small">${out.join('<br>')}</div>` : '';
}

function check(e) {
  const busy = state.checking.has(e.connector);
  const c = e.check;
  const last = c ? `<div class="small"><span class="badge ${CHECKS[c.status] || ''}" title="${h(c.messages.join('\n'))}">${h(c.status === 'Ok' ? 'passed' : c.status === 'Warning' ? 'warning' : 'failed')}</span> <span class="faint" title="${h(at(c.at))}">${h(ago(c.at))} · ${h(fmtMs(c.milliseconds))}</span></div>` : '';
  return `${readOnly() ? '' : `<button class="btn small" data-action="endpoint-check" data-name="${h(e.connector)}" ${busy ? 'disabled' : ''} title="Ask it now, without calling a model: reachable and key accepted (HTTP), installed and signed in (CLI)">${busy ? 'Checking…' : 'Check'}</button>`}${last}`;
}

/* Overview: every endpoint, its state and figures. */
export function endpointsCard() {
  const list = endpoints();
  if (!list.length) return '';
  const now = currentEndpoint();
  return `<div class="card section" id="endpoints">
    <div class="card-head"><h2>Endpoints</h2><span class="muted small">Where each connector sends requests and how it did over the last ${state.status.activity.total} routed requests. Failed attempts that fell through to another model count against it.</span></div>
    <div class="table-wrap"><table class="wrap-head">
      <tr><th>Connector</th><th>Host</th><th>State</th><th class="num">Answered</th><th class="num" title="Time until the client starts seeing the reply">First byte p50 / p95</th><th class="num">Total p50 / p95</th><th class="num" title="Output tokens per second after the first byte, when the backend reports usage">Tokens/s</th><th>Last request</th><th>Check</th></tr>
      ${list.map((e) => `<tr>
        <td><b>${h(e.connector)}</b>${e === now ? ' <span class="badge accent" title="The latest request went here">in use</span>' : ''}<div class="faint small">${h(e.kind)} · ${e.models} model${e.models === 1 ? '' : 's'}</div></td>
        <td>${where(e)}</td>
        <td>${stateBadge(e)}${details(e)}</td>
        <td class="num">${e.successRate != null ? Math.round(e.successRate * 100) + '%' : '—'}${e.attempts ? `<div class="faint small">${e.attempts - e.failures} of ${e.attempts}</div>` : ''}</td>
        <td class="num nowrap">${e.firstByteP50 != null ? `${fmtMs(e.firstByteP50)} / ${fmtMs(e.firstByteP95)}` : '—'}</td>
        <td class="num nowrap">${e.totalP50 != null ? `${fmtMs(e.totalP50)} / ${fmtMs(e.totalP95)}` : '—'}</td>
        <td class="num">${e.tokensPerSecond != null ? num(e.tokensPerSecond, 0) : '—'}</td>
        <td class="small nowrap">${e.lastUsed ? `<span title="${h(at(e.lastUsed))}">${h(ago(e.lastUsed))}</span>` : '<span class="faint">never</span>'}</td>
        <td>${check(e)}</td>
      </tr>`).join('')}
    </table></div>
  </div>`;
}

/* Top bar: the endpoint in use, with its state and speed in the tooltip. */
export function endpointPill() {
  const e = currentEndpoint();
  if (!e) return '';
  const tip = [
    `${e.connector}: ${(STATES[e.state] || ['', e.state])[1]}${e.stateNote ? '. ' + e.stateNote : ''}`,
    e.successRate != null ? `${Math.round(e.successRate * 100)}% of ${e.attempts} requests answered` : '',
    e.firstByteP50 != null ? `first byte ${fmtMs(e.firstByteP50)} (p50), ${fmtMs(e.firstByteP95)} (p95)` : '',
    `last request ${ago(e.lastUsed)}`,
  ].filter(Boolean).join('\n');
  return `<a href="#/overview" class="endpoint-now" title="${h(tip)}" aria-label="${h('In use: ' + (e.host || e.program || e.connector) + ', ' + e.state)}"><span class="dot ${tone(e)}"></span> <span class="mono">${h(e.host || e.program || e.connector)}</span></a>`;
}

/* Connectors page: the state of a configured connector, as the running router sees it. */
export function connectorStateBadge(name) {
  const e = endpoints().find((x) => x.connector.toLowerCase() === String(name).toLowerCase());
  return e ? stateBadge(e) : '';
}
