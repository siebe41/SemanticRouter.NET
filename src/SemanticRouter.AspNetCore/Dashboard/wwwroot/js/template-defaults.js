/* Semantic Router dashboard: the settings an upgrade to a newer built-in template kept as you had them (Overview). */
import { api, h, readOnly, state, toast } from './core.js';
import { loadDigest } from './digest.js';
import { pageActions } from './page-actions.js';
import { adoptConfig, refreshStatus, render } from './shell.js';

/* GET api/config/template-defaults into state.templateDefaults; an older router without it leaves it null. */
export async function loadTemplateDefaults() {
  try { state.templateDefaults = await api('GET', '/config/template-defaults'); } catch { state.templateDefaults = null; }
}

const value = (json) => json == null ? '<span class="muted">none</span>' : `<span class="mono">${h(json)}</span>`;

/* Nothing unless an upgrade kept settings that are still to decide. */
export function templateDefaultsCard() {
  const t = state.templateDefaults;
  if (!t || !t.settings || !t.settings.length) return '';
  return `<div class="card section" id="template-defaults" role="region" aria-label="New defaults to review">
    <div class="card-head"><h2>New defaults to review</h2><span class="badge warn">${t.settings.length}</span></div>
    <p class="small muted">The upgrade from template ${h(String(t.from))} to ${h(String(t.to))} brought its fixes to every setting you had left as it was. These you had changed, and the template changed them too, so they were kept as yours. For each, take the new default or keep yours.</p>
    ${t.settings.map((s) => `<div class="learned-item">
      <div class="inline"><b class="mono">${h(s.name)}</b><span class="spacer"></span>${readOnly() ? '' : `<button class="btn small primary" data-action="template-use" data-setting="${h(s.name)}">Use the new default</button> <button class="btn small ghost" data-action="template-keep" data-setting="${h(s.name)}">Keep mine</button>`}</div>
      <div class="small">Yours: ${value(s.yours)} · the template's: ${value(s.default)}${s.current !== s.yours ? ` · now: ${value(s.current)}` : ''}</div>
    </div>`).join('')}
  </div>`;
}

async function afterChoice() {
  const result = adoptConfig(await api('GET', '/config'));
  await Promise.all([loadTemplateDefaults(), loadDigest()]);
  await refreshStatus();
  render();
  return result === 'kept' ? ' Your unapplied edits stay on the version they started from.' : '';
}

Object.assign(pageActions, {
  async 'template-use'(el) {
    const r = await api('POST', '/config/template-defaults/use', { setting: el.dataset.setting });
    toast(`${r.saved ? 'Applied and saved' : 'Applied, not saved'}: ${r.setting} now has the template's default.${await afterChoice()}`, 'ok');
  },
  async 'template-keep'(el) {
    await api('POST', '/config/template-defaults/keep', { setting: el.dataset.setting });
    toast(`Kept yours: ${el.dataset.setting}.${await afterChoice()}`, 'ok');
  },
});
