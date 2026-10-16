/* Semantic Router dashboard: first run. The "Set up SemanticRouter" card on the Overview, the connector presets (also
   behind Add connector), and discovering a connector's models from the draft. */
import { lineupPanel } from './config.js';
import { $, api, h, isDirty, lineupFor, readOnly, state, toast } from './core.js';
import { closeSection, openAttr } from './fields.js';
import { plural } from './labels.js';
import { pageActions } from './page-actions.js';
import { render } from './shell.js';

/*
 * GET api/setup: what `semantic-router init` would find on this machine (detected) and the ready-made connectors
 * (presets). It may start processes (it asks CLIs whether they are signed in), so it is asked only when needed: on
 * the Overview while no model is enabled, and when the Add connector menu or an empty Connectors page shows presets.
 * An older router without it gives no presets; the checklist still works.
 */
const setup = () => (state.setup = state.setup || { data: null, loading: false, absent: false, error: null, shown: false, dismissed: false, added: [] });

export async function loadSetup() {
  const s = setup();
  if (s.loading || s.data) return;
  s.loading = true;
  try {
    s.data = await api('GET', '/setup');
  } catch (e) {
    s.absent = e.status === 404 || e.status === 405;
    s.error = s.absent || e.status === 403 ? null : e.message;
    s.data = { needed: !anyEnabled(), detected: [], presets: [] };
  }
  s.loading = false;
  if (['overview', 'connectors', 'models'].includes(state.view)) render();
}

const anyEnabled = () => ((state.status && state.status.models) || []).some((m) => m.enabled);
const draftConnectors = () => Object.keys((state.config && state.config.Connectors) || {});
const draftEnabledModels = () => ((state.config && state.config.Models) || []).filter((m) => m.Enabled !== false);
// The tester's reply text starts with "HTTP <status>:" when the gateway refused or failed.
const replyFailed = (r) => !!r && /^HTTP \d{3}:/.test(r.text || '');
// Answered since the card first showed: by the test prompt, or by any client.
const servedOnce = () => (!!state.tester.reply && !replyFailed(state.tester.reply))
  || state.activity.some((e) => e.servedBy && e.outcome !== 'failed' && new Date(e.timestamp) >= setup().since);

/* True while the setup card is on the Overview: the "no model is enabled" message is shown there, once. */
export function setupShowing() {
  if (readOnly() || !state.status) return false;
  const s = setup();
  return !s.dismissed && (!anyEnabled() || s.shown);
}

/* ---------------------------------------------------------------------------------------------
   Presets and detected tools
   --------------------------------------------------------------------------------------------- */
const KIND = { cli: 'your sign-in', local: 'this machine', cloud: 'cloud API', copilot: 'your sign-in' };

/* One detected tool or preset, with what enabling it means. */
function choice(c, detected) {
  const added = setup().added.includes(c.id);
  const note = detected ? c.consent : c.needsKey ? 'Needs an API key: set the environment variable it names, or type the key on the connector after adding it.' : '';
  return `<li class="choice">
    <div class="choice-text"><b>${h(c.title)}</b>${detected ? ' <span class="badge ok">found</span>' : ''}${c.needsKey ? ' <span class="badge">needs an API key</span>' : ''}${KIND[c.kind] ? ` <span class="badge">${KIND[c.kind]}</span>` : ''}
      ${c.detail ? `<div class="small muted">${h(c.detail)}</div>` : ''}${note ? `<div class="small faint">${h(note)}</div>` : ''}</div>
    ${readOnly() ? '' : added ? '<span class="badge ok">added</span>' : `<button class="btn small ${detected ? 'primary' : ''}" data-action="setup-choose" data-id="${h(c.id)}" data-detected="${detected ? '1' : ''}">${detected ? 'Enable' : 'Add'}</button>`}
  </li>`;
}

/* Detected tools first, then the presets. With blank, the last entry adds an empty connector to fill in by hand. */
export function presetList(blank) {
  const s = setup();
  if (!s.data) { loadSetup(); return '<p class="muted small">Looking for providers on this machine…</p>'; }
  const detected = s.data.detected || []; const presets = s.data.presets || [];
  return `<ul class="choices">${detected.map((c) => choice(c, true)).join('')}${presets.map((c) => choice(c, false)).join('')}
    ${blank ? `<li class="choice"><div class="choice-text"><b>Blank connector</b><div class="small muted">Any OpenAI-compatible server, the Anthropic API or a CLI, set up by hand.</div></div><button class="btn small" data-action="connector-add">Add</button></li>` : ''}</ul>
    ${s.error ? `<p class="small muted">Couldn't look for providers on this machine: ${h(s.error)}</p>` : ''}`;
}

