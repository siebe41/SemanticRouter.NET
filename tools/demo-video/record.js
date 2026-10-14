// Records the demo video: drives real requests through the router started by serve.js and captures the live
// dashboard, with captions and title cards drawn over it (overlay.js), as JPEG frames plus their timestamps.
//   node record.js <frames-dir>        then ./build.sh <frames-dir> <out.mp4>
// The router must be fresh (just started), so the dashboard starts empty and Claude is not cooling down.
const fs = require('fs');
const path = require('path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');

const base = `http://127.0.0.1:${Number(process.env.DEMO_PORT || 5290)}`;
const limitFile = path.join(require('os').tmpdir(), 'semantic-router-demo-claude-limit');
const out = path.resolve(process.argv[2] || 'frames');
const keys = { 'open-webui': 'demo-key-open-webui', 'claude-code-laptop': 'demo-key-claude-code', 'codex-cli': 'demo-key-codex' };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// One request in the protocol each client really speaks; returns the router's decision headers.
async function send(client, prompt, { tools = false } = {}) {
  const started = Date.now();
  let url; let headers; let body;
  if (client === 'claude-code-laptop') {
    url = '/v1/messages';
    headers = { 'x-api-key': keys[client], 'anthropic-version': '2023-06-01' };
    body = {
      model: 'claude-sonnet-4-5', max_tokens: 1024, messages: [{ role: 'user', content: prompt }],
      ...(tools ? { tools: [{ name: 'Edit', description: 'Edit a file', input_schema: { type: 'object', properties: { path: { type: 'string' } } } }] } : {}),
    };
  } else if (client === 'codex-cli') {
    url = '/v1/responses';
    headers = { authorization: `Bearer ${keys[client]}` };
    body = { model: 'gpt-5-codex', input: prompt };
  } else {
    url = '/v1/chat/completions';
    headers = { authorization: `Bearer ${keys[client]}` };
    body = { model: 'auto', messages: [{ role: 'user', content: prompt }] };
  }

  const res = await fetch(base + url, { method: 'POST', headers: { 'content-type': 'application/json', ...headers }, body: JSON.stringify(body) });
  await res.text();
  const h = (n) => res.headers.get(`x-prompt-route${n}`);
  return {
    status: res.status, model: h('-model'), tier: h('-tier'), route: h(''), similarity: Number(h('-similarity') || 0),
    attempt: Number(h('-attempt') || 1), degraded: h('-degraded') === 'true', localOnly: h('-local-only') === 'true',
    ms: Date.now() - started,
  };
}

const isLocal = (model) => model?.startsWith('lmstudio');
function describe(r, extra = '') {
  const cls = isLocal(r.model) ? 'local' : 'cloud';
  const where = isLocal(r.model) ? '<span class="tag">local · free</span>' : '<span class="tag">Claude subscription</span>';
  return `→ <span class="${cls}">${r.model}</span> <span class="meta">· T${r.tier} · ${r.route} ${r.similarity.toFixed(2)} · ${r.ms} ms</span>${where}${extra}`;
}

