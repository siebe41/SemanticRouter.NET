/* Semantic Router dashboard: Actions behind data-action buttons, and applying configuration. */
import { lineDiff } from './config.js';
import { $, api, connectorNames, h, isDirty, lineupFor, setPath, state, tiers, toast } from './core.js';
import { loadTuning } from './learned.js';
import { loadReliability } from './reliability.js';
import { graph, loadMemory, updateMemorySide } from './memory.js';
import { refreshStatus, render, setConfig } from './shell.js';

/* ---------------------------------------------------------------------------------------------
   Actions
   --------------------------------------------------------------------------------------------- */
export const actions = {
  toggle(el) {
    const k = el.dataset.key; if (state.open.has(k)) state.open.delete(k); else state.open.add(k);
    $(`.item[data-key="${CSS.escape(k)}"]`).classList.toggle('open', state.open.has(k));
    el.setAttribute('aria-expanded', String(state.open.has(k)));
  },
  refresh() { refreshStatus(); },

  async validate() {
    const r = await api('POST', '/config/validate', state.config);
    state.errors = r.valid ? null : r.errors;
    render();
    toast(r.valid ? 'Configuration is valid.' : `${r.errors.length} problem(s).`, r.valid ? 'ok' : 'danger');
  },
  async apply() { await applyConfig(false); },
  async 'apply-save'() { await applyConfig(true); },
  discard() { state.config = JSON.parse(state.original); state.errors = null; render(); },
  async 'config-reset'() {
    if (!confirm('Reset the running configuration to the application baseline? Unsaved edits are lost; the stored file is left in place.')) return;
    setConfig(await api('POST', '/config/reset'));
    await refreshStatus();
    render();
    toast('Reset to baseline.', 'ok');
  },
  async 'memory-refresh'() { await loadMemory(true); },
  async 'memory-reset'() {
    if (!confirm('Forget which notes were sent to which models? The notes themselves are not touched.')) return;
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
    const memory = state.config.Memory = state.config.Memory || { Sources: [], TrackSharing: true, MinimumLineLength: 24, RefreshInterval: '00:01:00' };
    memory.Sources = memory.Sources || [];
    const base = { Markdown: 'vault', ClaudeCode: 'claude-code', KnowledgeGraph: 'memory-graph' }[type];
    let name = base; let n = 1;
    while (memory.Sources.some((x) => (x.Name || '').toLowerCase() === name)) name = base + '-' + (++n);
    memory.Sources.push({ Name: name, Type: type, Path: type === 'ClaudeCode' ? null : '', Exclude: [], Extensions: [], Enabled: true });
    state.open.add('mem:' + (memory.Sources.length - 1));
    render();
    toast(type === 'ClaudeCode' ? 'Added; apply to scan ~/.claude.' : 'Fill in the path, then apply.', 'ok');
  },
  'memory-remove'(el) { state.config.Memory.Sources.splice(+el.dataset.index, 1); render(); },
  'trends-window'(el) { state.trends.window = el.dataset.window; state.trends.data = null; render(); },
  'usage-set'(el) { state.usage[el.dataset.key] = el.dataset.value; state.usage.data = null; render(); },
  'budget-add'() { state.config.Usage = state.config.Usage || {}; (state.config.Usage.Budgets = state.config.Usage.Budgets || []).push({ Name: 'budget-' + (state.config.Usage.Budgets.length + 1), Model: null, Connector: null, Period: 'Day', MaxTokens: null, MaxCostUsd: 5 }); render(); },
  'budget-remove'(el) { state.config.Usage.Budgets.splice(+el.dataset.index, 1); render(); },
  async 'history-load'() { state.history.versions = (await api('GET', '/config/history?limit=100')).versions; render(); },
  async 'history-diff'(el) {
    const v = await api('GET', '/config/history/' + el.dataset.id);
    state.history.diff = { id: el.dataset.id, html: lineDiff(JSON.stringify(v.config, null, 2), JSON.stringify(state.config, null, 2)) };
    render();
  },
  async 'history-rollback'(el) {
    if (!confirm(`Roll back to version ${el.dataset.id}? It is applied and saved; the current version stays in the history.`)) return;
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
    await afterTuning(); toast(run.result, run.changes.length ? 'ok' : undefined);
  },
  async 'tuning-undo'(el) {
    const c = await api('POST', `/tuning/changes/${el.dataset.id}/undo`);
    await afterTuning(); toast(`${c.kind === 'RouteModel' && !c.version ? 'Trial stopped' : 'Undone'}: ${c.summary}. It is not made again for a while.`, 'ok');
  },
  async 'tuning-apply'(el) { const c = await api('POST', `/tuning/changes/${el.dataset.id}/apply`); await afterTuning(); toast(`Applied: ${c.summary}.`, 'ok'); },
  async 'tuning-dismiss'(el) { await api('POST', `/tuning/changes/${el.dataset.id}/dismiss`); await afterTuning(); },
  async 'tuning-undo-all'() {
    if (!confirm('Undo every change the router learned? Each setting goes back to what it was before; nothing is learned for those settings for a while.')) return;
    const r = await api('POST', '/tuning/undo-all');
    await afterTuning(); toast(`Undid ${r.undone} change${r.undone === 1 ? '' : 's'}${r.skipped.length ? '; left ' + r.skipped.join(', ') : ''}.`, 'ok');
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

  'tiers-customize'() { state.config.Tiers = tiers().map((t) => ({ Level: t.Level, Name: t.Name, Description: t.Description || null })); render(); },
  'tiers-defaults'() { state.config.Tiers = null; render(); },
  'tier-add'() { const next = Math.max(0, ...(state.config.Tiers || []).map((t) => t.Level)) + 1; state.config.Tiers.push({ Level: next, Name: 'Tier ' + next, Description: null }); render(); },
  'tier-remove'(el) { state.config.Tiers.splice(+el.dataset.index, 1); render(); },

  'route-add'() { state.config.Routes = state.config.Routes || []; state.config.Routes.push({ Name: 'new-route', Tier: 1, RequiredCapabilities: [], Utterances: [], SimilarityThreshold: null, LocalOnly: false, Description: null }); state.open.add('route:' + (state.config.Routes.length - 1)); render(); },
  'route-remove'(el) { state.config.Routes.splice(+el.dataset.index, 1); render(); },
  'route-duplicate'(el) { const r = JSON.parse(JSON.stringify(state.config.Routes[+el.dataset.index])); r.Name += '-copy'; state.config.Routes.push(r); render(); },
  'model-add'() { state.config.Models = state.config.Models || []; state.config.Models.push({ Name: 'new-model', Connector: connectorNames()[0] || '', Model: null, Tier: 1, Capabilities: ['chat'], IsLocal: false, ContextWindow: 0, InputCostPerMillionTokens: 0, OutputCostPerMillionTokens: 0, Priority: 0, Enabled: true, Description: null }); state.open.add('model:' + (state.config.Models.length - 1)); render(); },
  'model-remove'(el) { state.config.Models.splice(+el.dataset.index, 1); render(); },
  'model-duplicate'(el) { const m = JSON.parse(JSON.stringify(state.config.Models[+el.dataset.index])); m.Name += '-copy'; state.config.Models.push(m); render(); },
  'connector-add'() {
    let name = 'new-connector'; let n = 1;
    while (state.config.Connectors[name]) name = 'new-connector-' + (++n);
    state.config.Connectors[name] = { Type: 'OpenAICompatible', BaseAddress: 'http://localhost:1234', RequestPath: '/v1/chat/completions', ApiKey: null, Headers: {}, ForwardHeaders: [], AnthropicVersion: '2023-06-01', Preset: 'Custom', Executable: null, Arguments: null, PromptInput: 'Stdin', ToolCalling: 'None', QuotaErrorPatterns: [], EnvironmentVariables: {}, RemoveEnvironmentVariables: null, MaxArgumentLength: 24000, MaxConcurrency: 2, AllowRemoteClients: false, Timeout: '00:00:00' };
    state.open.add('conn:' + name); render();
  },
  'connector-remove'(el) { delete state.config.Connectors[el.dataset.name]; render(); },
  'secret-change'(el) { setPath(state.config, el.dataset.path, ''); render(); },
  'secret-clear'(el) { setPath(state.config, el.dataset.path, null); render(); },
  async discover(el) {
    const name = el.dataset.connector;
    const out = $('#discover-' + CSS.escape(name));
    try {
      const r = await api('POST', `/connectors/${encodeURIComponent(name)}/discover`);
      if (el.dataset.index != null) {
        const dl = $('#models-' + el.dataset.index);
        if (dl) dl.innerHTML = r.models.map((m) => `<option value="${h(m)}">`).join('');
      }
      const text = r.models.length ? `${r.models.length} model(s): ${r.models.join(', ')}` : 'No models reported.';
      if (out) out.textContent = (r.note ? r.note + ' ' : '') + text; else toast(text, 'ok');
    } catch (e) { if (out) out.textContent = 'Discovery failed: ' + e.message; else toast('Discovery failed: ' + e.message, 'danger'); }
  },
  async 'lineup-discover'(el) {
    const connector = el.dataset.connector;
    let l = lineupFor(connector);
    if (!l) { l = { connector }; state.lineups.push(l); }
    Object.assign(l, { busy: true, error: null });
    render();
    try {
      const r = await api('POST', `/connectors/${encodeURIComponent(connector)}/discover`);
      Object.assign(l, r, { busy: false, lineup: r.lineup.map((e) => ({ ...e, selected: e.recommended })) });
    } catch (e) {
      Object.assign(l, { busy: false, error: e.status === 404 && isDirty() ? `${e.message} Apply the draft first: discovery asks the running router's connectors.` : e.message });
    }
    render();
  },
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
  'rule-add'() { state.config.ComplexityTiers.push({ MinScore: 0.5, Tier: 2 }); render(); },
  'rule-remove'(el) { state.config.ComplexityTiers.splice(+el.dataset.index, 1); render(); },
  'rules-defaults'() { state.config.ComplexityTiers = null; render(); },
  'rules-customize'() { state.config.ComplexityTiers = [{ MinScore: 0.45, Tier: 2 }, { MinScore: 0.65, Tier: 3 }, { MinScore: 0.85, Tier: 4 }]; render(); },
  'alias-add'() { state.config.Middleware.ModelAliases = state.config.Middleware.ModelAliases || {}; let k = '*pattern*'; let n = 1; while (k in state.config.Middleware.ModelAliases) k = `*pattern${++n}*`; state.config.Middleware.ModelAliases[k] = 'auto'; render(); },
  'alias-claude'() { const a = state.config.Middleware.ModelAliases = state.config.Middleware.ModelAliases || {}; Object.assign(a, { '*haiku*': 'auto:cheapest', '*sonnet*': 'auto', '*opus*': 'auto:best', 'claude-*': 'auto', 'gpt-*': 'auto', 'o*-mini': 'auto:cheapest' }); render(); },
  'ratelimit-add'() { const limits = state.config.Middleware.RateLimits = state.config.Middleware.RateLimits || {}; const k = '*' in limits ? (Object.keys(state.config.Middleware.GatewayKeys || {}).find((c) => !(c in limits)) || 'client') : '*'; limits[k] = { RequestsPerMinute: 60, TokensPerMinute: null }; render(); },
  'ratelimit-remove'(el) { const entries = Object.entries(state.config.Middleware.RateLimits || {}); entries.splice(+el.dataset.index, 1); state.config.Middleware.RateLimits = Object.fromEntries(entries); render(); },
  'alias-remove'(el) { const entries = Object.entries(state.config.Middleware.ModelAliases); entries.splice(+el.dataset.index, 1); state.config.Middleware.ModelAliases = Object.fromEntries(entries); render(); },
  async 'embeddings-test'() {
    const out = $('#embeddings-result'); out.textContent = 'testing…';
    try { const r = await api('POST', '/embeddings/test', { text: 'The quick brown fox' }); out.innerHTML = r.ok ? `<span class="badge ok">ok</span> ${r.dimensions} dimensions in ${r.milliseconds} ms` : `<span class="badge danger">failed</span> ${h(r.error)}`; }
    catch (e) { out.textContent = e.message; }
  },
  'embeddings-threshold'(el) { state.config.DefaultSimilarityThreshold = parseFloat(el.dataset.value); render(); toast('Threshold set in the draft: Apply to use it.', 'ok'); },
  'json-load'() { $('#json-editor').value = JSON.stringify(state.config, null, 2); },
  'json-use'() { try { state.config = JSON.parse($('#json-editor').value); state.errors = null; render(); toast('Draft replaced from JSON. Apply to use it.', 'ok'); } catch (e) { toast('Invalid JSON: ' + e.message, 'danger'); } },
  'json-download'() { const blob = new Blob([JSON.stringify(state.config, null, 2)], { type: 'application/json' }); const a = document.createElement('a'); a.href = URL.createObjectURL(blob); a.download = 'semantic-router.json'; a.click(); },
  async 'json-baseline'() { const b = await api('GET', '/config/baseline'); $('#json-editor').value = JSON.stringify(b.config, null, 2); toast('Showing the application baseline (not the draft).'); },
  copy(el) { const code = el.parentElement.querySelector('code').textContent; navigator.clipboard.writeText(code).then(() => toast('Copied.', 'ok')); },

  async 'eval-run'(el) {
    const ev = state.evaluation; ev.busy = true; ev.addFor = null; render();
    try { ev.report = await api('POST', '/evaluate' + (el.dataset.calibrate ? '?calibrate=true' : ''), { text: ev.text || '' }); }
    catch (e) { toast(e.message, 'danger'); }
    ev.busy = false; render();
  },
  async 'eval-save'() {
    const r = await api('PUT', '/evaluation/set', { text: state.evaluation.text || '' });
    state.evaluation.count = r.count; state.evaluation.location = r.location; render();
    toast(`Saved ${r.count} prompt(s)${r.location ? ' to ' + r.location : ''}.`, 'ok');
  },
  'eval-add-open'(el) { state.evaluation.addFor = +el.dataset.index; render(); },
  'eval-add-cancel'() { state.evaluation.addFor = null; render(); },
  async 'eval-add-save'(el) {
    const m = state.evaluation.report.misroutes[+el.dataset.index];
    const routeName = $('#add-route').value;
    const r = await api('POST', `/routes/${encodeURIComponent(routeName)}/utterances`, { utterance: m.prompt });
    setConfig(await api('GET', '/config'));
    state.evaluation.addFor = null;
    toast(`Added to ${r.route}: it now classifies as tier ${r.tier}${r.matchedRoute ? ' via ' + r.matchedRoute : ''}. Re-running…`, 'ok');
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
    try { state.evaluation.health = await api('GET', '/routes/health'); } catch (e) { toast(e.message, 'danger'); }
    render();
  },
  async 'eval-compare'() {
    const f = state.evaluation.form;
    const embeddings = f.provider === 'Hashing'
      ? { Provider: 'Hashing', Dimensions: f.dimensions || 512 }
      : { Provider: 'OpenAICompatible', Endpoint: f.endpoint || null, Model: f.model || null, ApiKey: f.apiKey || null };
    state.evaluation.compare = null; toast('Comparing…');
    try { state.evaluation.compare = await api('POST', '/evaluate/compare', { embeddings, text: state.evaluation.text || '' }); } catch (e) { toast(e.message, 'danger'); }
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
      state.evaluation.text = null; done.push(`evaluation set (${r.count})`);
    }
    if (l.toRoute) {
      const name = l.utteranceRoute || l.newRoute.trim();
      if (!name) { toast('Pick a route or name a new one.', 'danger'); return; }
      const r = await api('POST', `/routes/${encodeURIComponent(name)}/utterances`, { utterance: l.prompt, tier: l.utteranceRoute ? null : l.tier });
      setConfig(await api('GET', '/config'));
      done.push(`route ${r.route}: now tier ${r.tier}${r.matchedRoute ? ' via ' + r.matchedRoute : ''}`);
    }
    state.label = null; render();
    toast(done.length ? 'Saved to ' + done.join('; ') + '.' : 'Nothing to save.', 'ok');
  },

  'profile-add'() {
    state.config.Profiles = state.config.Profiles || {};
    let name = 'profile'; let n = 1; while (state.config.Profiles[name]) name = 'profile-' + (++n);
    state.config.Profiles[name] = { AllowedModels: [], DeniedModels: [], AllowedConnectors: [], DeniedConnectors: [], Strategy: null, MinTier: null, MaxTier: null, LocalOnly: false, Description: null };
    state.open.add('profile:' + name); render();
  },
  'profile-remove'(el) { delete state.config.Profiles[el.dataset.name]; render(); },
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
    catch (e) { toast(e.message, 'danger'); }
    state.tester.busy = false; render();
  },
  async 'tester-send'() {
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
    } catch (e) { toast(e.message, 'danger'); }
    state.tester.busy = false; render(); refreshStatus();
  },
};

export function readTester() {
  const p = $('#tester-prompt'); const m = $('#tester-model');
  if (p) state.tester.prompt = p.value;
  if (m) state.tester.model = m.value;
}

export async function applyConfig(persist) {
  try {
    const r = await api('PUT', '/config?persist=' + persist, state.config);
    setConfig(r);
    state.errors = null;
    await refreshStatus();
    render();
    toast(persist ? (r.store ? `Applied and saved to ${r.store}.` : 'Applied (no store registered: not saved).') : 'Applied to the running router (not saved).', 'ok');
    if (state.view === 'memory') loadMemory(true);
    else state.memory = null;
  } catch (e) {
    state.errors = (e.data && e.data.errors) || [e.message];
    render();
    toast('Not applied: ' + state.errors.length + ' problem(s).', 'danger');
  }
}

/* A learned change was applied or undone: the configuration changed too. */
async function afterTuning() {
  // An unsaved draft is kept (Apply would then overwrite the learned change, which the diff shows).
  if (!isDirty()) setConfig(await api('GET', '/config'));
  await loadTuning();
  await refreshStatus();
  render();
}
