/* Semantic Router dashboard: Actions behind data-action buttons, and applying configuration. */
import { lineDiff } from './config.js';
import { $, ask, api, connectorNames, evaluationEdited, h, isDirty, jsonEdited, lineupFor, plural, readOnly, setPath, state, tiers, toast } from './core.js';
import { loadAudit } from './audit.js';
import { loadTrends } from './charts.js';
import { aliasRows, draftChanges, limitRows, localProblems, pageForPath, writeAliases, writeLimits } from './draft.js';
import { loadTuning } from './learned.js';
import { loadReliability } from './reliability.js';
import { graph, loadMemory, updateMemorySide } from './memory.js';
import { adoptConfig, fieldFor, noteConflict, refreshStatus, render, renderChrome, setConfig } from './shell.js';
import { discoverDraft } from './setup.js';
import { loadUsage } from './usage.js';

/* ---------------------------------------------------------------------------------------------
   Removing with Undo. Items whose open state is keyed by position (route:3, model:0, mem:1) shift with a removal, so
   the item that moves into the gap does not open in its place; Undo puts the item back where it was, open or not.
   --------------------------------------------------------------------------------------------- */
function shiftOpen(prefix, index, delta) {
  const keys = [...state.open].filter((k) => k.startsWith(prefix + ':') && /^\d+$/.test(k.slice(prefix.length + 1)));
  const was = state.open.has(`${prefix}:${index}`);
  keys.forEach((k) => state.open.delete(k));
  keys.forEach((k) => {
    const i = +k.slice(prefix.length + 1);
    if (delta < 0) { if (i !== index) state.open.add(`${prefix}:${i > index ? i - 1 : i}`); } else state.open.add(`${prefix}:${i >= index ? i + 1 : i}`);
  });
  return was;
}
/* Removes list()[index]; returns the function that puts it back. */
function spliceItem(list, index, prefix) {
  const [item] = list().splice(index, 1);
  const wasOpen = prefix ? shiftOpen(prefix, index, -1) : false;
  return () => {
    const now = list();
    now.splice(Math.min(index, now.length), 0, item);
    if (prefix) { shiftOpen(prefix, index, 1); if (wasOpen) state.open.add(`${prefix}:${index}`); }
  };
}
/* Removes a key from a map (connectors, profiles), keeping the others' order; returns the function that puts it back. */
function removeKey(holder, prop, name) {
  const entries = Object.entries(holder()[prop] || {});
  const at = entries.findIndex(([k]) => k === name);
  const [entry] = entries.splice(at, 1);
  holder()[prop] = Object.fromEntries(entries);
  return () => {
    const now = Object.entries(holder()[prop] || {}).filter(([k]) => k !== name);
    now.splice(Math.min(at, now.length), 0, entry);
    holder()[prop] = Object.fromEntries(now);
  };
}
function undoToast(message, undo, focus) {
  toast(message, 'ok', { action: { label: 'Undo', run: () => { undo(); state.focusAfter = focus || null; render(); } } });
}
const ensure = (holder, prop) => () => (holder()[prop] = holder()[prop] || []);
const config = () => state.config;
const usage = () => (state.config.Usage = state.config.Usage || {});
const memory = () => (state.config.Memory = state.config.Memory || { Sources: [] });
// After a removal: the item now in its place, the one before it, or the page heading.
const nextItem = (prefix, i) => [`.item[data-key="${prefix}:${i}"] .item-head`, `.item[data-key="${prefix}:${i - 1}"] .item-head`, '#view h1'];
const nextRow = (bind, i, add) => [`[data-bind="${bind.replace('#', i)}"]`, `[data-bind="${bind.replace('#', i - 1)}"]`, `[data-action="${add}"]`, '#view h1'];

function removeListItem(list, index, prefix, label, focus, restoredFocus) {
  const undo = spliceItem(list, index, prefix);
  state.focusAfter = focus;
  render();
  undoToast(`Removed ${label}.`, undo, restoredFocus);
}

/* The draft is about to be replaced: say how many changes go with it. */
const replacesDraft = () => (isDirty() ? ` Your ${plural(draftChanges(), 'unapplied change')} ${draftChanges() === 1 ? 'is' : 'are'} replaced.` : '');
/* After the running configuration changed: what happened to the draft. */
const draftNote = (result) => (result === 'rebased' ? ' Your unapplied changes are kept on top of it.' : result === 'kept' ? ' Your draft is kept as it was: see the notice above.' : '');
/* Contract: an action that applied a change says whether it was also saved (saved: false while other changes are unsaved). */
const savedWord = (r) => (r && r.saved === false ? 'applied, not saved' : 'applied and saved');

async function confirmOverwrite() {
  if (!jsonEdited()) return true;
  return await ask({ title: 'Replace the text in the editor?', body: '<p>What you typed in the JSON editor is not in the draft yet, and it would be replaced.</p>', actions: [{ label: 'Replace it', value: true, kind: 'danger' }, { label: 'Keep my text', value: false, focus: true }] });
}

/* ---------------------------------------------------------------------------------------------
   Problems from Validate and Apply, each tied to its field when the router says which (issues), else listed.
   --------------------------------------------------------------------------------------------- */
let problemsToast = null;
function showProblems(errors, issues, applying) {
  state.errors = errors;
  state.issues = issues.map((x) => ({ ...x, page: x.page || (x.path ? pageForPath(x.path) : null) }));
  state.focusIssue = true;
  render();
  if (problemsToast) problemsToast.dismiss();
  const text = plural(errors.length, 'problem');
  problemsToast = toast(applying ? `Not applied: ${text}.` : `${text} found.`, 'danger', { sticky: applying });
}
function clearProblems() {
  state.errors = null; state.issues = null;
  if (problemsToast) { problemsToast.dismiss(); problemsToast = null; }
}
/* The router's answer to a configuration it refused: errors always, issues (with a path each) from newer routers. */
const issuesOf = (errors, issues) => (Array.isArray(issues) && issues.length ? issues : errors.map((message) => ({ path: null, message })));

/* ---------------------------------------------------------------------------------------------
   Actions
   --------------------------------------------------------------------------------------------- */