/* The Add connector button: a menu of the presets and a blank connector. */
export function addConnectorMenu() {
  return `<details class="menu" data-setup-menu ${openAttr('add-connector')}><summary class="btn primary small">Add connector</summary><div class="menu-panel card">${setup().data ? presetList(true) : '<p class="muted small">Looking for providers on this machine…</p>'}</div></details>`;
}

/* Adds a connector to the draft under a free name; returns the name used. */
function addConnector(name, settings) {
  const conns = state.config.Connectors = state.config.Connectors || {};
  const taken = (n) => Object.keys(conns).some((k) => k.toLowerCase() === n.toLowerCase());
  let free = name; let i = 1;
  while (taken(free)) free = `${name}-${++i}`;
  conns[free] = JSON.parse(JSON.stringify(settings || {}));
  return free;
}

/* Enables a detected tool (its connector, models and aliases) or adds a preset's connector, in the draft. */
function choose(id, detected) {
  const s = setup();
  const c = ((detected ? s.data.detected : s.data.presets) || []).find((x) => x.id === id);
  if (!c) return;
  const name = addConnector(c.connector.name, c.connector.settings);
  const models = state.config.Models = state.config.Models || [];
  const added = [];
  for (const m of c.models || []) {
    const model = JSON.parse(JSON.stringify(m));
    let n = model.Name; let i = 1;
    while (models.some((x) => (x.Name || '').toLowerCase() === n.toLowerCase())) n = `${model.Name}-${++i}`;
    models.push({ ...model, Name: n, Connector: name });
    added.push(n);
  }
  const skipped = [];
  if (c.aliases && Object.keys(c.aliases).length) {
    const aliases = (state.config.Middleware = state.config.Middleware || {}).ModelAliases = state.config.Middleware.ModelAliases || {};
    Object.entries(c.aliases).forEach(([k, v]) => { if (k in aliases) { if (aliases[k] !== v) skipped.push(k); } else aliases[k] = v; });
  }
  s.added.push(c.id);
  s.connector = name;
  closeSection('add-connector');
  state.open.add('conn:' + name);
  render();
  toast(added.length
    ? `Added ${c.title} to the draft with ${plural(added.length, 'model')}. Apply & save to use ${added.length === 1 ? 'it' : 'them'}.${skipped.length ? ` Kept your aliases for ${skipped.join(', ')}.` : ''}`
    : `Added ${c.title} to the draft as “${name}”. Next: discover its models.`, 'ok');
}

/* ---------------------------------------------------------------------------------------------
   Discover models, from the draft
   --------------------------------------------------------------------------------------------- */
/*
 * POST api/connectors/discover asks a connector as the draft describes it, so a connector added a moment ago works
 * without Apply. An older router without it asks the running connector by name, which needs the draft applied first.
 */
export async function discoverDraft(connector) {
  let l = lineupFor(connector);
  if (!l) { l = { connector }; state.lineups.push(l); }
  Object.assign(l, { busy: true, error: null });
  render();
  const settings = state.config.Connectors && state.config.Connectors[connector];
  let r;
  try {
    try {
      r = await api('POST', '/connectors/discover', { name: connector, settings });
    } catch (e) {
      if (e.status !== 404 && e.status !== 405) throw e;
      r = await api('POST', `/connectors/${encodeURIComponent(connector)}/discover`);
    }
    // In the catalog means in the draft's catalog: a model added a moment ago counts.
    const draft = (state.config.Models || []).filter((m) => (m.Connector || '').toLowerCase() === connector.toLowerCase());
    const lineup = (r.lineup || []).map((e) => {
      const own = draft.find((m) => m.Model === e.id);
      const inCatalog = own ? own.Name : e.inCatalog;
      return { ...e, inCatalog, selected: !!(e.recommended && e.suggestion && !inCatalog) };
    });
    Object.assign(l, r, { busy: false, lineup });
  } catch (e) {
    Object.assign(l, { busy: false, error: e.status === 404 && isDirty() ? `${e.message} Apply the draft first: this router asks its running connectors.` : e.message });
  }
  render();
}

/* ---------------------------------------------------------------------------------------------
   The setup card
   --------------------------------------------------------------------------------------------- */
function step(n, title, status, done, current, body) {
  return `<li class="step ${done ? 'done' : current ? 'current' : 'todo'}" aria-current="${current ? 'step' : 'false'}">
    <div class="step-head"><span class="step-mark" aria-hidden="true">${done ? '✓' : n}</span><b>${title}</b><span class="small muted">${status}</span></div>
    ${body ? `<div class="step-body">${body}</div>` : ''}</li>`;
}

