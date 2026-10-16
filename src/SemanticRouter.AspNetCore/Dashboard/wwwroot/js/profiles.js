/* Semantic Router dashboard: Profiles page and the JSON page. */
import { historyCard } from './config.js';
import { STRATEGIES, adminOnly, csv, h, kv, state, tiers, views, when } from './core.js';

/* Profiles: named routing policies, how requests pick one, and a tester that compares them for one prompt. */
export const STRATEGY_OPTIONS = (value) => `<option value="" ${value ? '' : 'selected'}>(configured)</option>` + STRATEGIES.map((x) => `<option ${x === value ? 'selected' : ''}>${x}</option>`).join('');
export const tierSelect = (bind, value, blank) => `<select data-bind="${bind}" data-kind="number" data-nullable="1"><option value="" ${value == null ? 'selected' : ''}>${blank}</option>${tiers().map((t) => `<option value="${t.Level}" ${t.Level === value ? 'selected' : ''}>${t.Level} · ${h(t.Name)}</option>`).join('')}</select>`;

export function profileTesterCard() {
  const t = state.profileTester;
  const rows = (t.results || []).map((r) => `<tr><td>${r.profile ? `<span class="badge accent">${h(r.profile)}</span>` : '<span class="muted">none</span>'}</td>
    ${r.error ? `<td colspan="3" class="small"><span class="badge danger">error</span> ${h(r.error)}</td>` : `<td><span class="tier">T${r.plan.requiredTier}</span>${r.plan.localOnly ? ' <span class="badge warn">local only</span>' : ''}</td><td class="small">${h(r.plan.reason)}</td>
    <td class="small">${r.plan.models.map((m, i) => `${i ? ' → ' : ''}<b>${h(m.model)}</b>`).join('') || '<span class="badge danger">no model</span>'}</td>`}</tr>`).join('');
  return `<div class="card section"><div class="card-head"><h2>What would each profile do?</h2><span class="muted small">Routes the prompt under every profile without calling a model.</span></div>
    <div class="inline"><input type="text" id="profile-prompt" aria-label="Prompt to compare" data-state="profileTester.prompt" value="${h(t.prompt)}" placeholder="design a multi-tenant billing system" style="flex:1"><button class="btn primary small" data-action="profiles-compare" ${adminOnly(t.busy)}>${t.busy ? 'Routing…' : 'Compare'}</button></div>
    ${t.results ? `<table style="margin-top:10px"><tr><th>Profile</th><th>Tier</th><th>Reason</th><th>Models, in order</th></tr>${rows}</table>` : ''}</div>`;
}