export const actions = {
  toggle(el) {
    const k = el.dataset.key; if (state.open.has(k)) state.open.delete(k); else state.open.add(k);
    $(`.item[data-key="${CSS.escape(k)}"]`).classList.toggle('open', state.open.has(k));
    el.setAttribute('aria-expanded', String(state.open.has(k)));
  },
  refresh() { return refreshStatus({ force: true }); },

  async validate() {
    const local = localProblems();
    let r;
    try { r = await api('POST', '/config/validate', state.config); } catch (e) {
      if (e.status !== 400 || !e.data) throw e;
      r = { valid: false, errors: e.data.errors || [e.message], issues: e.data.issues };
    }
    const errors = local.map((x) => x.message).concat(r.valid ? [] : r.errors);
    if (!errors.length) { clearProblems(); render(); toast('Configuration is valid.', 'ok'); return; }
    showProblems(errors, local.concat(r.valid ? [] : issuesOf(r.errors, r.issues)), false);
  },
  async apply() { await applyConfig(false); },
  async 'apply-save'() { await applyConfig(true); },
  async discard() {
    const n = draftChanges();
    const ok = await ask({ title: `Discard ${plural(n, 'change')}?`, body: '<p>The draft goes back to the running configuration.</p>', actions: [{ label: 'Discard', value: true, kind: 'danger' }, { label: 'Keep editing', value: false, focus: true }] });
    if (!ok) return;
    const draft = state.config; const open = new Set(state.open);
    state.config = JSON.parse(state.original);
    clearProblems();
    render();
    toast(`Discarded ${plural(n, 'change')}.`, 'ok', { action: { label: 'Undo', run: () => { state.config = draft; state.open = open; render(); } } });
  },
  'draft-review'() {
    const before = JSON.stringify(JSON.parse(state.original), null, 2);
    return ask({ title: `Unapplied changes (${draftChanges()})`, wide: true, body: `<p class="muted small">The running configuration (−) and your draft (+).</p><pre class="diff">${lineDiff(before, JSON.stringify(state.config, null, 2))}</pre>`, actions: [{ label: 'Close', value: null, focus: true }] });
  },
  async 'config-reset'() {
    const ok = await ask({ title: 'Reset to the application baseline?', body: `<p>The running configuration goes back to the application's own settings. The stored file is left in place.${replacesDraft()}</p>`, actions: [{ label: 'Reset', value: true, kind: 'danger' }, { label: 'Cancel', value: false, focus: true }] });
    if (!ok) return;
    setConfig(await api('POST', '/config/reset'));
    await refreshStatus();
    render();
    toast('Reset to baseline.', 'ok');
  },
  // "Running configuration not saved": write it to the store as it is, or go back to what the store holds.
  async 'config-save'() {
    let r;
    try { r = await api('POST', '/config/save'); } catch (e) {
      if (e.status === 404 || e.status === 405) { state.saveApi = false; renderChrome(); toast('This router cannot save from here: apply your next change with Apply & save.', 'danger'); return; }
      throw e;
    }
    const result = adoptConfig(r);
    await refreshStatus();
    render();
    toast((r.store ? `Saved to ${r.store}.` : 'Saved.') + draftNote(result), 'ok');
  },
  async 'config-revert'() {
    const ok = await ask({ title: 'Revert to the saved configuration?', body: '<p>The router goes back to what the configuration store holds. The changes that were applied without saving are lost.</p>', actions: [{ label: 'Revert', value: true, kind: 'danger' }, { label: 'Cancel', value: false, focus: true }] });
    if (!ok) return;
    let r;
    try { r = await api('POST', '/config/revert'); } catch (e) {
      if (e.status === 404 || e.status === 405) { state.saveApi = false; renderChrome(); toast('This router cannot revert from here.', 'danger'); return; }
      throw e;
    }
    const result = adoptConfig(r);
    await refreshStatus();
    render();
    toast('Back to the saved configuration.' + draftNote(result), 'ok');
  },
  // The configuration changed under the draft (a 409 from Apply, or seen by the refresh).
  async 'conflict-reload'() {
    const before = { config: state.config, original: state.original, meta: state.meta, open: new Set(state.open) };
    setConfig(await api('GET', '/config'));
    render();
    toast('Loaded the running configuration; your changes were dropped.', 'ok', { action: { label: 'Undo', run: () => {
      Object.assign(state, { config: before.config, original: before.original, meta: before.meta, open: before.open });
      noteConflict({ currentVersion: state.status ? state.status.version : null, instance: state.status ? state.status.instance : undefined });
      render();
    } } });
  },
  async 'conflict-compare'() {
    const running = await api('GET', '/config');
    await ask({ title: `Version ${running.version} and your draft`, wide: true, body: `<p class="muted small">Lines marked − are in the running configuration only, + in your draft only.</p><pre class="diff">${lineDiff(JSON.stringify(running.config, null, 2), JSON.stringify(state.config, null, 2))}</pre>`, actions: [{ label: 'Close', value: null, focus: true }] });
  },
  async 'conflict-apply'() {
    const c = state.conflict || {};
    await applyConfig(c.persist === true, c.currentVersion, c.instance);
  },
  'conflict-dismiss'() { if (state.conflict) state.conflict.dismissed = true; renderChrome(); },
  // A problem in the list: its field, on its page.
  'issue-go'(el) {
    const x = (state.issues || [])[+el.dataset.index];
    if (!x) return;
    state.focusPath = x.path || x.selector;
    const here = x.selector ? $(x.selector) : fieldFor($('#view'), x.path);
    const page = x.page || pageForPath(x.path);
    if (here || page === state.view) render(); else location.hash = '#/' + page;
  },
  async 'activity-purge'() {
    const ok = await ask({ title: 'Delete every activity entry?', body: '<p>With their prompt previews. This cannot be undone; usage, budgets and the configuration history are kept.</p>', actions: [{ label: 'Delete all activity', value: true, kind: 'danger' }, { label: 'Cancel', value: false, focus: true }] });
    if (!ok) return;
    const result = await api('DELETE', '/activity');
    state.activity = [];
    render();
    toast(`Deleted ${result.removed} activity entries.`, 'ok');
  },
  'activity-show'() { state.activityNew = 0; render(); },
  async 'memory-refresh'() { await loadMemory(true); },
  async 'memory-reset'() {
    const ok = await ask({ title: 'Forget where notes were sent?', body: '<p>Which notes were sent to which models is forgotten. The notes themselves are not touched.</p>', actions: [{ label: 'Clear sharing history', value: true, kind: 'danger' }, { label: 'Cancel', value: false, focus: true }] });
    if (!ok) return;
    await api('POST', '/memory/exposure/reset');
    if (state.memory) state.memory.exposure = {};
    render();
    toast('Sharing history cleared.', 'ok');
  },
  'memory-fit'() { if (graph) graph.fit(); },
  'memory-select'(el) {
    state.memoryUi.selected = el.dataset.id || null;
    if (graph) { graph.select(state.memoryUi.selected); if (state.memoryUi.selected) graph.centerOn(state.memoryUi.selected); }
    updateMemorySide();
  },
  'memory-open'(el) {
    state.memoryUi.selected = el.dataset.id || null;
    state.memoryUi.filter = 'all'; state.memoryUi.search = '';
    if (state.view === 'memory') { render(); } else { location.hash = '#/memory'; }
  },
  'memory-add'(el) {
    const type = el.dataset.type || 'Markdown';
    const mem = state.config.Memory = state.config.Memory || { Sources: [], TrackSharing: true, MinimumLineLength: 24, RefreshInterval: '00:01:00' };
    mem.Sources = mem.Sources || [];
    const base = { Markdown: 'vault', ClaudeCode: 'claude-code', KnowledgeGraph: 'memory-graph' }[type];
    let name = base; let n = 1;
    while (mem.Sources.some((x) => (x.Name || '').toLowerCase() === name)) name = base + '-' + (++n);
    mem.Sources.push({ Name: name, Type: type, Path: type === 'ClaudeCode' ? null : '', Exclude: [], Extensions: [], Enabled: true });
    const i = mem.Sources.length - 1;
    state.open.add('mem:' + i);
    state.focusAfter = [type === 'ClaudeCode' ? `[data-bind="Memory.Sources.${i}.Name"]` : `[data-bind="Memory.Sources.${i}.Path"]`, `[data-bind="Memory.Sources.${i}.Name"]`];
    render();
    toast(type === 'ClaudeCode' ? 'Added; apply to scan ~/.claude.' : 'Fill in the path, then apply.', 'ok');
  },
  'memory-remove'(el) {
    const i = +el.dataset.index;
    removeListItem(() => memory().Sources, i, 'mem', `memory source ${(memory().Sources[i] || {}).Name || ''}`.trim(), nextItem('mem', i), [`.item[data-key="mem:${i}"] .item-head`]);
  },
  'trends-window'(el) { state.trends.window = el.dataset.window; state.trends.data = null; state.trends.error = null; render(); },
  'trends-retry'() { state.trends.error = null; if (state.trends.data) loadTrends(); render(); },
  // The figures on screen stay until the new ones arrive, so changing the window or the grouping doesn't blank the page.
  'usage-set'(el) { state.usage[el.dataset.key] = el.dataset.value; render(); loadUsage(); },
  'usage-retry'() { state.usage.error = null; if (state.usage.data) loadUsage(); render(); },
  'budget-add'() {
    const list = (usage().Budgets = usage().Budgets || []);
    list.push({ Name: 'budget-' + (list.length + 1), Model: null, Connector: null, Period: 'Day', MaxTokens: null, MaxCostUsd: 5 });
    state.focusAfter = [`[data-bind="Usage.Budgets.${list.length - 1}.Name"]`];
    render();
  },
  'budget-remove'(el) {
    const i = +el.dataset.index;
    removeListItem(ensure(usage, 'Budgets'), i, null, `budget ${(usage().Budgets[i] || {}).Name || ''}`.trim(), nextRow('Usage.Budgets.#.Name', i, 'budget-add'), [`[data-bind="Usage.Budgets.${i}.Name"]`]);
  },
  async 'audit-load'() { state.audit.entries = null; await loadAudit(); },
  async 'sessions-revoke-all'() {
    const ok = await ask({ title: 'Sign out every session?', body: '<p>Every dashboard session signed in with a token ends, yours included. Tokens keep working; everyone signs in again.</p>', actions: [{ label: 'Sign out every session', value: true, kind: 'danger' }, { label: 'Cancel', value: false, focus: true }] });
    if (!ok) return;
    await api('POST', '/session/revoke-all');
    location.reload();
  },
  async 'history-load'() { state.history.versions = (await api('GET', '/config/history?limit=100')).versions; render(); },
  async 'history-diff'(el) {
    const v = await api('GET', '/config/history/' + el.dataset.id);
    state.history.diff = { id: el.dataset.id, html: lineDiff(JSON.stringify(v.config, null, 2), JSON.stringify(state.config, null, 2)) };
    render();
  },
  async 'history-rollback'(el) {
    const ok = await ask({ title: `Roll back to version ${el.dataset.id}?`, body: `<p>It is applied and saved; the current version stays in the history.${replacesDraft()}</p>`, actions: [{ label: 'Roll back', value: true, kind: 'danger' }, { label: 'Cancel', value: false, focus: true }] });
    if (!ok) return;
    setConfig(await api('POST', `/config/history/${el.dataset.id}/rollback`));
    state.history.diff = null;
    state.history.versions = (await api('GET', '/config/history?limit=100')).versions;
    await refreshStatus(); render(); toast(`Rolled back to version ${el.dataset.id}.`, 'ok');
  },
  async 'reliability-reset'(el) {
    await api('POST', '/reliability/reset', { model: el.dataset.model, route: el.dataset.route || null, capability: el.dataset.capability || null });
    state.reliability = null; await loadReliability(); render(); toast(`${el.dataset.model} is offered again, with a fresh count.`, 'ok');
  },
  async 'tuning-run'() {
    const run = await api('POST', '/tuning/run');
    const note = await afterTuning();
    toast(run.result + (run.saved === false ? ' Applied, not saved: the running configuration has other unsaved changes.' : '') + note, run.changes.length ? 'ok' : undefined);
  },
  async 'tuning-undo'(el) {
    const c = await api('POST', `/tuning/changes/${el.dataset.id}/undo`);
    const note = await afterTuning();
    toast(`${c.kind === 'RouteModel' && !c.version ? 'Trial stopped' : c.saved === false ? 'Undone (applied, not saved)' : 'Undone'}: ${c.summary}. It is not made again for a while.${note}`, 'ok');
  },
  async 'tuning-apply'(el) {
    const c = await api('POST', `/tuning/changes/${el.dataset.id}/apply`);
    const note = await afterTuning();
    toast(`${c.saved === false ? 'Applied, not saved' : 'Applied and saved'}: ${c.summary}.${note}`, 'ok');
  },
  async 'tuning-dismiss'(el) { await api('POST', `/tuning/changes/${el.dataset.id}/dismiss`); await afterTuning(); },
  async 'tuning-undo-all'() {
    const ok = await ask({ title: 'Undo every learned change?', body: '<p>Each setting goes back to what it was before; nothing is learned for those settings for a while.</p>', actions: [{ label: 'Undo all', value: true, kind: 'danger' }, { label: 'Cancel', value: false, focus: true }] });
    if (!ok) return;
    const r = await api('POST', '/tuning/undo-all');
    const note = await afterTuning();
    toast(`Undid ${r.undone} change${r.undone === 1 ? '' : 's'}${r.skipped.length ? '; left ' + r.skipped.join(', ') : ''}.${note}`, 'ok');
  },
  async 'tuning-pause'() { await api('POST', '/tuning/pause'); await afterTuning(); toast('Learning paused.', 'ok'); },
  async 'tuning-resume'() { await api('POST', '/tuning/resume'); await afterTuning(); toast('Learning resumed.', 'ok'); },
  async 'endpoint-check'(el) {
    const name = el.dataset.name;
    state.checking.add(name); render();
    try {
      const r = await api('POST', `/connectors/${encodeURIComponent(name)}/check`);
      toast(`${name}: ${r.messages[0] || r.status}`, r.status === 'Failed' ? 'danger' : r.status === 'Ok' ? 'ok' : '');
    } finally {
      state.checking.delete(name);
      await refreshStatus();
      render();
    }
  },
  async 'reset-health'() { await api('POST', '/health/reset'); await refreshStatus(); toast('Cooldowns cleared.', 'ok'); },

  'tiers-customize'() { state.config.Tiers = tiers().map((t) => ({ Level: t.Level, Name: t.Name, Description: t.Description || null })); state.focusAfter = ['[data-bind="Tiers.0.Name"]']; render(); },
  async 'tiers-defaults'() {
    const before = state.config.Tiers;
    const ok = await ask({ title: 'Use the built-in tiers?', body: `<p>Your ${plural((before || []).length, 'custom tier')} ${(before || []).length === 1 ? 'is' : 'are'} dropped from the draft.</p>`, actions: [{ label: 'Use defaults', value: true, kind: 'danger' }, { label: 'Keep mine', value: false, focus: true }] });
    if (!ok) return;
    state.config.Tiers = null;
    render();
    undoToast('Using the built-in tiers.', () => { state.config.Tiers = before; });
  },
  'tier-add'() {
    const next = Math.max(0, ...(state.config.Tiers || []).map((t) => t.Level)) + 1;
    state.config.Tiers.push({ Level: next, Name: 'Tier ' + next, Description: null });
    state.focusAfter = [`[data-bind="Tiers.${state.config.Tiers.length - 1}.Name"]`];
    render();
  },
  'tier-remove'(el) {
    const i = +el.dataset.index;
    const t = (state.config.Tiers || [])[i] || {};
    removeListItem(ensure(config, 'Tiers'), i, null, `tier ${t.Level} (${t.Name})`, nextRow('Tiers.#.Name', i, 'tier-add'), [`[data-bind="Tiers.${i}.Name"]`]);
  },

  'route-add'() {
    state.config.Routes = state.config.Routes || [];
    state.config.Routes.push({ Name: 'new-route', Tier: 1, RequiredCapabilities: [], Utterances: [], SimilarityThreshold: null, LocalOnly: false, Description: null });
    const i = state.config.Routes.length - 1;
    state.open.add('route:' + i);
    state.focusAfter = [`[data-bind="Routes.${i}.Name"]`];
    render();
  },
  'route-remove'(el) {
    const i = +el.dataset.index;
    removeListItem(ensure(config, 'Routes'), i, 'route', `route ${(state.config.Routes[i] || {}).Name}`, nextItem('route', i), [`.item[data-key="route:${i}"] .item-head`]);
  },
  'route-duplicate'(el) {
    const r = JSON.parse(JSON.stringify(state.config.Routes[+el.dataset.index])); r.Name += '-copy';
    state.config.Routes.push(r);
    const i = state.config.Routes.length - 1;
    state.open.add('route:' + i);
    state.focusAfter = [`[data-bind="Routes.${i}.Name"]`];
    render();
  },
  'model-add'() {
    state.config.Models = state.config.Models || [];
    state.config.Models.push({ Name: 'new-model', Connector: connectorNames()[0] || '', Model: null, Tier: 1, Capabilities: ['chat'], IsLocal: false, ContextWindow: 0, InputCostPerMillionTokens: 0, OutputCostPerMillionTokens: 0, Priority: 0, Enabled: true, Description: null });
    const i = state.config.Models.length - 1;
    state.open.add('model:' + i);
    state.focusAfter = [`[data-bind="Models.${i}.Name"]`];
    render();
  },
  'model-remove'(el) {
    const i = +el.dataset.index;
    removeListItem(ensure(config, 'Models'), i, 'model', `model ${(state.config.Models[i] || {}).Name}`, nextItem('model', i), [`.item[data-key="model:${i}"] .item-head`]);
  },
  'model-duplicate'(el) {
    const m = JSON.parse(JSON.stringify(state.config.Models[+el.dataset.index])); m.Name += '-copy';
    state.config.Models.push(m);
    const i = state.config.Models.length - 1;
    state.open.add('model:' + i);
    state.focusAfter = [`[data-bind="Models.${i}.Name"]`];
    render();
  },
  'connector-add'() {
    let name = 'new-connector'; let n = 1;
    while (state.config.Connectors[name]) name = 'new-connector-' + (++n);
    state.config.Connectors[name] = { Type: 'OpenAICompatible', BaseAddress: 'http://localhost:1234', RequestPath: '/v1/chat/completions', ApiKey: null, Headers: {}, ForwardHeaders: [], AnthropicVersion: '2023-06-01', PromptCaching: 'Auto', CacheTtl: '5m', Preset: 'Custom', Executable: null, Arguments: null, PromptInput: 'Stdin', ToolCalling: 'None', QuotaErrorPatterns: [], EnvironmentVariables: {}, RemoveEnvironmentVariables: null, MaxArgumentLength: 24000, MaxConcurrency: 2, AllowRemoteClients: false, Timeout: '00:00:00' };
    state.open.add('conn:' + name);
    state.focusAfter = [`[data-action-input="connector-rename"][data-name="${CSS.escape(name)}"]`];
    render();
  },
  /* A connector models use: remove them too, or keep them (they then need another connector before Apply). */
  async 'connector-remove'(el) {
    const name = el.dataset.name;
    const uses = (m) => (m.Connector || '').toLowerCase() === name.toLowerCase();
    const users = (state.config.Models || []).filter(uses);
    let cascade = false;
    if (users.length) {
      const choice = await ask({
        title: `Remove connector ${name}?`,
        body: `<p>${plural(users.length, 'model')} ${users.length === 1 ? 'uses' : 'use'} it: <b>${users.map((m) => h(m.Name)).join(', ')}</b>.</p><p class="muted small">Models you keep need another connector before the draft can be applied.</p>`,
        actions: [{ label: users.length === 1 ? 'Remove it too' : 'Remove them too', value: 'all', kind: 'danger' }, { label: users.length === 1 ? 'Keep the model' : 'Keep the models', value: 'keep' }, { label: 'Cancel', value: null, kind: 'ghost', focus: true }],
      });
      if (!choice) return;
      cascade = choice === 'all';
    }
    const names = Object.keys(state.config.Connectors);
    const at = names.indexOf(name);
    const undo = [];
    if (cascade) state.config.Models.map((m, i) => (uses(m) ? i : -1)).filter((i) => i >= 0).reverse().forEach((i) => undo.push(spliceItem(ensure(config, 'Models'), i, 'model')));
    undo.push(removeKey(config, 'Connectors', name));
    const wasOpen = state.open.delete('conn:' + name);
    const near = [names[at + 1], names[at - 1]].filter(Boolean).map((n) => `.item[data-key="conn:${CSS.escape(n)}"] .item-head`);
    state.focusAfter = [...near, '#view h1'];
    render();
    undoToast(`Removed connector ${name}${cascade ? ` and ${plural(users.length, 'model')}` : ''}.`, () => {
      undo.reverse().forEach((u) => u());
      if (wasOpen) state.open.add('conn:' + name);
    }, [`.item[data-key="conn:${CSS.escape(name)}"] .item-head`]);
  },
  'secret-change'(el) { setPath(state.config, el.dataset.path, ''); render(); },
  'secret-clear'(el) { setPath(state.config, el.dataset.path, null); render(); },
  // The models a connector offers, for one model's "Provider model id": into its datalist, with a line under the field.
  async discover(el) {
    const name = el.dataset.connector;
    const field = el.closest('.field');
    let out = field && field.querySelector('.discover-result');
    if (field && !out) { out = document.createElement('span'); out.className = 'hint discover-result'; out.setAttribute('role', 'status'); field.appendChild(out); }
    if (out) out.textContent = `Asking ${name}…`;
    try {
      const r = await api('POST', `/connectors/${encodeURIComponent(name)}/discover`);
      const ids = r.models || (r.lineup || []).map((e) => e.id);
      if (el.dataset.index != null) {
        const dl = $('#models-' + el.dataset.index);
        if (dl) dl.innerHTML = ids.map((m) => `<option value="${h(m)}">`).join('');
      }
      const text = ids.length ? `${plural(ids.length, 'model')}: ${ids.join(', ')}` : 'No models reported.';
      if (out) out.textContent = (r.note ? r.note + ' ' : '') + text; else toast(text, 'ok');
    } catch (e) { if (out) out.textContent = "Couldn't discover models: " + e.message; else toast("Couldn't discover models: " + e.message, 'danger'); }
  },
  // Discover models asks with the draft's settings (setup.js), so a connector added a moment ago works without Apply.
  async 'lineup-discover'(el) { await discoverDraft(el.dataset.connector); },
  'lineup-close'(el) { state.lineups = state.lineups.filter((l) => l.connector !== el.dataset.connector); render(); },
  'lineup-all'(el) {
    const l = state.lineups[+el.dataset.index];
    const rows = l.lineup.filter((e) => e.suggestion && !e.inCatalog);
    const on = !rows.every((e) => e.selected);
    rows.forEach((e) => { e.selected = on; });
    render();
  },
  'lineup-add'(el) {
    const l = state.lineups[+el.dataset.index];
    const models = state.config.Models = state.config.Models || [];
    const taken = new Set(models.map((m) => (m.Name || '').toLowerCase()));
    const added = [];
    l.lineup.filter((e) => e.selected && e.suggestion && !e.inCatalog).forEach((e) => {
      const model = JSON.parse(JSON.stringify(e.suggestion));
      let name = (model.Name || '').trim() || e.id; let n = 1;
      while (taken.has(name.toLowerCase())) name = `${model.Name}-${++n}`;
      model.Name = name; taken.add(name.toLowerCase());
      models.push(model);
      Object.assign(e, { inCatalog: name, selected: false });
      added.push(name);
    });
    render();
    toast(added.length ? `Added ${added.join(', ')} to the draft. Apply to use ${added.length === 1 ? 'it' : 'them'}.` : 'Nothing selected.', added.length ? 'ok' : '');
  },
  'rule-add'() { state.config.ComplexityTiers.push({ MinScore: 0.5, Tier: 2 }); state.focusAfter = [`[data-bind="ComplexityTiers.${state.config.ComplexityTiers.length - 1}.MinScore"]`]; render(); },
  'rule-remove'(el) {
    const i = +el.dataset.index;
    removeListItem(ensure(config, 'ComplexityTiers'), i, null, 'the rule', nextRow('ComplexityTiers.#.MinScore', i, 'rule-add'), [`[data-bind="ComplexityTiers.${i}.MinScore"]`]);
  },
  async 'rules-defaults'() {
    const before = state.config.ComplexityTiers;
    const ok = await ask({ title: 'Use the default complexity rules?', body: `<p>Your ${plural((before || []).length, 'rule')} ${(before || []).length === 1 ? 'is' : 'are'} dropped from the draft; the defaults are 0.45 → T2, 0.65 → T3, 0.85 → T4.</p>`, actions: [{ label: 'Use defaults', value: true, kind: 'danger' }, { label: 'Keep mine', value: false, focus: true }] });
    if (!ok) return;
    state.config.ComplexityTiers = null;
    render();
    undoToast('Using the default complexity rules.', () => { state.config.ComplexityTiers = before; });
  },
  'rules-customize'() { state.config.ComplexityTiers = [{ MinScore: 0.45, Tier: 2 }, { MinScore: 0.65, Tier: 3 }, { MinScore: 0.85, Tier: 4 }]; render(); },

  // Model aliases and rate limits are edited as rows (draft.js): order kept, duplicates flagged rather than merged.
  'alias-add'() {
    const rows = aliasRows();
    const taken = new Set(rows.map((r) => r.key.trim().toLowerCase()));
    let k = '*pattern*'; let n = 1; while (taken.has(k)) k = `*pattern${++n}*`;
    rows.push({ key: k, value: 'auto' });
    writeAliases();
    state.focusAfter = [`[data-row="alias-${rows.length - 1}"]`];
    render();
  },
  // Claude Code's names: adds the ones missing, never changes a target you set, and says what it left alone.
  'alias-claude'() {
    const defaults = { 'opus-pinned': 'family:opus', '*haiku*': 'auto:cheapest', '*sonnet*': 'auto', '*opus*': 'auto', 'claude-*': 'auto', 'gpt-*': 'auto', 'o*-mini': 'auto:cheapest' };
    const rows = aliasRows();
    const have = new Map(rows.map((r) => [r.key.trim().toLowerCase(), r]));
    const added = []; const kept = [];
    Object.entries(defaults).forEach(([k, v]) => {
      const r = have.get(k);
      if (!r) { rows.push({ key: k, value: v }); added.push(k); } else if (r.value !== v) kept.push(`${k} → ${r.value}`);
    });
    writeAliases();
    render();
    const keptText = kept.length ? `kept your ${kept.join(', ')} (the default differs)` : '';
    toast(added.length ? `Added ${plural(added.length, 'alias', 'aliases')}${keptText ? '; ' + keptText : ''}.` : keptText ? `Nothing added: ${keptText}.` : 'Claude Code\'s aliases are all there already.', 'ok');
  },
  'alias-remove'(el) {
    const i = +el.dataset.index;
    const [row] = aliasRows().splice(i, 1);
    writeAliases();
    state.focusAfter = [`[data-row="alias-${i}"]`, `[data-row="alias-${i - 1}"]`, '[data-action="alias-add"]'];
    render();
    undoToast(`Removed alias ${row.key}.`, () => { aliasRows().splice(i, 0, row); writeAliases(); }, [`[data-row="alias-${i}"]`]);
  },
  'alias-up'(el) { moveAlias(+el.dataset.index, -1); },
  'alias-down'(el) { moveAlias(+el.dataset.index, 1); },
  'ratelimit-add'() {
    const rows = limitRows();
    const taken = new Set(rows.map((r) => r.key.trim().toLowerCase()));
    const k = !taken.has('*') ? '*' : (Object.keys(state.config.Middleware.GatewayKeys || {}).find((c) => !taken.has(c.toLowerCase())) || 'client');
    rows.push({ key: k, value: { RequestsPerMinute: 60, TokensPerMinute: null } });
    writeLimits();
    state.focusAfter = [`[data-row="limit-${rows.length - 1}"]`];
    render();
  },
  'ratelimit-remove'(el) {
    const i = +el.dataset.index;
    const [row] = limitRows().splice(i, 1);
    writeLimits();
    state.focusAfter = [`[data-row="limit-${i}"]`, `[data-row="limit-${i - 1}"]`, '[data-action="ratelimit-add"]'];
    render();
    undoToast(`Removed the limit for ${row.key || 'a client'}.`, () => { limitRows().splice(i, 0, row); writeLimits(); }, [`[data-row="limit-${i}"]`]);
  },
  async 'embeddings-test'() {
    const out = $('#embeddings-result'); out.textContent = 'testing…';
    try { const r = await api('POST', '/embeddings/test', { text: 'The quick brown fox' }); out.innerHTML = r.ok ? `<span class="badge ok">ok</span> ${r.dimensions} dimensions in ${r.milliseconds} ms` : `<span class="badge danger">failed</span> ${h(r.error)}`; }
    catch (e) { out.textContent = e.message; }
  },
  'embeddings-threshold'(el) { state.config.DefaultSimilarityThreshold = parseFloat(el.dataset.value); render(); toast('Threshold set in the draft: Apply to use it.', 'ok'); },
  // The JSON editor's text is kept in state (jsonText): a render does not reset it, and replacing typed text asks first.
  async 'json-load'() { if (!await confirmOverwrite()) return; state.jsonText = null; render(); },
  'json-use'() {
    const text = state.jsonText != null ? state.jsonText : $('#json-editor').value;
    try { state.config = JSON.parse(text); } catch (e) { toast('Invalid JSON: ' + e.message, 'danger'); return; }
    state.jsonText = null; clearProblems(); render(); toast('Draft replaced from JSON. Apply to use it.', 'ok');
  },
  'json-download'() { const blob = new Blob([JSON.stringify(state.config, null, 2)], { type: 'application/json' }); const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = 'semantic-router.json'; a.click(); },
  async 'json-baseline'() {
    if (!await confirmOverwrite()) return;
    const b = await api('GET', '/config/baseline');
    state.jsonText = JSON.stringify(b.config, null, 2);
    render();
    toast('Showing the application baseline (not the draft).');
  },
  copy(el) { const code = el.parentElement.querySelector('code').textContent; navigator.clipboard.writeText(code).then(() => toast('Copied.', 'ok')); },
  'copy-text'(el) { navigator.clipboard.writeText(el.dataset.text).then(() => toast('Copied.', 'ok')); },

  async 'eval-run'(el) {
    const ev = state.evaluation; ev.busy = true; ev.addFor = null; render();
    try { ev.report = await api('POST', '/evaluate' + (el.dataset.calibrate ? '?calibrate=true' : ''), { text: ev.text || '' }); }
    catch (e) { toast("Couldn't run the evaluation: " + e.message, 'danger'); }
    ev.busy = false; render();
  },
  async 'eval-save'() {
    const text = state.evaluation.text || '';
    const r = await api('PUT', '/evaluation/set', { text });
    state.evaluation.count = r.count; state.evaluation.location = r.location; state.evaluation.savedText = text; render();
    toast(`Saved ${plural(r.count, 'prompt')}${r.location ? ' to ' + r.location : ''}.`, 'ok');
  },
  'eval-add-open'(el) { state.evaluation.addFor = +el.dataset.index; render(); },
  'eval-add-cancel'() { state.evaluation.addFor = null; render(); },
  async 'eval-add-save'(el) {
    const m = state.evaluation.report.misroutes[+el.dataset.index];
    const routeName = $('#add-route').value;
    const r = await api('POST', `/routes/${encodeURIComponent(routeName)}/utterances`, { utterance: m.prompt });
    const result = adoptConfig(await api('GET', '/config'));
    state.evaluation.addFor = null;
    toast(`Added to ${r.route}, ${savedWord(r)}: it now classifies as tier ${r.tier}${r.matchedRoute ? ' via ' + r.matchedRoute : ''}.${draftNote(result)} Re-running…`, 'ok');
    await actions['eval-run']({ dataset: { calibrate: state.evaluation.report && state.evaluation.report.calibration ? '1' : '' } });
  },
  'eval-use-calibration'() {
    const c = state.evaluation.report.calibration;
    state.config.DefaultSimilarityThreshold = c.recommended.threshold;
    c.routes.forEach((x) => { const r = (state.config.Routes || []).find((y) => y.Name.toLowerCase() === x.route.toLowerCase()); if (r) r.SimilarityThreshold = x.recommended; });
    render();
    toast('The recommended thresholds are in the draft: Apply to use them.', 'ok');
  },
  async 'eval-health'() {
    try { state.evaluation.health = await api('GET', '/routes/health'); } catch (e) { toast("Couldn't check the routes: " + e.message, 'danger'); }
    render();
  },
  async 'eval-compare'() {
    const f = state.evaluation.form;
    const embeddings = f.provider === 'Hashing'
      ? { Provider: 'Hashing', Dimensions: f.dimensions || 512 }
      : { Provider: 'OpenAICompatible', Endpoint: f.endpoint || null, Model: f.model || null, ApiKey: f.apiKey || null };
    state.evaluation.compare = null; toast('Comparing…');
    try { state.evaluation.compare = await api('POST', '/evaluate/compare', { embeddings, text: state.evaluation.text || '' }); } catch (e) { toast("Couldn't compare: " + e.message, 'danger'); }
    render();
  },
  'label-open'(el) {
    const e = state.activity.find((x) => String(x.id) === el.dataset.id);
    if (!e) return;
    const truncated = e.promptPreview.endsWith('…');
    const routes = state.config.Routes || [];
    const sameTier = routes.find((r) => r.Tier === e.tier);
    state.label = { id: e.id, prompt: truncated ? e.promptPreview.slice(0, -1) : e.promptPreview, truncated, tier: e.tier, route: '', routedTier: e.tier, routedRoute: e.route, localOnly: e.localOnly, toSet: true, toRoute: false, utteranceRoute: sameTier ? sameTier.Name : '', newRoute: '' };
    render(); const form = $('#label-form'); if (form) form.scrollIntoView({ block: 'nearest' });
  },
  'label-cancel'() { state.label = null; render(); },
  async 'label-save'() {
    const l = state.label;
    if (!l.prompt.trim()) { toast('The prompt is empty.', 'danger'); return; }
    const done = [];
    if (l.toSet) {
      const r = await api('POST', '/evaluation/cases', { prompt: l.prompt, tier: l.tier, route: l.route || null, localOnly: !!l.localOnly, note: 'labelled from activity #' + l.id });
      // Unsaved edits to the set on the Evaluate page stay; the new case is added to them, as it was to the stored set.
      if (evaluationEdited()) {
        const line = JSON.stringify(Object.fromEntries(Object.entries({ prompt: l.prompt, tier: l.tier, route: l.route || null, localOnly: l.localOnly || undefined, note: 'labelled from activity #' + l.id }).filter(([, v]) => v != null)));
        state.evaluation.text = state.evaluation.text.replace(/\n*$/, '\n') + line;
        state.evaluation.savedText = state.evaluation.savedText.replace(/\n*$/, '\n') + line;
        state.evaluation.count = r.count;
      } else state.evaluation.text = null;
      done.push(`Saved to the evaluation set (${plural(r.count, 'prompt')}).`);
    }
    if (l.toRoute) {
      const name = l.utteranceRoute || l.newRoute.trim();
      if (!name) { toast('Pick a route or name a new one.', 'danger'); return; }
      const r = await api('POST', `/routes/${encodeURIComponent(name)}/utterances`, { utterance: l.prompt, tier: l.utteranceRoute ? null : l.tier });
      const result = adoptConfig(await api('GET', '/config'));
      done.push(`Added to route ${r.route}, ${savedWord(r)}: it now gets tier ${r.tier}${r.matchedRoute ? ' via ' + r.matchedRoute : ''}.${draftNote(result)}`);
    }
    state.label = null; render();
    toast(done.length ? done.join(' ') : 'Nothing to save.', 'ok');
  },

  'profile-add'() {
    state.config.Profiles = state.config.Profiles || {};
    let name = 'profile'; let n = 1; while (state.config.Profiles[name]) name = 'profile-' + (++n);
    state.config.Profiles[name] = { AllowedModels: [], DeniedModels: [], AllowedConnectors: [], DeniedConnectors: [], Strategy: null, MinTier: null, MaxTier: null, LocalOnly: false, Description: null };
    state.open.add('profile:' + name);
    state.focusAfter = [`[data-action-input="profile-rename"][data-name="${CSS.escape(name)}"]`];
    render();
  },
  /* A profile that rules, virtual models, client mappings or budgets name: remove those too, or keep them. */
  async 'profile-remove'(el) {
    const name = el.dataset.name;
    const m = state.config.Middleware = state.config.Middleware || {};
    const u = state.config.Usage || {};
    const refs = [
      [(m.ProfileRules || []).filter((r) => r.Profile === name).length, 'rule'],
      [Object.values(m.VirtualModels || {}).filter((v) => v.Profile === name).length, 'virtual model'],
      [Object.values(m.ClientProfiles || {}).filter((p) => p === name).length, 'client mapping'],
      [(u.Budgets || []).filter((b) => b.Profile === name).length, 'budget'],
    ].filter(([n]) => n);
    const total = refs.reduce((n, [x]) => n + x, 0);
    let cascade = false;
    if (total) {
      const choice = await ask({
        title: `Remove profile ${name}?`,
        body: `<p>${refs.map(([n, what]) => plural(n, what)).join(', ')} ${total === 1 ? 'names' : 'name'} it.</p><p class="muted small">What you keep must name another profile before the draft can be applied.</p>`,
        actions: [{ label: total === 1 ? 'Remove it too' : 'Remove them too', value: 'all', kind: 'danger' }, { label: 'Keep them', value: 'keep' }, { label: 'Cancel', value: null, kind: 'ghost', focus: true }],
      });
      if (!choice) return;
      cascade = choice === 'all';
    }
    const names = Object.keys(state.config.Profiles);
    const at = names.indexOf(name);
    const undo = [];
    if (cascade) {
      const saved = JSON.stringify({ rules: m.ProfileRules, virtual: m.VirtualModels, clients: m.ClientProfiles, budgets: u.Budgets });
      if (m.ProfileRules) m.ProfileRules = m.ProfileRules.filter((r) => r.Profile !== name);
      if (m.VirtualModels) m.VirtualModels = Object.fromEntries(Object.entries(m.VirtualModels).filter(([, v]) => v.Profile !== name));
      if (m.ClientProfiles) m.ClientProfiles = Object.fromEntries(Object.entries(m.ClientProfiles).filter(([, p]) => p !== name));
      if (u.Budgets) u.Budgets = u.Budgets.filter((b) => b.Profile !== name);
      undo.push(() => {
        const s = JSON.parse(saved); const mw = state.config.Middleware = state.config.Middleware || {};
        mw.ProfileRules = s.rules; mw.VirtualModels = s.virtual; mw.ClientProfiles = s.clients;
        if (s.budgets) usage().Budgets = s.budgets;
      });
    }
    undo.push(removeKey(config, 'Profiles', name));
    const wasOpen = state.open.delete('profile:' + name);
    state.focusAfter = [...[names[at + 1], names[at - 1]].filter(Boolean).map((n) => `.item[data-key="profile:${CSS.escape(n)}"] .item-head`), '#view h1'];
    render();
    undoToast(`Removed profile ${name}${cascade ? ` and ${plural(total, 'setting')} naming it` : ''}.`, () => {
      undo.reverse().forEach((f) => f());
      if (wasOpen) state.open.add('profile:' + name);
    }, [`.item[data-key="profile:${CSS.escape(name)}"] .item-head`]);
  },
  'rule-profile-add'() { const m = state.config.Middleware; (m.ProfileRules = m.ProfileRules || []).push({ Profile: Object.keys(state.config.Profiles)[0], Clients: [], Projects: [], ToolsInclude: [], ToolsExclude: [] }); render(); },
  'rule-profile-remove'(el) { state.config.Middleware.ProfileRules.splice(+el.dataset.index, 1); render(); },
  'virtual-add'() { const m = state.config.Middleware; m.VirtualModels = m.VirtualModels || {}; let name = 'fast'; let n = 1; while (m.VirtualModels[name]) name = 'model-' + (++n); m.VirtualModels[name] = { Profile: null, Strategy: 'CheapestCapable', Description: null }; render(); },
  'virtual-remove'(el) { delete state.config.Middleware.VirtualModels[el.dataset.name]; render(); },
  async 'profiles-compare'() {
    const t = state.profileTester; const prompt = (t.prompt || '').trim();
    if (!prompt) { toast('Type a prompt first.', 'danger'); return; }
    if (isDirty()) toast('The tester uses the applied configuration: apply your changes to test them.');
    t.busy = true; render();
    const profiles = [null, ...Object.keys((state.status && state.config.Profiles) || {})];
    t.results = await Promise.all(profiles.map(async (profile) => {
      try { return { profile, plan: await api('POST', '/classify', { prompt, profile }) }; } catch (e) { return { profile, error: e.message }; }
    }));
    t.busy = false; render();
  },

  async 'tester-classify'() {
    readTester();
    state.tester.busy = true; state.tester.reply = null; render();
    try { state.tester.result = await api('POST', '/classify', { prompt: state.tester.prompt, model: state.tester.model }); }
    catch (e) { toast("Couldn't classify: " + e.message, 'danger'); }
    state.tester.busy = false; render();
  },
  async 'tester-send'() {
    // Sending calls a model for real: like the other actions that spend, it needs the admin role.
    if (readOnly()) { toast('Sending a prompt needs the admin token.', 'danger'); return; }
    readTester();
    state.tester.busy = true; state.tester.result = null; render();
    const started = performance.now();
    try {
      const path = (state.status.gateway.pathPrefixes[0] || '/v1/chat/completions');
      const res = await fetch(location.origin + path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ model: state.tester.model, messages: [{ role: 'user', content: state.tester.prompt }] }) });
      const text = await res.text();
      let reply = text;
      try { const j = JSON.parse(text); reply = (j.choices && j.choices[0] && (j.choices[0].message.content || JSON.stringify(j.choices[0].message.tool_calls))) || j.detail || text; } catch { /* raw */ }
      const headers = []; res.headers.forEach((v, k) => { if (k.toLowerCase().startsWith('x-prompt-route')) headers.push([k.replace(/(^|-)(\w)/g, (m0, p, c) => p + c.toUpperCase()), v]); });
      state.tester.reply = { text: res.ok ? reply : `HTTP ${res.status}: ${reply}`, headers, ms: Math.round(performance.now() - started) };
    } catch (e) { toast("Couldn't send: " + e.message, 'danger'); }
    state.tester.busy = false; render(); refreshStatus();
  },
};

