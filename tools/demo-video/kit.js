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

/** The rail's section of each dashboard page, as in the dashboard's core.js (SECTIONS). */
const SECTION_OF = {
  overview: 'monitor', live: 'monitor', activity: 'monitor', usage: 'monitor', memory: 'monitor',
  tiers: 'routing', routes: 'routing', routing: 'routing', profiles: 'routing', evaluate: 'routing', learned: 'routing',
  models: 'providers', connectors: 'providers', embeddings: 'providers',
  gateway: 'gateway', clients: 'gateway',
  service: 'admin', json: 'admin', audit: 'admin',
};

/** Whether a dashboard element is on screen, and not under the top bar that stays at the top as the page scrolls. */
const onScreen = (e) => {
  const b = e.getBoundingClientRect();
  const bar = document.querySelector('.topbar')?.getBoundingClientRect().bottom ?? 0;
  return b.top >= bar && b.bottom <= innerHeight;
};

/**
 * Scrolls the dashboard so the element is at the top ('start'), middle or bottom ('end'), eased, setting the position
 * every frame: the browser's own smooth scroll stops short when the dashboard re-renders mid-way, which shows as a jump.
 */
function glide(locator, block) {
  return locator.evaluate((e, b) => new Promise((done) => {
    const r = e.getBoundingClientRect();
    const margin = 12;
    // The top bar (status and page tabs) stays at the top as the page scrolls: aim below it.
    const bar = document.querySelector('.topbar')?.getBoundingClientRect().bottom ?? 0;
    const offset = b === 'start' ? r.top - bar - margin : b === 'end' ? r.bottom - innerHeight + margin : r.top - bar - (innerHeight - bar - r.height) / 2;
    const from = scrollY;
    const to = Math.max(0, Math.min(document.documentElement.scrollHeight - innerHeight, from + offset));
    if (Math.abs(to - from) < 2) { done(); return; }
    const ms = Math.min(1100, 450 + Math.abs(to - from) * 0.4);
    const ease = (t) => (t < 0.5 ? 4 * t * t * t : 1 - (-2 * t + 2) ** 3 / 2);
    const start = performance.now();
    const step = (now) => {
      const t = Math.min(1, (now - start) / ms);
      window.scrollTo({ top: from + (to - from) * ease(t), behavior: 'instant' });
      if (t < 1) requestAnimationFrame(step); else done();
    };
    requestAnimationFrame(step);
  }), block);
}

/**
 * The vertical cut's captions: each narration line as phrases of a few words, as they are read on a phone. A phrase
 * ends at a comma or a sentence end, or after three words or about 16 characters; the product's name is one word.
 */
function captionPhrases(words) {
  const result = {};
  for (const [key, list] of Object.entries(words)) {
    const merged = [];
    for (let i = 0; i < list.length; i++) {
      const [w, s, e] = list[i];
      if (w === 'Semantic' && list[i + 1]?.[0] === 'Router' && list[i + 2]?.[0] === 'dot' && /^net/.test(list[i + 3]?.[0] ?? '')) {
        merged.push([`SemanticRouter.NET${list[i + 3][0].slice(3)}`, s, list[i + 3][2]]);
        i += 3;
      } else merged.push([w, s, e]);
    }
    const phrases = [];
    let phrase = []; let times = [];
    const flush = () => { if (phrase.length) phrases.push([phrase, times]); phrase = []; times = []; };
    for (const [w, s, e] of merged) {
      if (phrase.length >= 3 || (phrase.length && [...phrase, w].join(' ').length > 18)) flush();
      phrase.push(w); times.push([s, e]);
      if (/[.,?!:]$/.test(w)) flush();
    }
    flush();
    result[key] = phrases;
  }
  return result;
}

/**
 * Starts everything a video needs and returns the kit.
 *   out, voiceDir  where frames go, and the narration made by tts.py for this video
 *   shells         [[id, cols, rows], …] for the terminals on the stage (ids match stage.html: router, claude)
 *   loaded         the stand-in LM Studio's models in memory at the start (default: all of them)
 *   paidApi        model ids for a stand-in paid OpenAI-compatible API at http://paid-api.example:4000 (the name
 *                  needs an /etc/hosts entry, which this adds when it can: an address that is not this machine's,
 *                  so the router treats it as a cloud API, and a reserved domain, so nobody mistakes it for one)
 *   fullAnswers    the stand-in models answer at real length (replies.js), for videos that show token counts and costs
 *   vertical       record the 9:16 stage (1080×1920) for Shorts and Reels, with captions that follow the voice word by
 *                  word (words.py times them into the voice folder's words.json)
 */