views.profiles = () => {
  const c = state.config; const m = c.Middleware || {};
  const profiles = c.Profiles || {};
  const names = Object.keys(profiles);
  const profileOptions = (value, blank) => (blank ? `<option value="">${blank}</option>` : '') + names.map((x) => `<option ${x === value ? 'selected' : ''}>${h(x)}</option>`).join('');
  const catalog = (c.Models || []).map((x) => x.Name).join(', ');
  const rules = m.ProfileRules || [];
  const virtuals = Object.entries(m.VirtualModels || {});
  const when = m.LocalOnlyWhen || {};
  return `
    <div class="section-head"><h1>Profiles</h1><span class="muted">Policy beyond the prompt text: which models a context may use, how aggressive routing is, what must stay on this machine.</span><span class="spacer"></span><button class="btn primary small" data-action="profile-add">Add profile</button></div>
    ${names.map((name) => {
      const pr = profiles[name]; const key = 'profile:' + name; const p = `Profiles.${name}`;
      return `<div class="item ${state.open.has(key) ? 'open' : ''}" data-key="${key}">
        <div class="item-head" data-action="toggle" data-key="${key}" role="button" tabindex="0" aria-expanded="${state.open.has(key)}"><span class="chev">▶</span><span class="title">${h(name)}</span>
          <span class="meta">${pr.LocalOnly ? '<span class="badge warn">local only</span>' : ''}${pr.Strategy ? `<span class="badge">${h(pr.Strategy)}</span>` : ''}${pr.MinTier != null ? `<span>tier ≥ ${pr.MinTier}</span>` : ''}${pr.MaxTier != null ? `<span>tier ≤ ${pr.MaxTier}</span>` : ''}${(pr.AllowedModels || []).length ? `<span>${pr.AllowedModels.length} allowed</span>` : ''}${(pr.DeniedModels || []).length + (pr.DeniedConnectors || []).length ? `<span>${(pr.DeniedModels || []).length + (pr.DeniedConnectors || []).length} denied</span>` : ''}</span>
          <span class="spacer"></span><span class="muted small">${h(pr.Description || '')}</span></div>
        <div class="item-body"><div class="form-grid">
          <label class="field"><span class="name">Name</span><input type="text" data-action-input="profile-rename" data-name="${h(name)}" value="${h(name)}"><span class="hint">Rules, virtual models, client mappings and budgets follow a rename.</span></label>
          <label class="field wide"><span class="name">Description</span><input type="text" data-bind="${p}.Description" data-kind="nulltext" value="${h(pr.Description || '')}"></label>
          <label class="field"><span class="name">Strategy</span><select data-bind="${p}.Strategy" data-kind="nulltext">${STRATEGY_OPTIONS(pr.Strategy)}</select></label>
          <label class="field"><span class="name">Minimum tier</span>${tierSelect(p + '.MinTier', pr.MinTier, '(none)')}</label>
          <label class="field"><span class="name">Maximum tier</span>${tierSelect(p + '.MaxTier', pr.MaxTier, '(none)')}<span class="hint">Caps the requirement, so cheaper models are acceptable.</span></label>
          <label class="field"><span class="name">Allowed models</span><input type="text" data-bind="${p}.AllowedModels" data-kind="csv" value="${h(csv(pr.AllowedModels))}" placeholder="all"><span class="hint">Names or globs (api-*). Catalog: ${h(catalog)}</span></label>
          <label class="field"><span class="name">Denied models</span><input type="text" data-bind="${p}.DeniedModels" data-kind="csv" value="${h(csv(pr.DeniedModels))}"></label>
          <label class="field"><span class="name">Allowed connectors</span><input type="text" data-bind="${p}.AllowedConnectors" data-kind="csv" value="${h(csv(pr.AllowedConnectors))}" placeholder="all"></label>
          <label class="field"><span class="name">Denied connectors</span><input type="text" data-bind="${p}.DeniedConnectors" data-kind="csv" value="${h(csv(pr.DeniedConnectors))}"></label>
          <label class="check wide"><input type="checkbox" data-bind="${p}.LocalOnly" data-kind="bool" ${pr.LocalOnly ? 'checked' : ''}> Local only: only models marked <i>local</i> answer, even on failover</label>
        </div><div class="row-actions" style="margin-top:10px"><button class="btn danger small" data-action="profile-remove" data-name="${h(name)}">Remove</button></div></div></div>`;
    }).join('') || '<div class="card muted">No profiles. Every request is routed by the prompt alone.</div>'}
    ${profileTesterCard()}
    <div class="card section"><div class="card-head"><h2>How a request picks a profile</h2><span class="muted small">First match wins: the gateway key's profile (enforced), the header, the model name, the rules.</span></div><div class="form-grid">
      <label class="field"><span class="name">Profile header</span><input type="text" data-bind="Middleware.ProfileHeader" value="${h(m.ProfileHeader || 'X-Router-Profile')}"></label>
      <label class="field"><span class="name">Project header</span><input type="text" data-bind="Middleware.ProjectHeader" value="${h(m.ProjectHeader || 'X-Router-Project')}"><span class="hint">Without it: Anthropic <code>metadata.user_id</code> or OpenAI <code>user</code>.</span></label>
      <label class="field wide"><span class="name">Gateway client → profile (client=profile, one per line)</span><textarea data-bind="Middleware.ClientProfiles" data-kind="kv" rows="2" placeholder="laptop=private">${h(kv(m.ClientProfiles))}</textarea><span class="hint">Clients are the names under Gateway keys. A client cannot leave its profile.</span></label>
    </div>
    <h3 style="margin-top:12px">Rules</h3>
    <table><tr><th>Profile</th><th>Clients</th><th>Projects (globs)</th><th>Offers a tool</th><th>Offers none of</th><th></th></tr>
      ${rules.map((r, i) => `<tr><td><select data-bind="Middleware.ProfileRules.${i}.Profile">${profileOptions(r.Profile)}</select></td>
        <td><input type="text" data-bind="Middleware.ProfileRules.${i}.Clients" data-kind="csv" value="${h(csv(r.Clients))}" placeholder="any"></td>
        <td><input type="text" data-bind="Middleware.ProfileRules.${i}.Projects" data-kind="csv" value="${h(csv(r.Projects))}" placeholder="any"></td>
        <td><input type="text" data-bind="Middleware.ProfileRules.${i}.ToolsInclude" data-kind="csv" value="${h(csv(r.ToolsInclude))}" placeholder="any, e.g. Bash"></td>
        <td><input type="text" data-bind="Middleware.ProfileRules.${i}.ToolsExclude" data-kind="csv" value="${h(csv(r.ToolsExclude))}"></td>
        <td><button class="btn ghost small" data-action="rule-profile-remove" data-index="${i}">remove</button></td></tr>`).join('') || '<tr><td colspan="6" class="muted small">No rules.</td></tr>'}
    </table><div class="inline" style="margin-top:8px"><button class="btn small" data-action="rule-profile-add" ${names.length ? '' : 'disabled title="Add a profile first"'}>Add rule</button></div>
    <h3 style="margin-top:12px">Virtual models</h3><p class="muted small">Model names clients can pick (listed by <code>/v1/models</code>). <code>auto@profile</code> works for every profile too.</p>
    <table><tr><th>Name</th><th>Profile</th><th>Strategy</th><th>Description</th><th></th></tr>
      ${virtuals.map(([n, v], i) => `<tr><td><input type="text" data-action-input="virtual-rename" data-name="${h(n)}" value="${h(n)}"></td>
        <td><select data-bind="Middleware.VirtualModels.${n}.Profile" data-kind="nulltext">${profileOptions(v.Profile, '(none)')}</select></td>
        <td><select data-bind="Middleware.VirtualModels.${n}.Strategy" data-kind="nulltext">${STRATEGY_OPTIONS(v.Strategy)}</select></td>
        <td><input type="text" data-bind="Middleware.VirtualModels.${n}.Description" data-kind="nulltext" value="${h(v.Description || '')}"></td>
        <td><button class="btn ghost small" data-action="virtual-remove" data-name="${h(n)}">remove</button></td></tr>`).join('') || '<tr><td colspan="5" class="muted small">No virtual models.</td></tr>'}
    </table><div class="inline" style="margin-top:8px"><button class="btn small" data-action="virtual-add">Add virtual model</button></div>
    <h3 style="margin-top:12px">Keep local when the conversation contains…</h3>
    <label class="check"><input type="checkbox" data-bind="Middleware.LocalOnlyWhen.Secrets" data-kind="bool" ${when.Secrets ? 'checked' : ''}> something that looks like a credential (API keys, private keys, JWTs)</label>
    <label class="check"><input type="checkbox" data-bind="Middleware.LocalOnlyWhen.FilePaths" data-kind="bool" ${when.FilePaths ? 'checked' : ''}> an absolute file path <span class="muted small">(agents mention paths on almost every turn)</span></label>
    </div>`;
};

