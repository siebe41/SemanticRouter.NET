/* Semantic Router dashboard: In the desktop app's window: the update banner and About SemanticRouter. */
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

if (bridge()) {
  const about = $('#desktop-about');
  about.classList.remove('hidden');
  about.addEventListener('click', () => send('about'));
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