function moveAlias(i, delta) {
  const rows = aliasRows();
  const j = i + delta;
  if (j < 0 || j >= rows.length) return;
  [rows[i], rows[j]] = [rows[j], rows[i]];
  writeAliases();
  state.focusAfter = [`[data-action="alias-${delta < 0 ? 'up' : 'down'}"][data-index="${j}"]:not([disabled])`, `[data-row="alias-${j}"]`];
  render();
}

export function readTester() {
  const p = $('#tester-prompt'); const m = $('#tester-model');
  if (p) state.tester.prompt = p.value;
  if (m) state.tester.model = m.value;
}

/*
 * Apply the draft. It names the version it started from (baseVersion) and the router process that ran it (instance,
 * when the router sends one: the version counter starts again after a restart): when someone else applied a
 * configuration in the meantime, or the router restarted, it refuses (409) and the conflict notice offers Reload,
 * Compare or Apply mine anyway. Apply mine anyway names the version (and instance) the notice was about.
 */
export async function applyConfig(persist, baseVersion, baseInstance) {
  const local = localProblems();
  if (local.length) { showProblems(local.map((x) => x.message), local, true); return; }
  const base = baseVersion != null ? baseVersion : state.meta.version;
  const instance = baseVersion != null ? baseInstance : state.meta.instance;
  const query = `persist=${persist}${base != null ? '&baseVersion=' + encodeURIComponent(base) : ''}${base != null && instance != null ? '&instance=' + encodeURIComponent(instance) : ''}`;
  try {
    const r = await api('PUT', `/config?${query}`, state.config);
    setConfig(r);
    clearProblems();
    await refreshStatus();
    render();
    toast(persist ? (r.store ? `Applied and saved to ${r.store}.` : 'Applied (no store registered: not saved).') : 'Applied to the running router (not saved).', 'ok');
    if (state.view === 'memory') loadMemory(true);
    else state.memory = null;
  } catch (e) {
    if (e.status === 401) return; // signing in again keeps the draft
    if (e.status === 409) {
      const d = e.data || {};
      noteConflict({ currentVersion: d.currentVersion, instance: d.instance, actor: d.actor, at: d.at, source: d.source, persist });
      return;
    }
    const d = e.data || {};
    const errors = d.errors && d.errors.length ? d.errors : [e.message];
    showProblems(errors, issuesOf(errors, d.issues), true);
  }
}

/* A learned change was applied or undone: the configuration changed too. The draft is kept (rebased when it can be). */
async function afterTuning() {
  const result = adoptConfig(await api('GET', '/config'));
  await loadTuning();
  await refreshStatus();
  render();
  return draftNote(result);
}
