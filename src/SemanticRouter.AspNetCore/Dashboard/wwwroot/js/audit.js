/* Semantic Router dashboard: Audit log page: who changed what through the dashboard and admin API, and sign-ins (admin only). */
import { API, api, h, readOnly, state, toast, views } from './core.js';
import { render } from './shell.js';

/*
 * GET api/audit into state.audit.entries. A load of the same limit already on its way is left to finish; a new limit, or
 * again (Refresh), starts another, and only the latest one's answer is kept.
 */
export async function loadAudit(again = false) {
  const limit = state.audit.limit;
  if (state.audit.loading && state.audit.loading.limit === limit && !again) return;
  const request = state.audit.loading = { limit };
  let entries; let error = null;
  try {
    entries = (await api('GET', `/audit?limit=${limit}`)).entries;
  } catch (e) {
    entries = [];
    error = e;
  }
  if (state.audit.loading !== request) return;
  state.audit.entries = entries;
  state.audit.error = !error ? null : error.status === 403 ? 'Only the admin role can read the audit log.' : error.message;
  if (error && error.status !== 403) toast(error.message, 'danger');
  state.audit.loading = null;
  if (state.view === 'audit') render();
}

const stamp = (iso) => { const d = new Date(iso); return isNaN(d) ? '' : d.toLocaleString(undefined, { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit', second: '2-digit' }); };
const outcome = (status) => `<span class="badge ${status >= 500 ? 'danger' : status >= 400 ? 'warn' : 'ok'}">${status}</span>`;

views.audit = () => {
  const a = state.audit;
  const head = `<div class="section-head"><h1>Audit log</h1><span class="muted">Every change made through the dashboard or the admin API, sign-ins and sign-outs: when, who, from where, and the outcome. Request bodies, tokens and prompt text are never recorded.</span></div>`;
  if (readOnly()) return head + '<div class="card muted">Only the admin role can read the audit log.</div>';
  if (!a.entries) { loadAudit(); return head + '<div class="muted">Loading…</div>'; }
  const who = state.session.identity ? `You are signed in as <b>${h(state.session.identity)}</b>.` : '';
  return head + `
    <div class="card section">
      <div class="card-head"><h2>Recent actions</h2><span class="muted small">${who}</span><span class="spacer"></span>
        <label class="small inline-field"><span>Show</span><select id="audit-limit" style="width:auto">${[100, 200, 1000].map((n) => `<option value="${n}" ${a.limit === n ? 'selected' : ''}>${n}</option>`).join('')}</select></label>
        <button class="btn small" data-action="audit-load">Refresh</button>
        <a class="btn small" href="${API}/audit?format=jsonl&amp;limit=10000" download>Download JSON Lines</a>
        <button class="btn danger small" data-action="sessions-revoke-all" title="End every dashboard session signed in with a token, yours included">Sign out every session…</button>
      </div>
      ${a.error ? `<p class="small" style="color:var(--danger)">${h(a.error)}</p>` : ''}
      <div class="table-wrap"><table aria-label="Audit log">
        <thead><tr><th scope="col">When</th><th scope="col">Who</th><th scope="col">From</th><th scope="col">What</th><th scope="col">Request</th><th scope="col">Result</th></tr></thead>
        <tbody>${a.entries.map((e) => `<tr>
          <td class="small">${h(stamp(e.timestamp))}</td>
          <td><b>${h(e.actor)}</b></td>
          <td class="small mono">${h(e.client || 'this machine')}</td>
          <td>${h(e.summary)}</td>
          <td class="small mono">${h(e.method)} ${h(e.path)}</td>
          <td>${outcome(e.status)}</td>
        </tr>`).join('') || '<tr><td colspan="6" class="muted small">Nothing recorded yet.</td></tr>'}</tbody>
      </table></div>
      <p class="small faint">Kept for <span title="State:Retention:AuditDays">365 days</span> by default in the state database (in memory without one), and written to the router's log too.</p>
    </div>`;
};