async function start({ out, voiceDir, shells: shellSpecs = [['router', 96, 26], ['claude', 88, 34]], loaded, paidApi, vertical = false, fullAnswers = false }) {
  if (fullAnswers) process.env.DEMO_FULL_ANSWERS = env.DEMO_FULL_ANSWERS = '1';
  fs.rmSync(path.join(os.tmpdir(), 'semantic-router-demo-cache'), { recursive: true, force: true });
  const durations = JSON.parse(fs.readFileSync(path.join(voiceDir, 'durations.json'), 'utf8'));
  const size = vertical ? { width: 1080, height: 1920 } : { width: 1920, height: 1080 };
  const wordsFile = path.join(voiceDir, 'words.json');
  if (vertical && !fs.existsSync(wordsFile)) throw new Error(`No ${wordsFile}: run words.py on the voice folder first`);
  const words = vertical ? JSON.parse(fs.readFileSync(wordsFile, 'utf8')) : {};
  const phrases = captionPhrases(words);
  prepareHome();
  fs.rmSync(out, { recursive: true, force: true });
  fs.mkdirSync(out, { recursive: true });

  const stub = createStub({ project, loaded });
  await new Promise((r) => stub.listen(1234, '127.0.0.1', r));
  let paid = null;
  if (paidApi) {
    const hosts = fs.readFileSync('/etc/hosts', 'utf8');
    if (!/\spaid-api\.example\b/.test(hosts)) {
      try { fs.appendFileSync('/etc/hosts', '\n127.0.0.1 paid-api.example\n'); } catch {
        throw new Error('Add "127.0.0.1 paid-api.example" to /etc/hosts: the paid API stand-in is served under that name.');
      }
    }
    paid = createStub({ project, models: paidApi, localApi: false });
    await new Promise((r) => paid.listen(4000, '127.0.0.1', r));
  }
  const stageServer = await serveStage();

  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: size });
  // The dashboard forbids framing (frame-ancestors 'none'); lift that for its page loads on this stage only.
  await page.route(`${router}/router/**`, async (route) => {
    if (route.request().resourceType() !== 'document') return route.continue();
    const response = await route.fetch();
    const headers = { ...response.headers() };
    delete headers['content-security-policy'];
    delete headers['x-frame-options'];
    return route.fulfill({ response, headers });
  });
  await page.goto(`http://127.0.0.1:${stageServer.address().port}/stage.html${vertical ? '?vertical' : ''}`);
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

  // The dashboard is drawn scaled in the stage's window: sizes meant in stage pixels are divided by this.
  const dashScale = () => page.evaluate(() => { const i = document.getElementById('dash'); return i.getBoundingClientRect().width / i.offsetWidth; });
  const press = () => k.pointer && k.frame().evaluate(() => new Promise((done) => {
    const c = document.getElementById('demo-pointer');
    if (!c) { done(); return; }
    c.style.transform = 'scale(0.82)';
    setTimeout(() => { c.style.transform = ''; setTimeout(done, 120); }, 130);
  }));
  const glideTo = (y) => k.frame().evaluate((top) => window.scrollTo({ top, behavior: 'instant' }), y);

  const k = {
    home, project, router, limitFile, env, sleep, page, shells, lmstudio: stub, paidApi: paid, paidApiUrl: 'http://paid-api.example:4000/v1',
    stage: (fn, ...args) => page.evaluate(([f, a]) => window.stage[f](...a), [fn, args]),

    /** Starts a narration line; resolves when it has been spoken. */
    say(key) {
      if (!(key in durations)) throw new Error(`No narration line "${key}" in ${voiceDir}`);
      narration.push({ key, wall: Date.now() / 1000 });
      if (phrases[key]) k.stage('speak', phrases[key]);
      return sleep(durations[key] * 1000);
    },
    /**
     * Waits until the line started last with say(key) reaches a word (the first one starting with `word`, after `after`
     * if given), to time a beat to it. Needs the voice folder's words.json (words.py).
     */
    async cue(key, word, after) {
      const list = words[key];
      if (!list) throw new Error(`No word times for "${key}": run words.py on ${voiceDir}`);
      const norm = (w) => w.toLowerCase().replace(/[^a-z0-9']/g, '');
      const from = after ? list.findIndex(([w]) => norm(w).startsWith(after)) + 1 : 0;
      const hit = list.slice(from).find(([w]) => norm(w).startsWith(word));
      if (!hit) throw new Error(`No word "${word}" in "${key}"`);
      const started = narration.findLast((n) => n.key === key).wall;
      await sleep(Math.max(0, (started + hit[1]) * 1000 - Date.now()));
    },
    /** Waits until the line started last with say(key) has this many seconds left, to time a beat to its last words. */
    async secondsLeft(key, seconds) {
      const started = narration.findLast((n) => n.key === key).wall;
      await sleep(Math.max(0, (started + durations[key] - seconds) * 1000 - Date.now()));
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
    /** Types like a person: about 18 characters a second (pace 2 is twice that), then Enter. */
    async type(id, text, enter = true, pace = 1) {
      for (const ch of text) { shells[id].write(ch); await sleep((35 + Math.random() * 45) / pace); }
      if (enter) { await sleep(280); shells[id].write('\r'); }
    },
    /** Runs a command at once, for setup the camera doesn't need to watch. */
    run: (id, command) => shells[id].write(`${command}\r`),

    /**
     * Installs the tool, runs init and starts the router off camera, then lets `configure` adjust the configuration.
     * Without a terminal init adds the subscription CLIs it finds disabled: --enable-cli turns Claude on, as answering
     * its question would.
     */
    async startRouter(configure) {
      const shellEnv = { ...env };
      // Asynchronously: init probes LM Studio, and the stand-in answers from this process.
      await new Promise((resolve, reject) => {
        spawn('bash', ['-c', 'dotnet tool install -g SemanticRouter.Host >/dev/null && semantic-router init --enable-cli claude >/dev/null'], { cwd: project, env: shellEnv, stdio: 'inherit' })
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
      await k.point(field);
      await field.fill('');
      await field.pressSequentially(text, { delay: text.length > 80 ? 8 : 28 });
      // Keystrokes that land while the dashboard re-renders are lost if it drops the focus: fail the take, not the viewer.
      const typed = await field.inputValue();
      if (typed !== text) throw new Error(`Typed "${text}" but the field holds "${typed}"`);
      // Settings fields take their value on change (when you leave them).
      await field.evaluate((e) => e.dispatchEvent(new Event('change', { bubbles: true })));
    },
    async dashCheck(selector, on = true) {
      const target = k.frame().locator(selector).first();
      await k.point(target);
      await press();
      await target.setChecked(on);
    },
    /** Clicks a dashboard element, the pointer going there first. */
    async dashClick(selector) {
      const target = k.frame().locator(selector).first();
      await k.point(target);
      await press();
      await target.click();
    },
    async dashSelect(selector, value) {
      const target = k.frame().locator(selector).first();
      await k.point(target);
      await target.selectOption(value);
    },
    /**
     * Opens a dashboard page as a person would: its section in the rail, then its tab. A section opens on the page last
     * shown in it (or its first), so that tab may already be the one.
     */
    async nav(view) {
      await k.unhighlight();
      const tab = `#tabs a[data-view="${view}"]`;
      if (!(await k.frame().locator(tab).count())) {
        await k.dashClick(`#nav a[data-section="${SECTION_OF[view]}"]`);
        await k.frame().locator(tab).first().waitFor();
        await sleep(400);
      }
      if (!(await k.frame().locator(`${tab}.active`).count())) await k.dashClick(tab);
      await k.frame().waitForFunction((v) => location.hash === `#/${v}`, view);
      await k.stage('url', `${router}/router/#/${view}`);
      await glideTo(0);
      await sleep(700);
    },
    /** Whether clicks and typing in the dashboard show a mouse pointer moving there (the how-to turns it on). */
    pointer: false,
    /** Moves the pointer onto an element (a locator in the dashboard), scrolling it into view first if need be. */
    async point(target) {
      if (!k.pointer) return;
      const visible = await target.evaluate(onScreen);
      // A scroll takes the page away from what a highlight outlines: drop it first.
      if (!visible) { await k.unhighlight(); await glide(target, 'center'); await k.settle(); }
      const scale = await dashScale();
      await target.evaluate((e, s) => new Promise((done) => {
        let c = document.getElementById('demo-pointer');
        if (!c) {
          c = document.createElement('div');
          c.id = 'demo-pointer';
          c.innerHTML = '<svg viewBox="0 0 16 24" width="100%" height="100%"><path d="M1 1 L1 19 L5.5 14.5 L8.5 22 L11.5 20.8 L8.6 13.5 L14.5 13.5 Z" fill="#fff" stroke="#111" stroke-width="1.4" stroke-linejoin="round"/></svg>';
          Object.assign(c.style, {
            position: 'fixed', zIndex: 2147483647, pointerEvents: 'none', width: `${20 / s}px`, height: `${30 / s}px`,
            left: `${innerWidth * 0.6}px`, top: `${innerHeight * 0.55}px`, filter: 'drop-shadow(0 1px 2px rgba(0,0,0,.5))',
            transition: 'left 650ms cubic-bezier(.4,0,.2,1), top 650ms cubic-bezier(.4,0,.2,1), transform 120ms ease',
          });
          document.documentElement.appendChild(c);
          c.getBoundingClientRect();
        }
        const b = e.getBoundingClientRect();
        c.style.left = `${b.left + Math.min(b.width / 2, 48)}px`;
        c.style.top = `${b.top + b.height / 2}px`;
        setTimeout(done, 700);
      }), scale);
    },
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
    /** Scrolls the dashboard to another part of the page; the current highlight belongs to the part it leaves. */
    async dashScroll(selector, block = 'center') {
      await k.unhighlight();
      await glide(k.frame().locator(selector).first(), block);
      await k.settle();
    },
    /**
     * Outlines a dashboard element. The box is drawn inside the dashboard page and redrawn every frame, so it moves
     * with the element as the page scrolls or re-renders, never a frame behind. Sizes are in stage pixels.
     */
    async highlight(selector, pad = 8) {
      if (tracker) await k.unhighlight();
      // Bring it into view first: the box follows a scroll, but a still page reads better.
      const target = k.frame().locator(selector).first();
      const visible = await target.evaluate(onScreen);
      if (!visible) await glide(target, 'center');
      await k.settle();
      const scale = await dashScale();
      await target.evaluate((e, { pad, s }) => {
        const box = document.createElement('div');
        Object.assign(box.style, {
          position: 'fixed', zIndex: 2147483647, pointerEvents: 'none', opacity: '0', transition: 'opacity 250ms ease',
          border: `${3 / s}px solid #d08c34`, borderRadius: `${10 / s}px`,
          boxShadow: `0 0 0 9999px rgba(5, 8, 10, 0.45), 0 0 ${24 / s}px rgba(208, 140, 52, 0.55)`,
        });
        document.documentElement.appendChild(box);
        const hl = window.demoHighlight = { el: e, box };
        const p = pad / s;
        const draw = () => {
          if (window.demoHighlight !== hl) return;
          // A re-render replaces the element; until it is found again, the box stays where it was.
          if (!hl.el.isConnected) { requestAnimationFrame(draw); return; }
          const b = hl.el.getBoundingClientRect();
          Object.assign(box.style, { left: `${b.left - p}px`, top: `${b.top - p}px`, width: `${Math.min(b.width, innerWidth - b.left - 8) + 2 * p}px`, height: `${b.height + 2 * p}px` });
          requestAnimationFrame(draw);
        };
        draw();
        requestAnimationFrame(() => { box.style.opacity = '1'; });
      }, { pad, s: scale });
      // A re-render can replace the element: point the box at whatever the selector finds now.
      tracker = setInterval(() => target.evaluate((e) => { if (window.demoHighlight) window.demoHighlight.el = e; }).catch(() => {}), 100);
    },
    async unhighlight() {
      if (!tracker) return;
      clearInterval(tracker);
      tracker = null;
      await k.frame()?.evaluate(() => {
        const hl = window.demoHighlight;
        window.demoHighlight = null;
        if (hl) { hl.box.style.opacity = '0'; setTimeout(() => hl.box.remove(), 300); }
      }).catch(() => {});
      await sleep(250);
    },

    async record() {
      cdp.on('Page.screencastFrame', async ({ data, metadata, sessionId }) => {
        const file = `${String(frames.length).padStart(5, '0')}.jpg`;
        frames.push({ file, t: metadata.timestamp, wall: Date.now() / 1000 });
        fs.writeFileSync(path.join(out, file), Buffer.from(data, 'base64'));
        try { await cdp.send('Page.screencastFrameAck', { sessionId }); } catch { /* closing */ }
      });
      await cdp.send('Page.startScreencast', { format: 'jpeg', quality: 92, maxWidth: size.width, maxHeight: size.height, everyNthFrame: 1 });
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
      stub.close(); stageServer.close(); paid?.close();
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
      fs.writeFileSync(path.join(out, 'stage.json'), JSON.stringify(size));
      console.log(`${frames.length} frames, ${(frames[frames.length - 1].t - t0).toFixed(1)} s; narration at ${timeline.map((n) => n.start.toFixed(1)).join(', ')}`);
    },
  };
  return k;
}

module.exports = { start };
