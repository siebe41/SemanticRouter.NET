/* Semantic Router dashboard: form fields the settings pages share: numbers and durations whose default shows as a
   placeholder, pickers (checkboxes and chips) instead of comma lists, the time zone list, and Basic/Advanced sections.
   The helpers escape values and binding paths (a path can hold a connector or profile name); label, hint and summary
   are HTML, so callers pass constants or escaped text. */
import { h, state } from './core.js';
import { DURATION_PATTERN, duration } from './labels.js';
import { pageActions } from './page-actions.js';

/*
 * A number setting. A value equal to its default shows as an empty field with the default as the placeholder, so a
 * fallback never looks like something stored. Emptying a field that cannot be null removes the key from the draft, so
 * the router's default applies (null would not parse); a nullable one becomes null. min, max and step match validation.
 */
export function numField({ bind, value, def, label, hint, min, max, step, float, nullable, placeholder, cls }) {
  const shown = value == null || (def != null && value === def) ? '' : value;
  const ph = placeholder || (def != null ? `${def} (default)` : '');
  return `<label class="field${cls ? ' ' + cls : ''}"><span class="name">${label}</span><input type="number" data-bind="${h(bind)}" data-kind="${float ? 'float' : 'number'}" ${nullable ? 'data-nullable="1"' : 'data-empty="drop"'}
    ${min != null ? `min="${min}"` : ''} ${max != null ? `max="${max}"` : ''} ${step != null ? `step="${step}"` : ''} value="${h(shown)}" placeholder="${h(ph)}">${hint ? `<span class="hint">${hint}</span>` : ''}</label>`;
}

/*
 * A duration setting (hh:mm:ss, as .NET reads it). Its value in words sits under it ("15 min"), and a value that is not
 * hh:mm:ss is flagged as you type, before Apply refuses it.
 */
export function durationField({ bind, value, def, label, hint, zeroMeansDefault, shownDefault }) {
  // zeroMeansDefault: 00:00:00 stands for a default the router picks (shownDefault), as connector timeouts do.
  const unset = value == null || value === '' || value === def || (zeroMeansDefault && /^0+:00:00$/.test(value));
  const shown = unset ? '' : value;
  const fallback = shownDefault || def || '';
  return `<label class="field"><span class="name">${label}</span><input type="text" data-bind="${h(bind)}" data-empty="drop" data-duration="${h(fallback)}" pattern="${DURATION_PATTERN}" inputmode="numeric" spellcheck="false"
      value="${h(shown)}" placeholder="${h(fallback ? `${fallback} (default)` : 'hh:mm:ss')}" title="hh:mm:ss, e.g. 00:15:00">
    <span class="hint"><span class="duration-words">${shown ? duration(shown) : fallback ? `${duration(fallback)} by default` : ''}</span><span class="duration-bad">Use hh:mm:ss, e.g. 00:15:00 for 15 minutes.</span>${hint ? ' ' + hint : ''}</span></label>`;
}

/*
 * A list of names picked with checkboxes (known values) plus a text field for anything else (globs, custom tags). The
 * checkboxes write to a hidden data-bind field, so the usual binding (and the save bar) does the rest.
 */
export function pickList({ bind, values, known, label, hint, otherLabel, otherPlaceholder, chips }) {
  const list = values || [];
  const lower = known.map((k) => k.toLowerCase());
  const others = list.filter((v) => !lower.includes(String(v).toLowerCase()));
  const id = 'pick-' + bind.replace(/[^A-Za-z0-9]/g, '-');
  return `<fieldset class="field pick${chips ? ' chips-pick' : ''}" data-pick="${h(bind)}"><legend class="name">${label}</legend>
    <input type="hidden" data-bind="${h(bind)}" data-kind="csv" value="${h(list.join(', '))}">
    <div class="pick-options">${known.map((k) => `<label class="${chips ? 'chip' : 'check'}"><input type="checkbox" data-pick-value="${h(k)}" ${list.some((v) => String(v).toLowerCase() === k.toLowerCase()) ? 'checked' : ''}> ${h(k)}</label>`).join('') || '<span class="faint small">none yet</span>'}</div>
    <input type="text" id="${id}" data-pick-other aria-label="${h(otherLabel || label + ': others')}" value="${h(others.join(', '))}" placeholder="${h(otherPlaceholder || 'others, comma-separated')}">
    ${hint ? `<span class="hint">${hint}</span>` : ''}</fieldset>`;
}

