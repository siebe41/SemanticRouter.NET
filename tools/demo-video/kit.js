// The recording kit shared by every video in videos/: a throw-away demo user, the stand-in models, real shells in
// pseudo-terminals shown with xterm.js on stage.html, the router's dashboard in a browser window, a screencast of the
// stage, and a clock that places each narration line on the video. A video is a function that receives the kit.
//
//   DEMO_HOME         the demo user's home directory (default /home/demo; it is deleted first)
//   DEMO_FEED         a folder holding a SemanticRouter.Host .nupkg to install instead of nuget.org's
//   DEMO_REAL_CLAUDE  the real Claude Code binary (default: `claude` on PATH)
const fs = require('fs');
const http = require('http');
const os = require('os');
const path = require('path');
const { execFileSync, spawn } = require('child_process');
const pty = require('node-pty');
const { chromium } = require('playwright');
const { createStub } = require('./lmstudio-stub');

const here = __dirname;
const home = process.env.DEMO_HOME || '/home/demo';
const project = path.join(home, 'billing');
const router = 'http://localhost:5080';
const limitFile = path.join(os.tmpdir(), 'semantic-router-demo-claude-limit');
const which = (cmd) => execFileSync('bash', ['-c', `command -v ${cmd}`]).toString().trim();
const realClaude = process.env.DEMO_REAL_CLAUDE || fs.realpathSync(which('claude'));
const dotnetDir = process.env.DOTNET_ROOT || path.dirname(fs.realpathSync(which('dotnet')));
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// Only what the demo user's shell needs: none of the recording machine's own variables or credentials.
const env = {
  HOME: home, TERM: 'xterm-256color', LANG: 'C.UTF-8',
  PATH: [path.join(home, '.local', 'bin'), path.join(home, '.dotnet', 'tools'), dotnetDir, path.dirname(process.execPath), '/usr/local/bin', '/usr/bin', '/bin'].join(':'),
  DOTNET_ROOT: dotnetDir, DOTNET_NOLOGO: '1', DOTNET_CLI_TELEMETRY_OPTOUT: '1',
  DISABLE_AUTOUPDATER: '1', CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1',
  DEMO_REAL_CLAUDE: realClaude, DEMO_CLAUDE_LIMIT_FILE: limitFile,
  PS1: '\\[\\e[38;5;38m\\]~/billing\\[\\e[0m\\] \\[\\e[38;5;179m\\]$\\[\\e[0m\\] ',
};

// A fresh demo user: no tool, no router configuration, Claude Code's first-run notices already seen, and a small
// project for Claude Code to work in.
function prepareHome() {
  fs.rmSync(home, { recursive: true, force: true });
  fs.mkdirSync(path.join(home, '.local', 'bin'), { recursive: true });
  fs.mkdirSync(project, { recursive: true });
  fs.symlinkSync(path.join(here, 'fake-claude', 'claude'), path.join(home, '.local', 'bin', 'claude'));
  if (process.env.DEMO_FEED) {
    fs.writeFileSync(path.join(project, 'nuget.config'),
      `<?xml version="1.0" encoding="utf-8"?>\n<configuration><packageSources><clear /><add key="local" value="${path.resolve(process.env.DEMO_FEED)}" /></packageSources></configuration>\n`);
  }
  fs.writeFileSync(path.join(project, 'README.md'), '# billing\n\nBuilds invoices from ledger events, applies tax per line and converts currencies through the FX client.\n\n'
    + 'Open risks: duplicate invoices when the ledger replays events, and account numbers reused after closure.\n');
  fs.writeFileSync(path.join(home, '.claude.json'), JSON.stringify({
    hasCompletedOnboarding: true, theme: 'dark', numStartups: 9, autoUpdates: false, migrationVersion: 14,
    opusProMigrationComplete: true, sonnet1m45MigrationComplete: true, hasResetAutoModeOptInForDefaultOffer: true,
    hasSeenAutoDefaultNotice: true, hasSeenAutoDefaultNudge: true, hasSeenAutoModeEntryWarning: true,
    lastReleaseNotesSeen: execFileSync(realClaude, ['--version']).toString().split(' ')[0],
    projects: { [project]: { hasTrustDialogAccepted: true, hasCompletedProjectOnboarding: true, allowedTools: [] } },
  }, null, 2));
  fs.rmSync(limitFile, { force: true });
}

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

