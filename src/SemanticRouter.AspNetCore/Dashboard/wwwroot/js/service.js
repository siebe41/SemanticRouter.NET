/* Semantic Router dashboard: the router itself, where it runs, the addresses clients point at and how it is doing (Overview card). */
import { h, state } from './core.js';
import { ago } from './endpoints.js';

const STATES = {
  ok: ['ok', 'running'],
  starting: ['info', 'starting'],
  degraded: ['warn', 'degraded'],
  failing: ['danger', 'failing'],
};

/* How long since iso: "3 d 4 h", "2 h 5 min", "12 min". */
function uptime(iso) {
  const m = Math.max(0, Math.floor((Date.now() - new Date(iso)) / 60000));
  if (isNaN(m)) return '';
  const d = Math.floor(m / 1440);
  const hr = Math.floor((m % 1440) / 60);
  return d ? `${d} d ${hr} h` : hr ? `${hr} h ${m % 60} min` : `${m} min`;
}

const copyable = (text) =>
  `<span class="mono">${h(text)}</span> <button class="btn ghost small" data-action="copy-text" data-text="${h(text)}" aria-label="Copy ${h(text)}">Copy</button>`;

/* The base addresses: those the server reports, or the one this page was loaded from. */
export function addresses(sv) {
  const list = sv.addresses || [];
  return list.length ? list : [{ url: location.origin, local: ['localhost', '127.0.0.1', '[::1]'].includes(location.hostname), encrypted: true }];
}

/* Who else can use the gateway, and what they need. */
function reach(sv) {
  if (!sv.remote) {
    const port = (sv.listening.map((u) => (u.match(/:(\d+)\/?$/) || [])[1]).find(Boolean)) || location.port || '5080';
    return `<span class="badge">this machine only</span> <span class="faint small">It listens on loopback. Run it with <span class="mono">--urls http://0.0.0.0:${h(port)}</span> to serve other machines.</span>`;
  }
  const cli = (state.status.endpoints || []).some((e) => e.program && e.via) ? ' Subscription CLI connectors still answer only this machine.' : '';
  if (!sv.gatewayKeyRequired) {
    return `<span class="badge warn">open to your network</span> <span class="faint small">Anything that can reach this machine can use your models (unauthenticated remote clients are allowed). Add gateway keys under <a href="#/gateway">Gateway</a>.${cli}</span>`;
  }
  return sv.gatewayKeysConfigured
    ? `<span class="badge ok">gateway key required</span> <span class="faint small">Clients on other machines send one (<span class="mono">Authorization: Bearer</span> or <span class="mono">x-api-key</span>).${cli}</span>`
    : `<span class="badge warn">other machines refused</span> <span class="faint small">Clients on other machines get 401 until you add gateway keys under <a href="#/gateway">Gateway</a>.${cli}</span>`;
}

/* Overview: the base URLs to give clients, who else can use the gateway, and the router's health. With quiet (the
   setup card is showing), "no model is enabled" is left to that card instead of repeated here. */
