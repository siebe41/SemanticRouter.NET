// Records the demo video. Everything on screen is real: two shells in pseudo-terminals (rendered with xterm.js on
// stage.html) install the tool, run `semantic-router init`, start the router and run Claude Code against it, and the
// router's own dashboard shows the decisions. Only the models are stand-ins: lmstudio-stub.js answers as LM Studio,
// and fake-claude/claude answers the router's Claude Code CLI calls (and can report a session limit). Each beat is
// timed to its narration line (tts.py), and the narration's start times are written for build.js.
//
//   node record.js <frames-dir> <voice-dir>
//
//   DEMO_HOME         the throw-away home directory of the demo user (default /home/demo; it is deleted first)
//   DEMO_FEED         a folder holding a SemanticRouter.Host .nupkg to install instead of nuget.org's
//   DEMO_REAL_CLAUDE  the real Claude Code binary (default: `claude` on PATH)
const fs = require('fs');
const http = require('http');
const os = require('os');
const path = require('path');
const { execFileSync } = require('child_process');
const pty = require('node-pty');
const { chromium } = require('playwright');
const { createStub } = require('./lmstudio-stub');

const here = __dirname;
const out = path.resolve(process.argv[2] || 'out/frames');
const voiceDir = path.resolve(process.argv[3] || 'out/voice');
const home = process.env.DEMO_HOME || '/home/demo';
const project = path.join(home, 'billing');
const router = 'http://localhost:5080';
const limitFile = path.join(os.tmpdir(), 'semantic-router-demo-claude-limit');
const which = (cmd) => execFileSync('bash', ['-c', `command -v ${cmd}`]).toString().trim();
const realClaude = process.env.DEMO_REAL_CLAUDE || fs.realpathSync(which('claude'));
const dotnetDir = process.env.DOTNET_ROOT || path.dirname(fs.realpathSync(which('dotnet')));
const durations = JSON.parse(fs.readFileSync(path.join(voiceDir, 'durations.json'), 'utf8'));
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// A fresh demo user: no tool, no router configuration, Claude Code's first-run notices already seen.
function prepareHome() {
  fs.rmSync(home, { recursive: true, force: true });
  fs.mkdirSync(path.join(home, '.local', 'bin'), { recursive: true });
  fs.mkdirSync(project, { recursive: true });
  fs.symlinkSync(path.join(here, 'fake-claude', 'claude'), path.join(home, '.local', 'bin', 'claude'));
  if (process.env.DEMO_FEED) {
    fs.writeFileSync(path.join(project, 'nuget.config'),
      `<?xml version="1.0" encoding="utf-8"?>\n<configuration><packageSources><clear /><add key="local" value="${path.resolve(process.env.DEMO_FEED)}" /></packageSources></configuration>\n`);
  }
  fs.writeFileSync(path.join(home, '.claude.json'), JSON.stringify({
    hasCompletedOnboarding: true, theme: 'dark', numStartups: 9, autoUpdates: false, migrationVersion: 14,
    opusProMigrationComplete: true, sonnet1m45MigrationComplete: true, hasResetAutoModeOptInForDefaultOffer: true,
    hasSeenAutoDefaultNotice: true, hasSeenAutoDefaultNudge: true, hasSeenAutoModeEntryWarning: true,
    lastReleaseNotesSeen: execFileSync(realClaude, ['--version']).toString().split(' ')[0],
    projects: { [project]: { hasTrustDialogAccepted: true, hasCompletedProjectOnboarding: true, allowedTools: [] } },
  }, null, 2));
  fs.rmSync(limitFile, { force: true });
}

// Only what the demo user's shell needs: none of the recording machine's own variables or credentials.
const env = {
  HOME: home, TERM: 'xterm-256color', LANG: 'C.UTF-8',
  PATH: [path.join(home, '.local', 'bin'), path.join(home, '.dotnet', 'tools'), dotnetDir, path.dirname(process.execPath), '/usr/local/bin', '/usr/bin', '/bin'].join(':'),
  DOTNET_ROOT: dotnetDir, DOTNET_NOLOGO: '1', DOTNET_CLI_TELEMETRY_OPTOUT: '1',
  DISABLE_AUTOUPDATER: '1', CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1',
  DEMO_REAL_CLAUDE: realClaude, DEMO_CLAUDE_LIMIT_FILE: limitFile,
  PS1: '\\[\\e[38;5;38m\\]~/billing\\[\\e[0m\\] \\[\\e[38;5;179m\\]$\\[\\e[0m\\] ',
};

// Serves stage.html and xterm.js to the browser.
function serveStage() {
  const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css' };
  return new Promise((resolve) => {
    const server = http.createServer((req, res) => {
      const file = path.join(here, decodeURIComponent(new URL(req.url, 'http://x').pathname));
      if (!file.startsWith(here) || !fs.existsSync(file)) { res.writeHead(404).end(); return; }
      res.writeHead(200, { 'Content-Type': types[path.extname(file)] || 'application/octet-stream' });
      fs.createReadStream(file).pipe(res);
    });
    server.listen(0, '127.0.0.1', () => resolve(server));
  });
}