/**
 * Starts everything a video needs and returns the kit.
 *   out, voiceDir  where frames go, and the narration made by tts.py for this video
 *   shells         [[id, cols, rows], …] for the terminals on the stage (ids match stage.html: router, claude)
 */
async function start({ out, voiceDir, shells: shellSpecs = [['router', 96, 26], ['claude', 88, 34]] }) {
  const durations = JSON.parse(fs.readFileSync(path.join(voiceDir, 'durations.json'), 'utf8'));
  prepareHome();
  fs.rmSync(out, { recursive: true, force: true });
  fs.mkdirSync(out, { recursive: true });

  const stub = createStub({ project });
  await new Promise((r) => stub.listen(1234, '127.0.0.1', r));
  const stageServer = await serveStage();

  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1920, height: 1080 } });
  // The dashboard forbids framing (frame-ancestors 'none'); lift that for its page loads on this stage only.
  await page.route(`${router}/router/**`, async (route) => {
    if (route.request().resourceType() !== 'document') return route.continue();
    const response = await route.fetch();
    const headers = { ...response.headers() };
    delete headers['content-security-policy'];
    delete headers['x-frame-options'];
    return route.fulfill({ response, headers });
  });
  await page.goto(`http://127.0.0.1:${stageServer.address().port}/stage.html`);
  await page.waitForFunction(() => window.stage);

  // Shells, each mirrored into its xterm on the stage.
  const shells = {};
  const timers = [];
  for (const [id, cols, rows] of shellSpecs) {
    await page.evaluate(([i, c, r]) => window.stage.terminal(i, c, r), [id, cols, rows]);
    const shell = pty.spawn('bash', ['--noprofile', '--norc', '-i'], { name: 'xterm-256color', cols, rows, cwd: project, env });
    let pending = '';
    shell.onData((d) => { pending += d; });
    timers.push(setInterval(() => {
      if (!pending) return;
      const data = pending; pending = '';
      page.evaluate(([i, d]) => window.stage.write(i, d), [id, data]).catch(() => {});
    }, 16));
    shells[id] = shell;
  }

  const cdp = await page.context().newCDPSession(page);
  const frames = [];
  const narration = [];
  let background = null;
  let tracker = null;

  const k = {
    home, project, router, limitFile, env, sleep, page, shells,
    stage: (fn, ...args) => page.evaluate(([f, a]) => window.stage[f](...a), [fn, args]),

    /** Starts a narration line; resolves when it has been spoken. */
    say(key) {
      if (!(key in durations)) throw new Error(`No narration line "${key}" in ${voiceDir}`);
      narration.push({ key, wall: Date.now() / 1000 });
      return sleep(durations[key] * 1000);
    },

    screen: (id) => page.evaluate((i) => window.stage.screen(i), id),
    async waitFor(id, pattern, timeout = 60000) {
      const until = Date.now() + timeout;
      while (Date.now() < until) {
        if (pattern.test(await k.screen(id))) return;
        await sleep(150);
      }
      throw new Error(`Timed out waiting for ${pattern} in the ${id} terminal:\n${await k.screen(id)}`);
    },
    /** Types like a person: about 18 characters a second, then Enter. */
    async type(id, text, enter = true) {
      for (const ch of text) { shells[id].write(ch); await sleep(35 + Math.random() * 45); }
      if (enter) { await sleep(280); shells[id].write('\r'); }
    },
    /** Runs a command at once, for setup the camera doesn't need to watch. */
    run: (id, command) => shells[id].write(`${command}\r`),

    /** Installs the tool, runs init and starts the router off camera, then lets `configure` adjust the configuration. */
    async startRouter(configure) {
      const shellEnv = { ...env };
      // Asynchronously: init probes LM Studio, and the stand-in answers from this process.
      await new Promise((resolve, reject) => {
        spawn('bash', ['-c', 'dotnet tool install -g SemanticRouter.Host >/dev/null && semantic-router init >/dev/null'], { cwd: project, env: shellEnv, stdio: 'inherit' })
          .on('exit', (code) => (code === 0 ? resolve() : reject(new Error(`install/init exited with ${code}`))));
      });
      background = spawn('semantic-router', [], { cwd: project, env: shellEnv, stdio: 'ignore' });
      for (let i = 0; i < 120; i++) {
        try { if ((await fetch(`${router}/router/api/status`)).ok) break; } catch { /* starting */ }
        await sleep(500);
      }
      if (configure) {
        const { config } = await k.api('GET', '/config');
        configure(config);
        await k.api('PUT', '/config?persist=true', config);
      }
    },
    async api(method, apiPath, body) {
      const res = await fetch(`${router}/router/api${apiPath}`, { method, headers: { 'content-type': 'application/json' }, body: body && JSON.stringify(body) });
      if (!res.ok) throw new Error(`${method} ${apiPath}: ${res.status} ${await res.text()}`);
      return res.json();
    },
    /** Sends a prompt as an OpenAI client would, without waiting for the answer. */
    send(prompt, model = 'auto') {
      return fetch(`${router}/v1/chat/completions`, { method: 'POST', headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ model, messages: [{ role: 'user', content: prompt }] }) }).then((r) => r.text()).catch(() => {});
    },

    /** The router's activity log, newest first. */
    async activity(limit = 30) {
      const res = await k.api('GET', `/activity?limit=${limit}`);
      return Array.isArray(res) ? res : res.entries || res.items || [];
    },
    /** What the router did with a prompt, from its own activity log. */
    async decision(prompt) {
      for (let i = 0; i < 20; i++) {
        const rows = await k.activity();
        const hit = rows.find((e) => e.promptPreview?.startsWith(prompt.slice(0, 30)) && e.outcome === 'served' && !e.toolContinuation);
        if (hit) return hit;
        await sleep(250);
      }
      return null;
    },
    isLocal: (model) => model?.startsWith('lmstudio'),
    describe(e) {
      if (!e) return '';
      const cls = k.isLocal(e.servedBy) ? 'local' : 'cloud';
      const tag = k.isLocal(e.servedBy) ? '<span class="tag local">local · free</span>' : '<span class="tag cloud">Claude subscription</span>';
      const failed = (e.sentTo || []).filter((m) => m !== e.servedBy).map((m) => `<span class="fail">${m}</span> `).join('');
      return `${failed}→ <span class="${cls}">${e.servedBy}</span>  ·  tier ${e.tier}  ·  ${e.route || e.reason}${tag}`;
    },
    /** Types a prompt into Claude Code, waits for its answer and returns the router's decision. */
    async ask(prompt, done) {
      await k.type('claude', prompt);
      await k.waitFor('claude', done);
      return k.decision(prompt.replace(/^@\S+\s+/, ''));
    },

    // The dashboard, in the stage's browser window.
    frame: () => page.frames().find((f) => f.url().startsWith(`${router}/router`)),
    async go(view) {
      await k.unhighlight();
      await k.stage('dash', `${router}/router/#/${view}`);
      await page.waitForFunction((u) => document.getElementById('dash').src === u, `${router}/router/#/${view}`);
      await sleep(900);
    },
    /** Types into a dashboard field; long text goes faster, so it doesn't outlast its narration. */
    async dashType(selector, text) {
      const field = k.frame().locator(selector).first();
      await field.fill('');
      await field.pressSequentially(text, { delay: text.length > 80 ? 8 : 28 });
    },
    dashClick: (selector) => k.frame().locator(selector).first().click(),
    dashSelect: (selector, value) => k.frame().locator(selector).first().selectOption(value),
    /** Waits until the dashboard has stopped scrolling (smooth scrolls take a variable time). */
    async settle() {
      let last = null; let still = 0;
      for (let i = 0; i < 40 && still < 3; i++) {
        const y = await k.frame().evaluate(() => scrollY);
        still = y === last ? still + 1 : 0;
        last = y;
        await sleep(100);
      }
    },
    /** Scrolls the dashboard; a highlight would stay where it was on screen while the page moves, so it goes first. */
    async dashScroll(selector, block = 'center') {
      await k.unhighlight();
      await k.frame().locator(selector).first().evaluate((e, b) => e.scrollIntoView({ behavior: 'smooth', block: b }), block);
      await sleep(300);
      await k.settle();
    },
    /**
     * Outlines a dashboard element on the stage, and keeps following it: the dashboard re-renders as status arrives,
     * and the element can move under a box drawn once.
     */
    async highlight(selector, pad = 8) {
      if (tracker) await k.unhighlight();
      // Bring it into view first, and measure only once the page has stopped scrolling.
      const target = k.frame().locator(selector).first();
      const visible = await target.evaluate((e) => { const b = e.getBoundingClientRect(); return b.top >= 0 && b.bottom <= innerHeight; });
      if (!visible) {
        await k.unhighlight();
        await target.evaluate((e) => e.scrollIntoView({ behavior: 'smooth', block: 'center' }));
        await sleep(300);
      }
      await k.settle();
      const measure = async () => {
        const r = await target.evaluate((e) => { const b = e.getBoundingClientRect(); return { x: b.left, y: b.top, w: Math.min(b.width, innerWidth - b.left - 8), h: b.height }; });
        const f = await page.evaluate(() => { const i = document.getElementById('dash'); const b = i.getBoundingClientRect(); return { x: b.left, y: b.top, s: b.width / i.offsetWidth }; });
        await k.stage('highlight', f.x + r.x * f.s - pad, f.y + r.y * f.s - pad, r.w * f.s + 2 * pad, r.h * f.s + 2 * pad);
      };
      await measure();
      tracker = setInterval(() => measure().catch(() => {}), 100);
    },
    async unhighlight() {
      if (tracker) { clearInterval(tracker); tracker = null; }
      await k.stage('unhighlight');
      await sleep(250);
    },

    async record() {
      cdp.on('Page.screencastFrame', async ({ data, metadata, sessionId }) => {
        const file = `${String(frames.length).padStart(5, '0')}.jpg`;
        frames.push({ file, t: metadata.timestamp, wall: Date.now() / 1000 });
        fs.writeFileSync(path.join(out, file), Buffer.from(data, 'base64'));
        try { await cdp.send('Page.screencastFrameAck', { sessionId }); } catch { /* closing */ }
      });
      await cdp.send('Page.startScreencast', { format: 'jpeg', quality: 92, maxWidth: 1920, maxHeight: 1080, everyNthFrame: 1 });
      await sleep(300);
    },

    /** Stops recording, shuts everything down and writes frames.txt and timeline.json for build.js. */
    async finish() {
      await cdp.send('Page.stopScreencast');
      await sleep(300);
      if (shells.claude) shells.claude.write('\x03\x03');
      if (shells.router) shells.router.write('\x03');
      await sleep(1500);
      for (const shell of Object.values(shells)) shell.kill();
      timers.forEach(clearInterval);
      if (background) background.kill();
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
      const timeline = narration.map((n) => ({ key: n.key, start: Math.max(0, +(n.wall - lag - t0).toFixed(3)), duration: durations[n.key] }));
      fs.writeFileSync(path.join(out, 'timeline.json'), JSON.stringify(timeline, null, 2));
      console.log(`${frames.length} frames, ${(frames[frames.length - 1].t - t0).toFixed(1)} s; narration at ${timeline.map((n) => n.start.toFixed(1)).join(', ')}`);
    },
  };
  return k;
}

module.exports = { start };