export function serviceCard(quiet) {
  const s = state.status;
  const sv = s.service;
  if (!sv) return '';
  const noModel = (text) => quiet && /no (model is enabled|enabled model)/i.test(text || '');
  const [cls, label] = STATES[sv.state] || ['', sv.state];
  const note = noModel(sv.stateNote) ? '' : sv.stateNote;
  const problems = sv.problems.filter((p) => !noModel(p));
  const anthropic = s.gateway.anthropicPathPrefixes[0] || '/v1/messages';
  const when = (iso) => `<span title="${h(new Date(iso).toLocaleString())}">${h(ago(iso))}</span>`;
  const last = sv.lastRequest
    ? when(sv.lastRequest) + (sv.lastOutcome && sv.lastOutcome !== 'served' ? ` <span class="badge ${sv.lastOutcome === 'failed' ? 'danger' : 'warn'}">${h(sv.lastOutcome)}</span>` : '')
    : '<span class="faint">none yet</span>';
  const embeddings = s.warmedUp ? '<span class="badge ok">index ready</span>' : (s.warmUpError ? `<span class="badge danger" title="${h(s.warmUpError)}">warm-up failed</span>` : '<span class="badge info">warming up</span>');
  const connectors = (s.endpoints || []).filter((e) => e.state !== 'unused');
  const answering = connectors.filter((e) => !['failing', 'down'].includes(e.state)).length;

  return `<div class="card section" id="service">
    <div class="card-head"><h2>Service</h2><span class="badge ${cls}">${h(label)}</span>${note ? `<span class="muted small">${h(note)}</span>` : ''}</div>
    <h3>Point clients at</h3>
    <div class="table-wrap"><table class="addresses">
      <tr><th>From</th><th>OpenAI-compatible base URL</th><th>Anthropic base URL</th></tr>
      ${addresses(sv).map((a) => `<tr>
        <td>${a.local ? 'this machine' : 'other machines'}${a.encrypted ? '' : ' <span class="badge warn" title="Prompts cross the network without TLS">unencrypted</span>'}</td>
        <td data-label="OpenAI-compatible">${copyable(a.url + '/v1')}</td>
        <td data-label="Anthropic">${copyable(a.url)}</td>
      </tr>`).join('')}
    </table></div>
    <div class="small" style="margin-top:8px">${reach(sv)}</div>
    <div class="faint small" style="margin-top:4px">Use model <span class="mono">auto</span> and any API key. Anthropic clients post to <span class="mono">${h(anthropic)}</span> under the base URL. Ready-made snippets: <a href="#/clients">Connect clients</a>.</div>
    <h3 style="margin-top:16px">Health</h3>
    <div class="table-wrap plain"><table class="kv">
      <tr><th>State</th><td><span class="badge ${cls}">${h(label)}</span>${note ? ` <span class="small muted">${h(note)}</span>` : ''}</td></tr>
      <tr><th>Connectors</th><td>${connectors.length ? `${answering} of ${connectors.length} answering <span class="faint small">(see Connectors below)</span>` : '<span class="muted">none in use</span>'}</td></tr>
      <tr><th>Embeddings</th><td>${embeddings}</td></tr>
      <tr><th>Last request</th><td>${last}</td></tr>
      <tr><th>Last answered</th><td>${sv.lastServed ? when(sv.lastServed) : '<span class="faint">none yet</span>'}</td></tr>
      ${problems.length ? `<tr><th>Needs a look</th><td><ul class="small" style="margin:0;padding-left:18px">${problems.map((p) => `<li>${h(p)}</li>`).join('')}</ul></td></tr>` : ''}
    </table></div>
  </div>`;
}

/* Where the router runs: machine, process, program, version, addresses and configuration file (About this router). */
export function runsOn() {
  const s = state.status;
  const sv = s.service;
  if (!sv) return '<p class="muted small">This router does not report where it runs.</p>';
  return `<table class="kv">
      <tr><th>Machine</th><td><b>${h(sv.machine)}</b> <span class="faint small">${h(sv.operatingSystem)}</span></td></tr>
      <tr><th>Process</th><td>pid ${h(sv.processId)} · up ${h(uptime(sv.startedAt))} <span class="faint small">since ${h(new Date(sv.startedAt).toLocaleString(undefined, { weekday: 'short', hour: '2-digit', minute: '2-digit' }))}</span></td></tr>
      ${sv.program ? `<tr><th>Program</th><td class="mono small break">${h(sv.program)}</td></tr>` : ''}
      <tr><th>Version</th><td>${h(s.libraryVersion)} <span class="faint small">${h(sv.runtime)}</span></td></tr>
      ${sv.listening.length ? `<tr><th>Listening on</th><td class="mono small break">${sv.listening.map(h).join(', ')}</td></tr>` : ''}
      ${s.store ? `<tr><th>Configuration</th><td class="mono small break">${h(s.store)}</td></tr>` : ''}
    </table>`;
}
