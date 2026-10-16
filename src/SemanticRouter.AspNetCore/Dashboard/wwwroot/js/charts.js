/* Semantic Router dashboard: Plain SVG charts (one y-axis, series colours from --series-*) and the Trends card. */
import { api, h, state } from './core.js';
import { render } from './shell.js';


/* ---------------------------------------------------------------------------------------------
   Charts. Plain SVG; one y-axis; series colours from --series-*; text in text tokens; hover tips.
   --------------------------------------------------------------------------------------------- */
export const fmtTime = (iso, span) => { const d = new Date(iso); return span === '7d' || span === '30d' ? d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' }) + (span === '7d' ? ' ' + d.getHours() + 'h' : '') : d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' }); };
export const fmtMs = (v) => v == null ? '–' : v >= 1000 ? (v / 1000).toFixed(1) + ' s' : Math.round(v) + ' ms';
/*
 * The top of a y-axis with ticks at 0, half and the top. Every chart counts things or milliseconds, so the half must be
 * a whole number too: up to 10 the top is the next even number (a top of 1 used to label "0, 1, 1").
 */
export const niceMax = (v) => {
  if (v <= 10) return Math.max(2, 2 * Math.ceil(v / 2));
  const p = Math.pow(10, Math.floor(Math.log10(v))); const n = v / p;
  return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * p;
};
export const W = 640;
export const H = 170;
export const PAD = { l: 44, r: 64, t: 10, b: 22 };

export function axes(max, fmt, buckets, span) {
  const ih = H - PAD.t - PAD.b;
  let g = '';
  // A label that reads the same as the one below it is left out (rounding can make two ticks look alike).
  let below = null;
  [0, 0.5, 1].forEach((f) => { const y = PAD.t + ih - f * ih; const text = fmt(max * f); g += `<line class="grid-line" x1="${PAD.l}" x2="${W - PAD.r}" y1="${y}" y2="${y}"/>${text === below ? '' : `<text class="axis" x="${PAD.l - 6}" y="${y + 3}" text-anchor="end">${h(text)}</text>`}`; below = text; });
  const iw = W - PAD.l - PAD.r; const step = Math.max(1, Math.ceil(buckets.length / 6));
  buckets.forEach((b, i) => { if (i % step === 0) g += `<text class="axis" x="${PAD.l + (i + 0.5) * iw / buckets.length}" y="${H - 6}" text-anchor="middle">${h(fmtTime(b.start, span))}</text>`; });
  return g;
}

export function requestsChart(series, span) {
  const b = series.buckets; const max = niceMax(Math.max(1, ...b.map((x) => x.requests)));
  const iw = W - PAD.l - PAD.r; const ih = H - PAD.t - PAD.b; const bw = iw / b.length;
  const bars = b.map((x, i) => {
    const hgt = x.requests / max * ih; const xpos = PAD.l + i * bw + 1; const w = Math.max(1, bw - 2);
    const tip = `${fmtTime(x.start, span)}\n${x.requests} requests · ${x.served} served · ${x.failed} failed`;
    return (x.requests ? `<rect class="bar-mark" x="${xpos}" y="${PAD.t + ih - hgt}" width="${w}" height="${hgt}" rx="${Math.min(4, w / 2)}"/>` : '') +
      `<rect class="hit" x="${PAD.l + i * bw}" y="${PAD.t}" width="${bw}" height="${ih}" data-tip="${h(tip)}"/>`;
  }).join('');
  return `<svg class="chart" viewBox="0 0 ${W} ${H}" role="img" aria-label="Requests per interval">${axes(max, (v) => String(Math.round(v)), b, span)}${bars}</svg>`;
}