export function setupCard() {
  if (!state.status) return '';
  if (readOnly()) {
    return anyEnabled() ? '' : `<div class="card section setup" id="setup"><div class="card-head"><h2>Not set up yet</h2></div>
      <p class="small">No model is enabled, so the router can't answer requests yet. An admin needs to add one.</p></div>`;
  }
  const s = setup();
  if (!setupShowing()) return '';
  if (!anyEnabled() && !s.data) loadSetup();
  if (!s.shown) { s.shown = true; s.since = new Date(); }

  const conns = draftConnectors();
  const running = anyEnabled();
  const pending = draftEnabledModels().filter((m) => !((state.status.models || []).some((x) => x.enabled && x.name.toLowerCase() === String(m.Name).toLowerCase())));
  const one = conns.length > 0;
  const two = running;
  const three = two && servedOnce();

  const step1 = one
    ? `<p class="small">In the draft: ${conns.map((c) => `<a href="#/connectors" data-action="setup-open-connector" data-name="${h(c)}">${h(c)}</a>`).join(', ')}.</p>
       <details class="small" ${openAttr('setup-another')}><summary>Add another</summary>${presetList(false)}</details>`
    : `${presetList(false)}<p class="small muted">Or set one up by hand on <a href="#/connectors">Connectors</a>.</p>`;

  const noModels = conns.filter((c) => !draftEnabledModels().some((m) => (m.Connector || '').toLowerCase() === c.toLowerCase()));
  const step2 = !one ? '' : two && !pending.length ? '' : `
    ${pending.length ? `<div class="inline"><span class="small">${plural(pending.length, 'model')} in the draft, not running yet.</span><button class="btn primary small" data-action="apply-save">Apply &amp; save</button></div>` : ''}
    ${noModels.map((c) => `<div class="inline" style="margin-top:8px"><span class="small">${h(c)} has no models yet.</span><button class="btn small" data-action="lineup-discover" data-connector="${h(c)}">Discover models</button></div>${lineupPanel(c)}`).join('')}
    <p class="small muted">Or add them by hand on <a href="#/models">Models</a>.</p>`;

  const reply = state.tester.reply;
  const step3 = !two ? '' : `<div class="inline"><button class="btn ${three ? '' : 'primary'} small" data-action="setup-test" ${state.tester.busy ? 'disabled' : ''}>Send a test prompt</button><span class="small muted">Sends “Say hello in five words.” through the gateway, as a client would.</span></div>
    ${replyFailed(reply) ? `<p class="small" style="color:var(--danger);margin-top:6px">${h(reply.text.slice(0, 300))}</p>` : ''}`;

  return `<div class="card section setup" id="setup" role="region" aria-label="Set up SemanticRouter">
    <div class="card-head"><h2>${three ? 'SemanticRouter is set up' : 'Set up SemanticRouter'}</h2><span class="spacer"></span><button class="btn ghost small" data-action="setup-dismiss">${three ? 'Done' : 'Hide'}</button></div>
    ${three ? `<p class="small">It answered. Next, point your tools at it: <a href="#/clients">Connect clients</a>.</p>`
      : `<p class="small muted">No model is enabled yet, so every request fails. Three steps get it answering.</p>`}
    <ol class="steps">
      ${step(1, 'Add a connector', one ? 'done' : 'how the router reaches a provider', one, !one, three ? '' : step1)}
      ${step(2, 'Add models', two ? 'done' : pending.length ? 'apply to use them' : 'the models it may choose from', two, one && !two, three ? '' : step2)}
      ${step(3, 'Send a test prompt', three ? 'done' : 'check it answers', three, two && !three, three ? '' : step3)}
    </ol>
  </div>`;
}

Object.assign(pageActions, {
  'setup-choose'(el) { choose(el.dataset.id, !!el.dataset.detected); },
  'setup-dismiss'() { setup().dismissed = true; render(); },
  'setup-open-connector'(el) { state.open.add('conn:' + el.dataset.name); location.hash = '#/connectors'; },
  async 'setup-test'() {
    const { actions } = await import('./actions.js'); // at click time: actions.js is loaded by then
    state.tester.prompt = 'Say hello in five words.';
    state.tester.model = 'auto';
    const p = $('#tester-prompt'); if (p) p.value = state.tester.prompt;
    const m = $('#tester-model'); if (m) m.value = 'auto';
    await actions['tester-send']();
  },
});

// The Add connector menu loads the presets when it opens (toggle does not bubble: capture).
document.addEventListener('toggle', (ev) => {
  if (ev.target && ev.target.matches && ev.target.matches('details[data-setup-menu]') && ev.target.open && !setup().data) loadSetup();
}, true);