(async () => {
  prepareHome();
  fs.rmSync(out, { recursive: true, force: true });
  fs.mkdirSync(out, { recursive: true });

  const stub = createStub();
  await new Promise((r) => stub.listen(1234, '127.0.0.1', r));
  const stageServer = await serveStage();

  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1920, height: 1080 } });
  // The dashboard forbids framing (frame-ancestors 'none'); lift that for its page loads on this stage only.
  await page.route('http://localhost:5080/router/**', async (route) => {
    if (route.request().resourceType() !== 'document') return route.continue();
    const response = await route.fetch();
    const headers = { ...response.headers() };
    delete headers['content-security-policy'];
    delete headers['x-frame-options'];
    return route.fulfill({ response, headers });
  });
  await page.goto(`http://127.0.0.1:${stageServer.address().port}/stage.html`);
  await page.waitForFunction(() => window.stage);

  // Two shells, each mirrored into its xterm on the stage.
  const shells = {};
  for (const [id, cols, rows] of [['router', 96, 26], ['claude', 88, 34]]) {
    const shell = pty.spawn('bash', ['--noprofile', '--norc', '-i'], { name: 'xterm-256color', cols, rows, cwd: project, env });
    let pending = '';
    shell.onData((d) => { pending += d; });
    setInterval(() => {
      if (!pending) return;
      const data = pending; pending = '';
      page.evaluate(([i, d]) => window.stage.write(i, d), [id, data]).catch(() => {});
    }, 16);
    shells[id] = shell;
  }

  const screen = (id) => page.evaluate((i) => window.stage.screen(i), id);
  async function waitFor(id, pattern, timeout = 60000) {
    const until = Date.now() + timeout;
    while (Date.now() < until) {
      if (pattern.test(await screen(id))) return;
      await sleep(150);
    }
    throw new Error(`Timed out waiting for ${pattern} in the ${id} terminal:\n${await screen(id)}`);
  }
  // Types like a person: about 18 characters a second, then Enter.
  async function type(id, text, enter = true) {
    for (const ch of text) { shells[id].write(ch); await sleep(35 + Math.random() * 45); }
    if (enter) { await sleep(280); shells[id].write('\r'); }
  }

  // Frames, with the wall-clock time each arrived, to place the narration on the video's own clock.
  const cdp = await page.context().newCDPSession(page);
  const frames = [];
  cdp.on('Page.screencastFrame', async ({ data, metadata, sessionId }) => {
    const file = `${String(frames.length).padStart(5, '0')}.jpg`;
    frames.push({ file, t: metadata.timestamp, wall: Date.now() / 1000 });
    fs.writeFileSync(path.join(out, file), Buffer.from(data, 'base64'));
    try { await cdp.send('Page.screencastFrameAck', { sessionId }); } catch { /* closing */ }
  });

  const narration = [];
  const say = (key) => {
    narration.push({ key, wall: Date.now() / 1000 });
    return sleep(durations[key] * 1000);
  };
  const stage = (fn, ...args) => page.evaluate(([f, a]) => window.stage[f](...a), [fn, args]);

  // What the router did with a prompt, from its own activity log.
  async function decision(prompt) {
    for (let i = 0; i < 20; i++) {
      const res = await fetch(`${router}/router/api/activity?limit=20`).then((r) => r.json());
      const rows = Array.isArray(res) ? res : res.entries || res.items || [];
      const hit = rows.find((e) => e.promptPreview?.startsWith(prompt.slice(0, 30)) && e.outcome === 'served');
      if (hit) return hit;
      await sleep(250);
    }
    return null;
  }
  const isLocal = (model) => model?.startsWith('lmstudio');
  function describe(e) {
    if (!e) return '';
    const cls = isLocal(e.servedBy) ? 'local' : 'cloud';
    const tag = isLocal(e.servedBy) ? '<span class="tag local">local · free</span>' : '<span class="tag cloud">Claude subscription</span>';
    const failed = (e.sentTo || []).filter((m) => m !== e.servedBy).map((m) => `<span class="fail">${m}</span> `).join('');
    return `${failed}→ <span class="${cls}">${e.servedBy}</span>  ·  tier ${e.tier}  ·  ${e.route || e.reason}${tag}`;
  }
  async function ask(prompt, done) {
    await type('claude', prompt);
    await waitFor('claude', done);
    return decision(prompt);
  }

  await cdp.send('Page.startScreencast', { format: 'jpeg', quality: 92, maxWidth: 1920, maxHeight: 1080, everyNthFrame: 1 });
  await sleep(300);

  // 1. Title.
  let line = say('title');
  await stage('title');
  await line;
  await stage('hideCard');
  await stage('layout', 'setup');
  await sleep(900);

  // 2. Setup, for real: install, init, start.
  line = say('install');
  await stage('caption', '1. Install the tool', 'one command · .NET 10');
  await type('router', 'dotnet tool install -g SemanticRouter.Host');
  await waitFor('router', /successfully installed/);
  await line;

  line = say('init');
  await stage('caption', '2. init finds your models', 'LM Studio · Ollama · the claude, codex and copilot CLIs · writes a starting configuration');
  await sleep(400);
  await type('router', 'semantic-router init');
  await waitFor('router', /Next: /);
  await line;

  line = say('start');
  await stage('caption', '3. Start the router', 'OpenAI and Anthropic APIs on localhost:5080 · dashboard on /router');
  await sleep(300);
  await type('router', 'semantic-router');
  await waitFor('router', /is listening on/);
  await stage('dash', `${router}/router/#/live`);
  await line;

  // 3. Claude Code, in a second terminal.
  await stage('layout', 'connect');
  await sleep(500);
  line = say('connect');
  await stage('caption', '4. Point Claude Code at it', 'two environment variables · Claude Code works exactly as before');
  await type('claude', `export ANTHROPIC_BASE_URL=${router}`);
  await type('claude', 'export ANTHROPIC_AUTH_TOKEN=semantic-router');
  await type('claude', 'claude --model sonnet');
  await waitFor('claude', /Claude Code v/);
  await line;
  await stage('layout', 'split');
  await sleep(1100);

  // 4. Routing, turn by turn.
  line = say('hello');
  await stage('caption', 'A simple question → a small local model', '');
  let e = await ask('hi! what can you do?', /What would you like to work on/);
  await stage('sub', describe(e));
  await Promise.all([line, sleep(2600)]);
  await sleep(600);

  line = say('code');
  await stage('caption', 'A small coding task → the local coder', '');
  e = await ask('write a unit test for the InvoiceTotal function', /Assert\.Equal/);
  await stage('sub', describe(e));
  await Promise.all([line, sleep(2600)]);
  await sleep(1400);

  line = say('hard');
  await stage('caption', 'A hard debugging question → Claude', '');
  e = await ask('the invoice job deadlocks under load, find the root cause', /three-file change/);
  await stage('sub', describe(e));
  await Promise.all([line, sleep(2600)]);
  await sleep(1400);

  // 5. The Claude session limit runs out: cooldown and failover, inside the same Claude Code session.
  fs.writeFileSync(limitFile, '');
  line = say('limit');
  await stage('caption', 'Claude hits its session limit → you keep working', '');
  e = await ask('why does the cache serve stale data after a deploy?', /Startup\.cs/);
  await stage('sub', `${describe(e)}<span class="tag">Claude cools down until it resets</span>`);
  await Promise.all([line, sleep(3000)]);
  await sleep(1200);

  // 6. The dashboard, with other clients on the same endpoint.
  await stage('hideCaption');
  await stage('layout', 'dash');
  line = say('dashboard');
  await sleep(900);
  await stage('caption', 'Every decision, live', 'any OpenAI or Anthropic client can use the same endpoint');
  const others = ['summarize this email in two lines', 'translate this sentence to French: the meeting moved to Friday',
    'write a regex that matches email addresses', 'thanks a lot!', 'rewrite this paragraph to sound more professional'];
  for (const prompt of others) {
    fetch(`${router}/v1/chat/completions`, { method: 'POST', headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ model: 'auto', messages: [{ role: 'user', content: prompt }] }) }).catch(() => {});
    await sleep(700);
  }
  await line;
  await sleep(600);

  // 7. End card.
  await stage('hideCaption');
  line = say('end');
  await stage('end');
  await line;
  await sleep(1800);

  await cdp.send('Page.stopScreencast');
  await sleep(300);
  shells.claude.write('\x03\x03');
  shells.router.write('\x03');
  await sleep(1500);
  for (const shell of Object.values(shells)) shell.kill();
  await browser.close();
  stub.close(); stageServer.close();
  fs.rmSync(limitFile, { force: true });

  // Each frame lasts until the next one; the narration goes on the same clock (frame time ≈ arrival time - lag).
  const lag = frames.map((f) => f.wall - f.t).sort((a, b) => a - b)[Math.floor(frames.length / 2)];
  const t0 = frames[0].t;
  const lines = [];
  frames.forEach((f, i) => lines.push(`file '${f.file}'`, `duration ${((frames[i + 1]?.t ?? f.t + 1) - f.t).toFixed(4)}`));
  lines.push(`file '${frames[frames.length - 1].file}'`);
  fs.writeFileSync(path.join(out, 'frames.txt'), lines.join('\n') + '\n');
  const timeline = narration.map((n) => ({ key: n.key, start: +(n.wall - lag - t0).toFixed(3), duration: durations[n.key] }));
  fs.writeFileSync(path.join(out, 'timeline.json'), JSON.stringify(timeline, null, 2));
  console.log(`${frames.length} frames, ${(frames[frames.length - 1].t - t0).toFixed(1)} s; narration at ${timeline.map((n) => n.start.toFixed(1)).join(', ')}`);
  process.exit(0);
})().catch((err) => { console.error(err); process.exit(1); });
