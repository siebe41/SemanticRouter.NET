/* Semantic Router dashboard: the router itself, where it runs, the addresses clients point at and how it is doing
   (Overview card), and the Service page (Admin): who can connect, restart and stop, and the background service. */
import { API, api, ask, h, PREFIX, readOnly, state, toast, views } from './core.js';
import { ago } from './endpoints.js';
import { refreshStatus, render } from './shell.js';

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
  return list.length ? list : [{ url: location.origin, local: ['localhost', '127.0.0.1', '[::1]'].includes(location.hostname), encrypted: location.protocol === 'https:' }];
}

/* Who else can use the gateway, and what they need. */
function reach(sv) {
  if (!sv.remote) {
    const port = (sv.listening.map((u) => (u.match(/:(\d+)\/?$/) || [])[1]).find(Boolean)) || location.port || '5080';
    return `<span class="badge">this machine only</span> <span class="faint small">It listens on loopback. To serve other machines, choose so under <a href="#/service">Admin → Service</a>, or run it with <span class="mono">--urls http://0.0.0.0:${h(port)}</span>.</span>`;
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

/* ---------------------------------------------------------------------------------------------
   Service page (Admin): GET api/service, PUT api/service/listen, POST api/service/{action}
   --------------------------------------------------------------------------------------------- */

const LOCAL_HOSTS = ['localhost', '127.0.0.1', '[::1]'];
const browsingRemotely = () => !LOCAL_HOSTS.includes(location.hostname);

/* GET api/service into state.hostService; the form keeps what is being typed. */
export async function loadService() {
  const hs = state.hostService;
  if (hs.loading) return;
  hs.loading = true;
  try {
    hs.data = await api('GET', '/service');
    hs.error = null;
    const listen = hs.data.service && hs.data.service.listen;
    if (listen && hs.scope == null) {
      // The form edits the saved setting, which differs from the next start's addresses while --urls decides.
      const saved = listen.saved && parseUrls(listen.saved);
      hs.scope = (saved || listen).scope === 'network' ? 'network' : 'local';
      hs.port = (saved || listen).port;
    }
  } catch (e) {
    hs.data = null;
    hs.error = e.status === 403 ? 'Only the admin role can see and change how the router runs.' : e.message;
  }
  hs.loading = false;
  if (state.view === 'service') render();
}

/* The scope and port of a saved urls value, as the router describes its own (HostListen.Describe). */
function parseUrls(urls) {
  const hosts = urls.split(';').map((u) => u.trim()).filter(Boolean).map((u) => (u.match(/^\w+:\/\/(\[[^\]]*\]|[^:/]+)(?::(\d+))?/) || []));
  const wildcard = (x) => ['0.0.0.0', '*', '+', '[::]'].includes(x);
  const port = +((hosts[0] || [])[2] || 0);
  if (!hosts.length || !port) return null;
  return { scope: hosts.every((m) => wildcard(m[1])) ? 'network' : 'local', port };
}

const RUNS_AS = {
  service: 'the background service',
  container: 'a container',
  foreground: 'a program started from a terminal, or by another program',
};

/* How to start the router again once it is stopped. */
function startAgain(sv) {
  if (sv.runsAs === 'service') return 'It starts again when you next sign in, or with <span class="mono">semantic-router service start</span>.';
  if (sv.runsAs === 'container') return 'The container runtime decides whether it starts again.';
  return 'Start it again the way you started it, for example <span class="mono">semantic-router</span> in a terminal.';
}

const sleep = (ms) => new Promise((resolve) => { setTimeout(resolve, ms); });

/*
 * After an action: wait until a router answers with another instance id (it restarted, or the service's router took
 * over). With another port, the new address is probed instead (no-cors: a reply of any kind means it listens) and the
 * page moves there.
 */
async function waitForRouter(before, origin) {
  const until = Date.now() + 60000;
  while (Date.now() < until) {
    await sleep(1000);
    try {
      if (origin) {
        await fetch(origin + PREFIX + '/api/session', { mode: 'no-cors', cache: 'no-store' });
        return { moved: origin };
      }
      const res = await fetch(API + '/status', { cache: 'no-store' });
      if (res.status === 401) return { status: null };
      if (res.ok) { const status = await res.json(); if (status.instance !== before) return { status }; }
    } catch { /* not listening yet */ }
  }
  return null;
}

/* Where the dashboard will be after a restart, when that is not this page's address (another port). */
function nextOrigin(listen) {
  if (!listen.restartNeeded || listen.scope === 'custom') return null;
  const port = String(listen.port);
  const current = location.port || (location.protocol === 'https:' ? '443' : '80');
  return port === current || location.protocol !== 'http:' ? null : `http://${location.hostname}:${port}`;
}

export const serviceActions = {
  'service-load'() { return loadService(); },

  async 'service-save'() { await saveListen(); },

  async 'service-save-restart'(el) {
    if (!await saveListen()) return;
    await runAction('restart', el);
  },

  async 'service-run'(el) { await runAction(el.dataset.id, el); },

  'service-reload'() { location.reload(); },
};

/* PUT api/service/listen from the form; false when nothing was saved. */
async function saveListen() {
  const hs = state.hostService;
  const port = Math.round(+hs.port);
  if (!(port >= 1 && port <= 65535)) { toast('The port is a number from 1 to 65535.', 'danger'); return false; }
  hs.data.service = await api('PUT', '/service/listen', { scope: hs.scope, port });
  render();
  const l = hs.data.service.listen;
  toast(l.restartNeeded ? `Saved. The router listens on ${l.urls} after a restart.` : 'Saved.', 'ok');
  return true;
}

/* Asks, then POST api/service/{action}, then waits for the router to come back (or says it has stopped). */
async function runAction(action, el) {
  const hs = state.hostService;
  const sv = hs.data.service;
  const bg = sv.background || {};
  const listen = sv.listen;
  const lockout = listen.restartNeeded && listen.scope === 'local' && browsingRemotely()
    ? '<p><b>You are using the dashboard from another machine.</b> Afterwards the router only answers this machine, so this page cannot reach it.</p>' : '';
  const moving = listen.restartNeeded ? `<p>It will listen on <span class="mono">${h(listen.urls)}</span>.</p>` : '';
  const texts = {
    restart: ['Restart the router?', `<p>Requests in flight finish first (up to 30 seconds); new ones are refused until it is back, usually within seconds. Unapplied changes in the draft are kept.</p>${moving}${lockout}`, 'Restart', 'primary'],
    stop: ['Stop the router?', `<p>Clients get no answers until it starts again, and this dashboard stops working. ${startAgain(sv)}</p>`, 'Stop the router', 'danger'],
    install: [`Run the router as a ${bg.kind}?`, `<p>It starts the router whenever you sign in${bg.restartsOnFailure ? ' and restarts it if it fails' : ''}. This router stops and hands over to it; the page reconnects once the service's router listens.</p>${moving}${lockout}`, 'Install and hand over', 'primary'],
    reinstall: [`Write the ${bg.kind} again?`, `<p>It then runs this program with the saved settings, and the router restarts.</p>${moving}${lockout}`, 'Reinstall', 'primary'],
    uninstall: [`Remove the ${bg.kind}?`, sv.runsAs === 'service'
      ? `<p>This router is the service's: it stops now and no longer starts when you sign in. Start it again with <span class="mono">semantic-router</span> in a terminal, or install the service again from there.</p>`
      : '<p>The router no longer starts when you sign in. This router restarts once and keeps running.</p>', 'Remove the service', 'danger'],
  };
  const [title, body, label, kind] = texts[action];
  if (!await ask({ title, body, actions: [{ label, value: true, kind }, { label: 'Cancel', value: false, focus: true }] })) return;

  const before = state.status && state.status.instance;
  const origin = nextOrigin(listen);
  await api('POST', `/service/${action}`);
  if (action === 'stop' || (action === 'uninstall' && sv.runsAs === 'service')) {
    hs.waiting = null;
    hs.stopped = { action, startAgain: startAgain(sv) };
    render();
    return;
  }

  hs.waiting = action;
  render();
  const back = await waitForRouter(before, origin);
  hs.waiting = null;
  if (back && back.moved) { location.href = back.moved + PREFIX + '/#/service'; return; }
  if (!back) {
    hs.stopped = { action, timedOut: true, log: bg.logHint };
    render();
    return;
  }

  hs.scope = null;
  await loadService();
  await refreshStatus({ force: true });
  const outcome = hs.data && hs.data.service && hs.data.service.lastOutcome;
  if (!outcome || outcome.succeeded) toast(action === 'restart' ? 'The router restarted.' : 'Done: the router is back.', 'ok');
}

const outcomeBanner = (o) => o
  ? `<div class="banner page-banner ${o.succeeded ? 'ok' : 'danger'}" role="${o.succeeded ? 'status' : 'alert'}">${h(o.message)} <span class="faint small">${h(new Date(o.at).toLocaleString())}</span></div>`
  : '';

const actionButton = (sv, action, label, kind, title) => sv.actions.includes(action)
  ? `<button class="btn ${kind || ''} small" data-action="service-run" data-id="${action}"${title ? ` title="${h(title)}"` : ''}>${h(label)}</button>`
  : '';

/* What clients and admins on other machines need, once the router listens beyond this machine. */
function remoteChecklist(data) {
  const sv = state.status.service || {};
  const d = data.dashboard;
  const port = state.hostService.port || data.service.listen.port;
  const items = [
    sv.gatewayKeysConfigured
      ? '<li><span class="badge ok">gateway keys set</span> Clients on other machines send one (<span class="mono">Authorization: Bearer</span> or <span class="mono">x-api-key</span>).</li>'
      : (state.config.Middleware || {}).AllowUnauthenticatedRemoteClients
        ? '<li><span class="badge warn">no gateway key</span> Anything that can reach this machine can use your models. Add keys under <a href="#/gateway">Gateway</a>.</li>'
        : '<li><span class="badge warn">no gateway key</span> Clients on other machines are refused until you add one under <a href="#/gateway">Gateway</a>.</li>',
    d.requireLocalClient || !d.tokensConfigured
      ? '<li><span class="badge">dashboard: this machine only</span> To open the dashboard from elsewhere, set <span class="mono">SemanticRouter:Dashboard:AdminToken</span> and <span class="mono">RequireLocalClient=false</span> in <span class="mono">appsettings.json</span>, then restart.</li>'
      : '<li><span class="badge ok">dashboard: token sign-in</span> Other machines sign in with a dashboard token.</li>',
    '<li><span class="badge warn">plain HTTP</span> Prompts and keys cross the network unencrypted. Beyond a network you trust, put a reverse proxy with TLS in front.</li>',
    `<li>Your firewall must let other machines reach port <span class="mono">${h(port)}</span>.</li>`,
  ];
  if ((state.status.endpoints || []).some((e) => e.program && e.via)) items.push('<li>Subscription CLI connectors (claude, codex, copilot) still answer only this machine.</li>');
  return `<h3 style="margin-top:16px">Serving other machines</h3><ul class="small checklist">${items.join('')}</ul>`;
}

function listenCard(data) {
  const hs = state.hostService;
  const l = data.service.listen;
  const sv = state.status.service || {};
  const now = (sv.listening || []).join(', ') || location.origin;
  const disabled = l.editable ? '' : 'disabled';
  const radio = (value, label, hint) => `<label class="check choice"><input type="radio" name="service-scope" value="${value}" data-state="hostService.scope" ${hs.scope === value ? 'checked' : ''} ${disabled}> <span class="choice-text"><b>${label}</b><br><span class="faint small">${hint}</span></span></label>`;
  const network = hs.scope === 'network' || l.scope === 'network' || sv.remote;
  return `<div class="card section"><div class="card-head"><h2>Who can connect</h2><span class="muted small">Where the router listens. Saved in <span class="mono">${h(l.file)}</span>; it applies when the router restarts.</span></div>
    ${l.pinnedBy ? `<p class="small"><span class="badge warn">set elsewhere</span> ${h(l.pinnedBy[0].toUpperCase() + l.pinnedBy.slice(1))} decides where the router listens${l.editable ? `, not the setting below. The setting is used once the router starts without it${data.service.runsAs === 'service' ? ': reinstall the background service below' : ''}.` : '. Change it there.'}</p>` : ''}
    <div role="radiogroup" aria-label="Who can connect" class="choices">
      ${radio('local', 'This machine only', 'Listens on localhost. Clients on other machines cannot reach it.')}
      ${radio('network', 'Other machines too', 'Listens on every network interface (0.0.0.0), for clients elsewhere on your network.')}
    </div>
    ${l.scope === 'custom' ? `<p class="small muted">Now set to <span class="mono">${h(l.urls)}</span>; saving replaces it.</p>` : ''}
    <div class="form-grid" style="margin-top:8px">
      <label class="field"><span class="name">Port</span><input type="number" min="1" max="65535" data-state="hostService.port" data-kind="number" value="${h(hs.port ?? l.port)}" ${disabled}><span class="hint">Default 5080. Clients use it in the base URL.</span></label>
    </div>
    <div class="inline" style="margin-top:10px">
      <button class="btn small" data-action="service-save" ${disabled}>Save</button>
      ${data.service.actions.includes('restart') && !l.pinnedBy ? `<button class="btn primary small" data-action="service-save-restart" ${disabled}>Save and restart</button>` : ''}
    </div>
    <table class="kv" style="margin-top:12px">
      <tr><th>Listening now</th><td class="mono small break">${h(now)}</td></tr>
      ${l.restartNeeded ? `<tr><th>After a restart</th><td><span class="mono small">${h(l.urls)}</span> <span class="badge info">restart to apply</span></td></tr>` : ''}
    </table>
    ${network ? remoteChecklist(data) : ''}
  </div>`;
}

function backgroundCard(data) {
  const sv = data.service;
  const bg = sv.background;
  const restarts = bg && bg.restartsOnFailure ? ', and restarts it if it fails' : '';
  const head = `<div class="card-head"><h2>Background service</h2><span class="muted small">Runs the router whenever you sign in, without a terminal${restarts}. Per user: no administrator rights.</span></div>`;
  if (sv.runsAs === 'container') return `<div class="card section">${head}<p class="small muted">In a container, the container runtime runs and restarts the router.</p></div>`;
  if (!bg) return `<div class="card section">${head}<p class="small muted">This operating system has no service manager the router can use. Start <span class="mono">semantic-router</span> with your own supervisor.</p></div>`;
  return `<div class="card section">${head}
    <div class="table-wrap plain"><table class="kv">
      <tr><th>Service manager</th><td>${h(bg.kind)}</td></tr>
      <tr><th>Installed</th><td>${bg.installed ? `<span class="badge ok">yes</span>${bg.status ? ` <span class="small muted">${h(bg.status)}</span>` : ''}` : '<span class="badge">no</span>'}</td></tr>
      <tr><th>Definition</th><td class="mono small break">${h(bg.location)}</td></tr>
      <tr><th>Logs</th><td>${copyable(bg.logHint)}</td></tr>
    </table></div>
    ${!bg.installed && bg.notes.length ? `<ul class="small faint">${bg.notes.map((n) => `<li>${h(n)}</li>`).join('')}</ul>` : ''}
    <div class="inline" style="margin-top:10px">
      ${actionButton(sv, 'install', 'Install and hand over', 'primary', 'Install the service; this router stops and the service starts its own')}
      ${actionButton(sv, 'reinstall', 'Reinstall', '', 'Write the definition again with this program and the saved settings, and restart')}
      ${actionButton(sv, 'uninstall', 'Remove the service…', 'danger')}
    </div>
    ${bg.installed && !sv.actions.includes('reinstall') && sv.runsAs === 'service' ? '<p class="small faint">To change the service\'s definition, run <span class="mono">semantic-router service install</span> in a terminal.</p>' : ''}
  </div>`;
}

views.service = () => {
  const hs = state.hostService;
  const head = '<div class="section-head"><h1>Service</h1><span class="muted">How the router runs on this machine: who can connect, restarting or stopping it, and running it in the background.</span></div>';
  if (readOnly()) return head + '<div class="card muted">Only the admin role can see and change how the router runs.</div>';
  if (hs.stopped) {
    const s = hs.stopped;
    return head + `<div class="card section" role="status"><h2>${s.timedOut ? 'The router has not come back' : 'The router has stopped'}</h2>
      <p class="small">${s.timedOut ? `It did not answer within a minute. See what it logged${s.log ? `: ${copyable(s.log)}` : '.'}` : s.startAgain}</p>
      <div class="inline"><button class="btn small" data-action="service-reload">Reload the page</button></div></div>`;
  }
  if (hs.waiting) {
    return head + `<div class="card section" role="status" aria-busy="true"><h2>${hs.waiting === 'restart' ? 'Restarting…' : 'Handing over…'}</h2><p class="small muted">The page reconnects once the router listens again.</p></div>`;
  }
  if (!hs.data && !hs.error) { loadService(); return head + '<div class="muted">Loading…</div>'; }
  if (hs.error) return head + `<div class="card"><p class="small">${h(hs.error)}</p><button class="btn small" data-action="service-load">Try again</button></div>`;
  const data = hs.data;
  if (!data.available) {
    const desktop = typeof window.invokeCSharpAction === 'function' || (window.external && typeof window.external.invokeCSharpAction === 'function');
    return head + `<div class="card section"><div class="card-head"><h2>This router</h2></div>${runsOn()}
      <p class="small muted" style="margin-top:10px">${desktop
        ? 'The desktop app runs this router: <b>App settings</b> under <b>More</b> restarts it, runs it as the background service and starts the app at sign-in.'
        : 'This router runs inside an application that decides where it listens and how it runs; the dashboard cannot change that.'}</p></div>`;
  }
  const sv = data.service;
  return head + outcomeBanner(sv.lastOutcome) + `
    <div class="card section"><div class="card-head"><h2>This router</h2><span class="spacer"></span>
        ${actionButton(sv, 'restart', 'Restart', '')}
        ${actionButton(sv, 'stop', 'Stop…', 'danger')}
        <button class="btn ghost small" data-action="service-load">Refresh</button></div>
      <div class="table-wrap plain">${runsOn().replace('<table class="kv">', () => `<table class="kv"><tr><th>Runs as</th><td>${h(RUNS_AS[sv.runsAs] || sv.runsAs)}</td></tr>`)}</div>
    </div>
    ${listenCard(data)}
    ${backgroundCard(data)}`;
};