export function latencyChart(series, span) {
  const b = series.buckets; const max = niceMax(Math.max(1, ...b.map((x) => x.p95Milliseconds || 0)));
  const iw = W - PAD.l - PAD.r; const ih = H - PAD.t - PAD.b; const bw = iw / b.length;
  const pt = (i, v) => [PAD.l + (i + 0.5) * bw, PAD.t + ih - v / max * ih];
  const path = (key) => { let d = ''; let pen = false; b.forEach((x, i) => { const v = x[key]; if (v == null) { pen = false; return; } const [px, py] = pt(i, v); d += (pen ? 'L' : 'M') + px.toFixed(1) + ' ' + py.toFixed(1); pen = true; }); return d; };
  const last = (key) => { for (let i = b.length - 1; i >= 0; i--) if (b[i][key] != null) return [i, b[i][key]]; return null; };
  const endLabel = (key, cls, name) => { const l = last(key); if (!l) return ''; const [px, py] = pt(l[0], l[1]); return `<circle class="dot-${cls}" cx="${px}" cy="${py}" r="4"/><text class="end-label" x="${px + 8}" y="${py + (cls === 1 ? 10 : -4)}">${name} ${h(fmtMs(l[1]))}</text>`; };
  const hits = b.map((x, i) => `<rect class="hit" x="${PAD.l + i * bw}" y="${PAD.t}" width="${bw}" height="${ih}" data-tip="${h(`${fmtTime(x.start, span)}\np50 ${fmtMs(x.p50Milliseconds)} · p95 ${fmtMs(x.p95Milliseconds)}`)}"/>`).join('');
  return `<svg class="chart" viewBox="0 0 ${W} ${H}" role="img" aria-label="Latency p50 and p95">${axes(max, fmtMs, b, span)}<path class="line-2" d="${path('p95Milliseconds')}"/><path class="line-1" d="${path('p50Milliseconds')}"/>${endLabel('p95Milliseconds', 2, 'p95')}${endLabel('p50Milliseconds', 1, 'p50')}${hits}</svg>`;
}

export async function loadTrends(auto) {
  if (state.trends.loading) return;
  // After a failure the automatic refresh backs off (10 s, doubling to 5 min) rather than asking on every render.
  if (auto && state.trends.error && Date.now() < state.trends.retryAt) return;
  state.trends.loading = true;
  try {
    state.trends.data = await api('GET', '/activity/series?window=' + state.trends.window);
    state.trends.error = null; state.trends.backoff = 0;
  } catch (e) {
    // The last figures stay; the card says the refresh failed.
    state.trends.backoff = Math.min(300000, (state.trends.backoff || 5000) * 2);
    state.trends.error = e.message; state.trends.retryAt = Date.now() + state.trends.backoff;
  }
  state.trends.loading = false;
  if (state.view === 'overview') render();
}

export const trendsCard = () => {
  const t = state.trends; const s = t.data;
  const failed = t.error ? `<div class="small load-error">Couldn't load trends: ${h(t.error.replace(/\.$/, ''))}. <button class="btn small" data-action="trends-retry">Retry</button></div>` : '';
  if (!s && t.error) return `<div class="card section"><div class="card-head"><h2>Trends</h2></div>${failed}</div>`;
  if (!s) { loadTrends(); return '<div class="card section"><div class="card-head"><h2>Trends</h2></div><div class="muted small">Loading…</div></div>'; }
  const seg = ['1h', '24h', '7d', '30d'].map((w) => `<button class="${w === t.window ? 'on' : ''}" data-action="trends-window" data-window="${w}">${w}</button>`).join('');
  const rows = s.buckets.filter((b) => b.requests).map((b) => `<tr><td>${h(fmtTime(b.start, t.window))}</td><td class="num">${b.requests}</td><td class="num">${b.served}</td><td class="num">${b.failed}</td><td class="num">${h(fmtMs(b.p50Milliseconds))}</td><td class="num">${h(fmtMs(b.p95Milliseconds))}</td></tr>`).join('');
  return `<div class="card section"><div class="card-head"><h2>Trends</h2><span class="muted small">${s.requests} requests · p50 ${h(fmtMs(s.p50Milliseconds))} · p95 ${h(fmtMs(s.p95Milliseconds))} · p99 ${h(fmtMs(s.p99Milliseconds))}</span><span class="spacer"></span><span class="seg" role="group" aria-label="Window">${seg}</span></div>
    ${failed}
    <div class="grid cols-2">
      <div><h3 class="small muted">Requests</h3>${requestsChart(s, t.window)}</div>
      <div><h3 class="small muted">Latency (served requests, end to end)</h3><div class="legend"><span><i class="key" style="background:var(--series-1)"></i>p50</span><span><i class="key" style="background:var(--series-2)"></i>p95</span></div>${latencyChart(s, t.window)}</div>
    </div>
    <details style="margin-top:8px"><summary class="small muted">Show as a table</summary><table><tr><th>From</th><th>Requests</th><th>Served</th><th>Failed</th><th>p50</th><th>p95</th></tr>${rows || '<tr><td colspan="6" class="muted small">No requests in this window.</td></tr>'}</table></details>
  </div>`;
};