views.json = () => `
    <div class="section-head"><h1>JSON</h1><span class="muted">The whole document, the same shape as the <code>SemanticRouter</code> section of appsettings.json.</span><span class="spacer"></span>
      <button class="btn small" data-action="json-load">Load into editor</button><button class="btn small" data-action="json-download">Download</button><button class="btn ghost small" data-action="json-baseline" title="Show what the application's own configuration says">Show baseline</button></div>
    <div class="card section">
      <textarea id="json-editor" aria-label="Configuration JSON" rows="30" spellcheck="false" data-state="jsonText">${h(state.jsonText != null ? state.jsonText : JSON.stringify(state.config, null, 2))}</textarea>
      <div class="inline" style="margin-top:10px"><button class="btn primary" data-action="json-use">Use this JSON as the draft</button><span class="muted small">Secrets show as <code>***</code>; that placeholder keeps the stored value.</span></div>
    </div>
    <div class="card section flat"><details><summary>Where does the configuration come from?</summary><p class="small muted" style="margin-top:8px">The application's <code>SemanticRouter</code> section (appsettings.json, environment variables, user-secrets, code) is the baseline. When a configuration store is registered (<code>UseConfigurationFile</code>), a saved document replaces the baseline on start-up; secrets left empty in the document are inherited from the baseline. <b>Apply</b> changes the running router; <b>Apply &amp; save</b> also writes the store. <b>Reset</b> (in the JSON page) goes back to the baseline for this process.</p><button class="btn danger small" data-action="config-reset">Reset running configuration to baseline</button></details></div>
    ${historyCard()}`;