(async () => {
  fs.rmSync(out, { recursive: true, force: true });
  fs.mkdirSync(out, { recursive: true });

  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1440, height: 810 }, deviceScaleFactor: 4 / 3 });
  await page.goto(`${base}/router/#/live`);
  await page.waitForSelector('text=Model router');
  await page.addStyleTag({ path: path.join(__dirname, 'overlay.css') });
  await page.addScriptTag({ path: path.join(__dirname, 'overlay.js') });
  await page.evaluate(() => window.__demo.title());

  // Capture every frame the compositor produces, with its timestamp, straight to disk.
  const cdp = await page.context().newCDPSession(page);
  const frames = [];
  cdp.on('Page.screencastFrame', async ({ data, metadata, sessionId }) => {
    const file = `${String(frames.length).padStart(5, '0')}.jpg`;
    frames.push({ file, t: metadata.timestamp });
    fs.writeFileSync(path.join(out, file), Buffer.from(data, 'base64'));
    try { await cdp.send('Page.screencastFrameAck', { sessionId }); } catch { /* closing */ }
  });
  await cdp.send('Page.startScreencast', { format: 'jpeg', quality: 92, maxWidth: 1920, maxHeight: 1080, everyNthFrame: 1 });

  const demo = (fn, arg) => page.evaluate(([f, a]) => window.__demo[f](a), [fn, arg]);
  const scene = async (headline, client, prompt, opts = {}) => {
    await page.evaluate(([h, c]) => window.__demo.scene(h, c), [headline, client]);
    await demo('type', prompt);
    const r = await send(client, prompt, opts);
    await demo('result', (opts.describe || describe)(r));
    await sleep(opts.hold ?? 2600);
    return r;
  };

  // 1. Title.
  await sleep(3200);
  await demo('hideCard');
  await sleep(900);

  // 2. One prompt per kind of work, each in the protocol its client speaks.
  await scene('Small talk? A small local model answers it. Free.', 'open-webui', 'hello, how are you today?');
  await scene('Everyday code goes to the local coder.', 'codex-cli',
    'write a unit test for this function: int Add(int a, int b) => a + b;');
  await scene('Hard reasoning goes to your Claude subscription.', 'claude-code-laptop',
    'compare the trade-offs between event sourcing and CRUD for our [[Billing migration]]');
  await scene('Agentic coding gets the strongest model.', 'claude-code-laptop',
    'implement this feature across the codebase: durable cooldowns for the router', { tools: true });
  await scene('Private data never leaves your machine.', 'open-webui',
    'look up the customer record for account number 4471',
    { describe: (r) => describe(r, '<span class="tag lock">local-only route</span>') });

  // 3. Notes: text from the vault in a prompt, and where it went.
  await scene('And you can see which of your notes went where.', 'open-webui',
    'summarize this text:\nDuplicate invoices if the ledger replays events twice.\nCustomer account numbers are reused after closure in the legacy system.',
    { hold: 1400 });
  await demo('hideCaption');
  await page.evaluate(() => window.scrollTo({ top: document.body.scrollHeight, behavior: 'smooth' }));
  await sleep(3200);
  await page.evaluate(() => window.scrollTo({ top: 0, behavior: 'smooth' }));
  await sleep(900);

  // 4. A burst from three clients at once.
  await page.evaluate(() => window.__demo.scene('Every request, every decision, live.', ''));
  await demo('result', '<span class="meta">12 prompts from Open WebUI, Codex and Claude Code at once…</span>');
  const burst = [
    ['open-webui', 'thanks a lot!'],
    ['codex-cli', 'convert this loop to LINQ: foreach (var o in orders) if (o.Total > 100) big.Add(o);'],
    ['claude-code-laptop', 'why does this async code deadlock when I call .Result in ASP.NET?'],
    ['open-webui', 'translate this to French: the meeting moved to Friday'],
    ['codex-cli', 'write a regex that matches email addresses'],
    ['open-webui', 'fix the grammar in this sentence: their going to the store tomorow'],
    ['claude-code-laptop', 'design a scalable architecture for a multi-tenant billing platform'],
    ['open-webui', 'show the employee salary history for Carol'],
    ['codex-cli', 'add null checks to this method: void Save(Order o) { db.Add(o); }'],
    ['open-webui', 'good morning! tell me a joke'],
    ['claude-code-laptop', 'refactor the module and update every caller of LegacyLedger'],
    ['open-webui', 'rewrite this to sound more professional: we messed up the invoices, sorry'],
  ];
  const pending = [];
  for (const [client, prompt] of burst) { pending.push(send(client, prompt, { tools: client === 'claude-code-laptop' })); await sleep(330); }
  const results = await Promise.all(pending);
  const local = results.filter((r) => isLocal(r.model)).length;
  await demo('result', `<span class="local">${local} of ${results.length}</span> <span class="meta">answered locally, at no cost · the rest on your Claude subscription · $0 on API keys</span>`);
  await sleep(2800);

  // 5. Claude hits its usage limit: the router cools it down and fails over.
  fs.writeFileSync(limitFile, '');
  await scene('Claude hit its usage limit? The router fails over on its own.', 'claude-code-laptop',
    'find the root cause of this memory leak in the cache layer',
    {
      hold: 3400,
      describe: (r) => `<span class="fail">claude-code</span> <span class="meta">“you've hit your session limit”</span> → <span class="local">${r.model}</span>`
        + ` <span class="meta">· ${r.ms} ms</span><span class="tag warn">Claude cools down until 5pm</span>`,
    });

  // 6. The memory map.
  await demo('hideCaption');
  await page.click('nav a[href="#/memory"]');
  await sleep(1200);
  await page.evaluate(() => window.__demo.scene('The memory map: which notes reached local models, and which reached the cloud.', ''));
  await sleep(4200);
  await demo('hideCaption');
  await sleep(400);

  // 7. End card.
  await demo('end');
  await sleep(5800);

  fs.rmSync(limitFile, { force: true });
  await cdp.send('Page.stopScreencast');
  await sleep(300);
  await browser.close();

  // ffmpeg concat list: each frame lasts until the next one arrived; the last one holds for a second.
  const lines = [];
  frames.forEach((f, i) => {
    const next = frames[i + 1];
    lines.push(`file '${f.file}'`, `duration ${(next ? next.t - f.t : 1).toFixed(4)}`);
  });
  lines.push(`file '${frames[frames.length - 1].file}'`);
  fs.writeFileSync(path.join(out, 'frames.txt'), lines.join('\n') + '\n');
  console.log(`${frames.length} frames, ${(frames[frames.length - 1].t - frames[0].t).toFixed(1)} s`);
})().catch((e) => { console.error(e); process.exit(1); });
