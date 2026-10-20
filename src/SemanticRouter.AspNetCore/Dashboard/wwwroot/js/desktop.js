/* Semantic Router dashboard: In the desktop app's window: the update banner, App settings and About SemanticRouter. */
// The app's web view gives the page invokeCSharpAction(...) to send it messages, and hands the page its state as
// window.semanticRouterDesktop plus a 'semantic-router-desktop' event (DesktopBridge in the desktop app). In a browser
// there is no bridge, and none of this shows.
import { $, h } from './core.js';

const DISMISSED = 'semantic-router.update-dismissed';

/* The web view's way to the app, or null outside the app window. */
function bridge() {
  if (typeof window.invokeCSharpAction === 'function') return (body) => window.invokeCSharpAction(body);
  const external = window.external;
  if (external && typeof external.invokeCSharpAction === 'function') return (body) => external.invokeCSharpAction(body);
  return null;
}

function send(action) {
  const post = bridge();
  if (post) post(JSON.stringify({ action }));
}

// "Not now" hides the banner for that version; the next version is announced again. Without storage (a private
// profile) it comes back at the next start.
function dismissed() { try { return localStorage.getItem(DISMISSED); } catch { return null; } }
function dismiss(version) { try { localStorage.setItem(DISMISSED, version); } catch { /* shown again next time */ } }

/* The banner: an update ready or available (Restart to update, Not now), or one downloading. */
function show(s) {
  const el = $('#update-banner');
  const offer = s && s.offer;
  const visible = !!offer && (s.downloading || dismissed() !== offer);
  const key = visible ? JSON.stringify(s) : '';
  if (el.dataset.key === key) return;
  el.dataset.key = key;
  el.classList.toggle('hidden', !visible);
  if (!visible) { el.innerHTML = ''; return; }
  const buttons = s.canRestart && !s.downloading
    ? `<span class="update-actions"><button class="btn primary small" type="button" data-desktop="restart-to-update">Restart to update</button>`
      + `<button class="btn ghost small" type="button" data-desktop="dismiss">Not now</button></span>`
    : '';
  el.innerHTML = `<span><strong>${h(s.headline)}</strong>${s.detail ? ' ' + h(s.detail) : ''}</span>${buttons}`;
}

/*
 * The editing shortcuts on a Mac, in the app window. WebKit carries out ⌘C, ⌘V, ⌘X, ⌘A and ⌘Z only through the app's
 * Edit menu: without one they reach the page as plain key presses, and nothing happens. The app window has an Edit menu
 * (MacApplication in the desktop app), but whether the keys get to it past the app's own window is still to be tried
 * on a Mac, so they are done here too. Nothing is done twice: a key the page handles it prevents, and WebKit then does
 * not take it to the menu; one it leaves (a copy from a password field) goes on to the menu, and WebKit does not copy
 * from a password field either. Password fields are not copied from, as in a browser.
 */
const isMac = () => /Mac/.test(navigator.platform || '');

function textField(el) {
  return !!el && (el.tagName === 'TEXTAREA' || (el.tagName === 'INPUT' && typeof el.selectionStart === 'number' && el.selectionStart !== null));
}

function insert(el, text) {
  // insertText keeps the field's undo history; a field that refuses it gets the text set directly.
  if (!document.execCommand('insertText', false, text)) {
    el.setRangeText(text, el.selectionStart, el.selectionEnd, 'end');
    el.dispatchEvent(new Event('input', { bubbles: true }));
  }
}

export function editShortcut(ev) {
  if (!ev.metaKey || ev.ctrlKey || ev.altKey || ev.defaultPrevented) return;
  const el = document.activeElement;
  const field = textField(el);
  const writable = field && !el.readOnly && !el.disabled;
  switch (ev.key.toLowerCase()) {
    case 'a':
      ev.preventDefault();
      if (field) el.select(); else document.execCommand('selectAll');
      break;
    case 'c':
    case 'x': {
      if (field && el.type === 'password') return;
      const text = field ? el.value.slice(el.selectionStart, el.selectionEnd) : String(window.getSelection());
      if (!text || !navigator.clipboard) return;
      ev.preventDefault();
      navigator.clipboard.writeText(text).then(() => {
        if (ev.key.toLowerCase() === 'x' && writable) insert(el, '');
      }, () => { /* refused: nothing copied, nothing cut */ });
      break;
    }
    case 'v':
      if (!writable || !navigator.clipboard) return;
      ev.preventDefault();
      navigator.clipboard.readText().then((text) => { if (text && document.activeElement === el) insert(el, text); }, () => { /* refused */ });
      break;
    case 'z':
      if (!writable) return;
      ev.preventDefault();
      document.execCommand(ev.shiftKey ? 'redo' : 'undo');
      break;
    default:
  }
}

if (bridge() && isMac()) document.addEventListener('keydown', editShortcut);

if (bridge()) {
  for (const [id, action] of [['#desktop-settings', 'settings'], ['#desktop-about', 'about']]) {
    const button = $(id);
    button.classList.remove('hidden');
    button.addEventListener('click', () => send(action));
  }
  $('#update-banner').addEventListener('click', (ev) => {
    const el = ev.target.closest('[data-desktop]');
    if (!el) return;
    if (el.dataset.desktop === 'dismiss') {
      dismiss(window.semanticRouterDesktop && window.semanticRouterDesktop.offer);
      show(window.semanticRouterDesktop);
    } else {
      el.disabled = true;
      send(el.dataset.desktop);
    }
  });
  window.addEventListener('semantic-router-desktop', (ev) => show(ev.detail));
  show(window.semanticRouterDesktop);
  send('ready');
}