/* IANA time zones the browser knows, for a datalist under a time zone field. */
export function timeZoneList(id) {
  let zones = [];
  try { zones = Intl.supportedValuesOf('timeZone'); } catch { /* older browser: free text only */ }
  return `<datalist id="${id}">${zones.map((z) => `<option value="${h(z)}">`).join('')}</datalist>`;
}

/* Advanced settings, collapsed until opened; the open state survives the re-renders a status refresh causes. */
const openSections = new Set();
export const openAttr = (key) => `data-open-key="${h(key)}" ${openSections.has(key) ? 'open' : ''}`;
export const closeSection = (key) => openSections.delete(key);
export function advanced(key, summary, body) {
  return `<details class="advanced" ${openAttr(key)}><summary>${summary}</summary>${body}</details>`;
}

/* Opens a page and puts the cursor in one of its settings (a link such as "set a reference model"). */
export function gotoField(view, bind) {
  if (state.view !== view) location.hash = '#/' + view;
  let tries = 30; // the page may still be loading its figures
  const find = () => {
    const el = document.querySelector(`[data-bind="${CSS.escape(bind)}"]`);
    if (el) { el.scrollIntoView({ block: 'center' }); el.focus(); el.classList.add('flash'); setTimeout(() => el.classList.remove('flash'), 1600); }
    else if (tries-- > 0) setTimeout(find, 100);
  };
  setTimeout(find, 50);
}
Object.assign(pageActions, { 'goto-field'(el) { gotoField(el.dataset.view, el.dataset.field); } });

/* ---------------------------------------------------------------------------------------------
   Wiring. These listeners run before app.js's (this module is evaluated first), and only touch their own attributes.
   --------------------------------------------------------------------------------------------- */
function deletePath(obj, path) {
  const keys = path.split('.');
  let o = obj;
  for (let i = 0; i < keys.length - 1; i++) { o = o && o[keys[i]]; }
  if (o && typeof o === 'object') delete o[keys[keys.length - 1]];
}

function syncPick(box) {
  const hidden = box.querySelector('input[type="hidden"][data-bind]');
  const picked = Array.from(box.querySelectorAll('input[data-pick-value]')).filter((c) => c.checked).map((c) => c.dataset.pickValue);
  const other = box.querySelector('input[data-pick-other]').value.split(',').map((s) => s.trim()).filter(Boolean);
  hidden.value = [...picked, ...other.filter((o) => !picked.some((p) => p.toLowerCase() === o.toLowerCase()))].join(', ');
  // The shared input handler binds the hidden field to the draft and shows the save bar.
  hidden.dispatchEvent(new Event('input', { bubbles: true }));
}

function onEdit(ev) {
  const el = ev.target;
  if (!el || !el.dataset) return;
  const box = el.closest && el.closest('[data-pick]');
  if (box && (el.dataset.pickValue != null || el.dataset.pickOther != null)) { syncPick(box); return; }
  if (el.dataset.empty === 'drop' && el.dataset.bind && el.value.trim() === '' && state.config) {
    // After the shared handler has written its 0 or "": the key goes, so the router's default applies.
    setTimeout(() => { if (el.value.trim() === '') deletePath(state.config, el.dataset.bind); });
  }
  if (el.dataset.duration != null) {
    const words = el.parentElement.querySelector('.duration-words');
    if (words) words.textContent = el.value.trim() ? duration(el.value) : el.dataset.duration ? `${duration(el.dataset.duration)} by default` : '';
  }
}
document.addEventListener('input', onEdit);
document.addEventListener('change', onEdit);
// toggle does not bubble: listen in the capture phase.
document.addEventListener('toggle', (ev) => {
  const key = ev.target && ev.target.dataset && ev.target.dataset.openKey;
  if (!key) return;
  if (ev.target.open) openSections.add(key); else openSections.delete(key);
}, true);
