// End-to-end tests of the dashboard's pages: first-run setup, the phone layout, Activity, settings fields and wording.
// Endpoints newer than the router under test (GET api/setup, POST api/connectors/discover) are answered with
// page.route, so these tests pass against routers with and without them.
const fs = require('fs');
const path = require('path');
const { test: base, expect } = require('@playwright/test');

const stub = `http://127.0.0.1:${Number(process.env.E2E_PORT || 5190) + 1}`;
const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'semantic-router.json'), 'utf8').replace(/__STUB__/g, stub));

// Every page, in rail order (Feedback, at the foot of the rail, last).
const PAGES = ['overview', 'live', 'activity', 'usage', 'memory', 'tiers', 'routes', 'routing', 'profiles', 'evaluate', 'learned', 'models', 'connectors', 'embeddings', 'gateway', 'clients', 'service', 'json', 'audit', 'feedback'];

// As in dashboard.spec.js: a test fails if the page throws, logs an error or asks another host for anything. A test
// that expects a 4xx on purpose (an older router's 404, the gateway's refusal) lets the browser's "Failed to load
// resource" through.
const expects4xx = new WeakSet();
const test = base.extend({
  page: async ({ page, baseURL }, use) => {
    const errors = [];
    const own = new URL(baseURL).origin;
    page.on('request', (r) => {
      const url = r.url();
      if (!url.startsWith('data:') && !url.startsWith('blob:') && new URL(url).origin !== own) errors.push(`request to another host: ${url}`);
    });
    page.on('pageerror', (e) => errors.push(`pageerror: ${e.message}`));
    page.on('console', (m) => { if (m.type() === 'error' && !(expects4xx.has(page) && /status of 4\d\d/.test(m.text()))) errors.push(`console: ${m.text()}`); });
    await use(page);
    expect(errors, 'the dashboard logged errors').toEqual([]);
  },
});

test.beforeEach(async ({ request }) => {
  const reset = await request.put('/router/api/config?persist=false', { data: fixture });
  expect(reset.ok(), await reset.text()).toBeTruthy();
});

async function open(page, view) {
  await page.goto(`/router#/${view}`);
  await expect(page.locator('#view h1').first()).toBeVisible();
}

/** Loads the dashboard afresh (after the test changed the configuration behind its back). */
async function reopen(page, view) {
  await open(page, view);
  await page.reload();
  await expect(page.locator('#view h1').first()).toBeVisible();
}

async function expandAll(page) {
  const closed = page.locator('.item-head[aria-expanded="false"]:visible');
  for (let i = 0; i < 50 && await closed.count() > 0; i++) await closed.first().click();
  await page.evaluate(() => document.querySelectorAll('#view details').forEach((d) => { d.open = true; }));
}

/** How far the page scrolls sideways: 0 when everything fits the window. */
const sideways = (page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

async function route(request, prompt) {
  const res = await request.post('/v1/chat/completions', { data: { model: 'auto', messages: [{ role: 'user', content: prompt }] } });
  expect(res.ok(), await res.text()).toBeTruthy();
}

/* ---------------------------------------------------------------------------------------------
   First run
   --------------------------------------------------------------------------------------------- */
// A router with nothing to route to: no connectors, no models, nothing that needs them.
const empty = { ...fixture, Connectors: {}, Models: [], Profiles: {} };
const lmstudio = { Type: 'OpenAICompatible', BaseAddress: stub, RequestPath: '/v1/chat/completions' };
const SETUP = {
  needed: true,
  detected: [],
  presets: [
    { id: 'lmstudio', title: 'LM Studio', detail: 'Local models from LM Studio\'s server', kind: 'local', needsKey: false, connector: { name: 'lmstudio', settings: lmstudio } },
    { id: 'openai', title: 'OpenAI', detail: 'GPT models with an API key', kind: 'cloud', needsKey: true, connector: { name: 'openai', settings: { Type: 'OpenAICompatible', BaseAddress: 'https://api.openai.com', ApiKey: '${env:OPENAI_API_KEY}' } } },
  ],
};
const LINEUP = {
  models: ['stub-small'], note: null, server: 'LM Studio', loading: 'OnDemand', loadingNote: null,
  lineup: [{ id: 'stub-small', kind: 'Chat', listed: true, loaded: true, inCatalog: null, recommended: true, reason: 'a small model → tier 1.',
    suggestion: { Name: 'stub-small', Connector: 'lmstudio', Model: 'stub-small', Tier: 1, Capabilities: ['chat'], IsLocal: true, Enabled: true } }],
};

test('with no model enabled, the Overview walks through setup: connector, models, a test prompt', async ({ page, request }) => {
  expect((await request.put('/router/api/config?persist=false', { data: empty })).ok()).toBeTruthy();
  await page.route('**/router/api/setup', (r) => r.fulfill({ json: SETUP }));
  let discovered = null;
  await page.route('**/router/api/connectors/discover', (r) => { discovered = r.request().postDataJSON(); return r.fulfill({ json: LINEUP }); });
  await open(page, 'overview');

  const card = page.locator('#setup');
  await expect(card.locator('h2')).toHaveText('Set up SemanticRouter');
  await expect(card.locator('.step.current .step-head')).toContainText('Add a connector');
  // The "no model is enabled" message is the card's, not repeated in the service card.
  await expect(page.locator('#service')).not.toContainText(/no (model is enabled|enabled model)/i);
  await expect(card.locator('.choice', { has: page.locator('b', { hasText: /^OpenAI$/ }) })).toContainText('needs an API key');

  await card.locator('.choice', { hasText: 'LM Studio' }).getByRole('button', { name: 'Add' }).click();
  await expect(page.locator('#toast')).toContainText('LM Studio');
  await expect(card.locator('.step.current .step-head')).toContainText('Add models');

  // Discover models asks with the draft's settings: the connector is not applied yet.
  await card.locator('[data-action="lineup-discover"]').click();
  expect(discovered).toMatchObject({ name: 'lmstudio', settings: { BaseAddress: stub } });
  await expect(card.locator('input[aria-label="Add stub-small"]')).toBeChecked();
  await card.locator('[data-action="lineup-add"]').click();
  await card.locator('[data-action="apply-save"]').click();
  await expect(card.locator('.step.current .step-head')).toContainText('Send a test prompt');

  await card.locator('[data-action="setup-test"]').click();
  await expect(card.locator('h2')).toHaveText('SemanticRouter is set up');
  await expect(page.locator('.tester .reply')).toContainText('Hello from the stub backend.');
  await card.getByRole('button', { name: 'Done' }).click();
  await expect(page.locator('#setup')).toHaveCount(0);
});

test('a tool found on this machine comes first, once, and the other presets fold away', async ({ page, request }) => {
  expect((await request.put('/router/api/config?persist=false', { data: empty })).ok()).toBeTruthy();
  const claude = { id: 'claude-code', title: 'Claude Code', kind: 'cli', detail: 'Signed in', consent: 'Uses your Claude subscription.', connector: { name: 'claude-cli', settings: { Type: 'Cli', Preset: 'ClaudeCode' } }, models: [] };
  const presetClaude = { ...claude, detail: 'Your Claude subscription through the claude CLI' };
  await page.route('**/router/api/setup', (r) => r.fulfill({ json: { ...SETUP, detected: [claude], presets: [...SETUP.presets, presetClaude] } }));
  await open(page, 'overview');
  const card = page.locator('#setup');
  await expect(card.locator('.choice b', { hasText: /^Claude Code$/ })).toHaveCount(1);
  await expect(card.locator('.choice', { hasText: 'Claude Code' })).toContainText('found');
  const others = card.locator('details.advanced', { has: page.locator('summary', { hasText: 'Other providers (2)' }) });
  await expect(others).not.toHaveAttribute('open', '');
  await expect(card.locator('.choice b', { hasText: /^LM Studio$/ })).toBeHidden();
  await others.locator('summary').click();
  await expect(card.locator('.choice b', { hasText: /^LM Studio$/ })).toBeVisible();
  // The empty Connectors page lists everything unfolded, still without the duplicate.
  await open(page, 'connectors');
  await expect(page.locator('#view .empty-state .choice')).toHaveText([/Claude Code/, /LM Studio/, /OpenAI/, /Blank connector/]);
});

test('a CLI found only as a script wrapper says what to do, with no Enable button, and keeps its preset', async ({ page, request }) => {
  expect((await request.put('/router/api/config?persist=false', { data: empty })).ok()).toBeTruthy();
  const codex = {
    id: 'codex', title: 'Codex', kind: 'cli', detail: 'Found C:\\Users\\ada\\AppData\\Roaming\\npm\\codex.cmd', models: [],
    problem: 'only a script wrapper was found (C:\\Users\\ada\\AppData\\Roaming\\npm\\codex.cmd). Install the native binary (codex.exe).',
    connector: { name: 'codex-cli', settings: { Type: 'Cli', Preset: 'Codex' } },
  };
  const presetCodex = { ...codex, problem: undefined, detail: 'Your ChatGPT plan through the codex CLI' };
  await page.route('**/router/api/setup', (r) => r.fulfill({ json: { ...SETUP, detected: [codex], presets: [...SETUP.presets, presetCodex] } }));
  await open(page, 'overview');
  const card = page.locator('#setup');
  const found = card.locator('.choice', { hasText: 'codex.cmd' });
  await expect(found).toContainText("can't be used as installed");
  await expect(found).toContainText('Install the native binary (codex.exe)');
  await expect(found.locator('[data-action="setup-choose"]')).toHaveCount(0);
  await card.locator('details.advanced summary', { hasText: 'Other providers (3)' }).click();
  await expect(card.locator('.choice', { hasText: 'Your ChatGPT plan' }).locator('[data-action="setup-choose"]')).toHaveText('Add');
});

test('the setup card lists the router\'s own presets, and a gateway refusal says where to fix it', async ({ page, request }) => {
  expects4xx.add(page); // the gateway refuses the Send with a 400
  const setup = await request.get('/router/api/setup');
  test.skip(setup.status() === 404, 'this router has no setup endpoint');
  expect((await request.put('/router/api/config?persist=false', { data: empty })).ok()).toBeTruthy();
  await open(page, 'overview');
  const card = page.locator('#setup');
  await expect(card.locator('h2')).toHaveText('Set up SemanticRouter');
  // On a machine where a CLI was found, the other presets are folded under "Other providers".
  const others = card.locator('details.advanced > summary', { hasText: 'Other providers' });
  if (await others.count()) await others.click();
  await expect(card.locator('.choice b', { hasText: /^LM Studio$/ })).toBeVisible();
  await expect(card.locator('.choice b', { hasText: /^OpenAI$/ })).toBeVisible();
  // The banner leaves "no model is enabled" to the card here.
  await expect(page.locator('#banner')).not.toContainText(/no model is enabled/i);
  await page.locator('#tester-prompt').fill('hello there');
  await page.locator('[data-action="tester-send"]').click();
  await expect(page.locator('.tester .next-step')).toBeVisible();
  await expect(page.locator('.tester .next-step a').first()).toHaveAttribute('href', '#/overview');
});

test('without the setup endpoint the checklist still shows, pointing to Connectors', async ({ page, request }) => {
  expect((await request.put('/router/api/config?persist=false', { data: empty })).ok()).toBeTruthy();
  expects4xx.add(page);
  await page.route('**/router/api/setup', (r) => r.fulfill({ status: 404, json: { title: 'Not Found' } }));
  await open(page, 'overview');
  const card = page.locator('#setup');
  await expect(card.locator('.step')).toHaveCount(3);
  await expect(card.locator('a[href="#/connectors"]')).toBeVisible();
});

test('a read-only user is told an admin needs to add a model', async ({ page, request }) => {
  expect((await request.put('/router/api/config?persist=false', { data: empty })).ok()).toBeTruthy();
  await page.route('**/router/api/session', (r) => (r.request().method() === 'GET'
    ? r.fulfill({ json: { role: 'ReadOnly', tokenRequired: true, tokensConfigured: true, identity: 'read-only token' } }) : r.continue()));
  await open(page, 'overview');
  await expect(page.locator('#setup')).toContainText(/an admin needs to add one/i);
  await expect(page.locator('#setup [data-action]')).toHaveCount(0);
});

test('Add connector offers the presets and a blank connector; Models needs a connector first', async ({ page, request }) => {
  await page.route('**/router/api/setup', (r) => r.fulfill({ json: { ...SETUP, needed: false } }));
  await open(page, 'connectors');
  await page.locator('details.menu > summary', { hasText: 'Add connector' }).click();
  const menu = page.locator('details.menu .menu-panel');
  await expect(menu.locator('.choice')).toHaveText([/LM Studio/, /OpenAI/, /Blank connector/]);
  await menu.locator('.choice', { has: page.locator('b', { hasText: /^OpenAI$/ }) }).getByRole('button', { name: 'Add' }).click();
  await expect(page.locator('.item.open .item-head')).toContainText('openai');
  await expect(page.locator('.item.open')).toContainText('${env:OPENAI_API_KEY}');

  expect((await request.put('/router/api/config?persist=false', { data: empty })).ok()).toBeTruthy();
  await reopen(page, 'models');
  await expect(page.getByRole('button', { name: 'Add model' })).toBeDisabled();
  await expect(page.locator('#view')).toContainText('a connector is how it reaches the provider');
  await open(page, 'connectors');
  await expect(page.locator('#view .empty-state .choice')).toHaveText([/LM Studio/, /OpenAI/, /Blank connector/]);
});

/* ---------------------------------------------------------------------------------------------
   Phone layout
   --------------------------------------------------------------------------------------------- */
test.describe('on a phone', () => {
  test.use({ viewport: { width: 390, height: 844 } });

  test('no page scrolls sideways, with every item and section open', async ({ page, request }) => {
    test.setTimeout(120_000); // every page, twice
    await page.route('**/router/api/setup', (r) => r.fulfill({ json: { ...SETUP, needed: false } })); // the Add connector menu opens
    await route(request, 'hello there, how are you?');
    await route(request, 'please design a scalable distributed architecture for an event sourcing system');
    const wide = {};
    for (const view of PAGES) {
      await open(page, view);
      await page.waitForLoadState('networkidle');
      let over = await sideways(page);
      if (over <= 0) { await expandAll(page); over = await sideways(page); }
      if (over > 0) wide[view] = over;
    }
    expect(wide, 'pages wider than the phone, in px').toEqual({});
  });

  test('the Overview keeps two stat tiles abreast and the Anthropic base URL in view', async ({ page }) => {
    await open(page, 'overview');
    const tiles = page.locator('.digest .card.stat');
    const [a, b] = [await tiles.nth(0).boundingBox(), await tiles.nth(1).boundingBox()];
    expect(Math.abs(a.y - b.y), 'two tiles on a row').toBeLessThan(2);
    // Each address row stacks its two base URLs on a phone, rather than scrolling the Anthropic one out of view.
    const anthropic = page.locator('#service [data-action="copy-text"]').nth(1);
    await anthropic.scrollIntoViewIfNeeded();
    await expect(anthropic).toBeInViewport({ ratio: 1 });
  });
});

/* ---------------------------------------------------------------------------------------------
   Activity
   --------------------------------------------------------------------------------------------- */
test('Activity searches prompts, opens a decision, and labelling asks for the tier', async ({ page, request }) => {
  // Activity outlives a test: unique words find this test's requests.
  await route(request, 'hello there, how are you? kx3q');
  await route(request, 'compare these system designs for a ledger zq7w');
  await open(page, 'activity');
  await expect(page.locator('#activity-filter')).toHaveAccessibleName(/^Show/);
  const rows = page.locator('table.activity-table tr[data-entry-id]');
  await page.getByLabel('Search').fill('zq7w');
  await expect(rows).toHaveCount(1);
  await page.getByLabel('Search').fill('');
  await expect.poll(() => rows.count()).toBeGreaterThan(1);

  const row = rows.filter({ hasText: 'kx3q' });
  await row.locator('td.prompt-cell').click();
  const detail = page.locator('#activity-detail');
  await expect(detail).toContainText('matched a route');
  await expect(detail).toContainText('SemanticMatch');
  await expect(detail).toContainText('smalltalk');
  await detail.getByRole('button', { name: 'Close' }).click();
  await expect(detail).toHaveCount(0);
  await row.focus();
  await page.keyboard.press('Enter');
  await expect(page.locator('#activity-detail')).toBeVisible();

  await row.getByRole('button', { name: /^Label/ }).click();
  const tier = page.locator('#label-tier');
  await expect(tier).toHaveValue('');
  await page.locator('#label-form [data-action="label-submit"]').click();
  await expect(page.locator('#toast')).toContainText('Pick the tier');
  await tier.selectOption('1');
  await expect(page.locator('#label-form')).toContainText('The tier it was routed to');
});

/* ---------------------------------------------------------------------------------------------
   Settings fields and wording
   --------------------------------------------------------------------------------------------- */
test('settings show their defaults as placeholders and flag a malformed duration', async ({ page }) => {
  await open(page, 'routing');
  const attempts = page.locator('input[data-bind="Selection.MaxAttempts"]');
  await expect(attempts).toHaveValue('');
  await expect(attempts).toHaveAttribute('placeholder', '3 (default)');
  await expect(attempts).toHaveAttribute('min', '1');
  // Advanced settings are folded until opened.
  await expect(page.locator('input[data-bind="AmbiguityMargin"]')).toBeHidden();
  await page.locator('details.advanced summary', { hasText: 'near ties' }).click();
  await expect(page.locator('input[data-bind="AmbiguityMargin"]')).toBeVisible();

  const cooldown = page.locator('input[data-bind="Selection.QuotaCooldown"]');
  await expect(cooldown.locator('xpath=..')).toContainText('15 min by default');
  await cooldown.fill('5m');
  await expect(cooldown.locator('xpath=..').locator('.duration-bad')).toBeVisible();
  await cooldown.fill('00:05:00');
  await expect(cooldown.locator('xpath=..')).toContainText('5 min');
});

test('a budget covers one thing, chosen by its scope, and is valid as soon as it is added', async ({ page, request }) => {
  await open(page, 'usage');
  await page.getByRole('button', { name: 'Add budget' }).click();
  const row = page.locator('table.budgets tr').nth(1);
  await expect(row.locator('input[data-budget-scope="Model"]')).toBeChecked();
  await row.locator('input[data-budget-scope="Connector"]').check();
  await expect(row.locator('select[data-bind$=".Connector"]')).toHaveValue('local');
  await page.locator('#save-bar [data-action="apply"]').click();
  await expect(page.locator('#toast')).toContainText('Applied');
  const config = (await (await request.get('/router/api/config')).json()).config;
  expect(config.Usage.Budgets[0]).toMatchObject({ Connector: 'local', MaxCostUsd: 5 });
  expect(config.Usage.Budgets[0].Model ?? null).toBeNull();
});

test('capabilities are ticked, a refused rename says why, and Gateway keeps Delete all activity apart', async ({ page }) => {
  await open(page, 'models');
  await page.locator('.item-head', { hasText: 'cloud-large' }).click();
  const caps = page.locator('.item.open fieldset[data-pick$=".Capabilities"]');
  await expect(caps.getByLabel('tools')).toBeChecked();
  await caps.getByLabel('vision').check();
  await expect(caps.locator('input[type="hidden"]')).toHaveValue('chat, code, tools, vision');
  await expect(page.locator('#save-bar')).toBeVisible();

  await open(page, 'connectors');
  await page.locator('.item-head', { hasText: 'local' }).first().click();
  const name = page.locator('.item.open input[data-action-input="connector-rename"]');
  await name.fill('cloud');
  await name.press('Tab');
  await expect(page.locator('#toast')).toContainText('There is already a connector named cloud');

  await open(page, 'gateway');
  const stored = page.locator('.card', { has: page.locator('h2', { hasText: 'Stored activity' }) });
  await expect(stored.locator('[data-action="activity-purge"]')).toBeVisible();
  await expect(page.locator('.card', { has: page.locator('h2', { hasText: 'Stored prompts' }) }).locator('[data-action="activity-purge"]')).toHaveCount(0);
});

test('chart axes count in whole numbers without repeating a label', async ({ page, request }) => {
  await route(request, 'hello there, how are you?');
  await open(page, 'overview');
  const labels = page.locator('#view svg.chart').first().locator('text.axis[text-anchor="end"]');
  await expect(labels.first()).toBeVisible();
  const texts = await labels.allTextContents();
  expect(new Set(texts).size, texts.join(', ')).toBe(texts.length);
  for (const t of texts) expect(t).toMatch(/^\d+$/);
});

test('Usage right-aligns its figures and Memory without sources offers one set of buttons', async ({ page }) => {
  await open(page, 'usage');
  const head = page.locator('table.usage-table tr').first();
  await expect(head.locator('th').first()).toHaveText('Model');
  for (const th of await head.locator('th.num').all()) expect(await th.evaluate((el) => getComputedStyle(el).textAlign)).toBe('right');

  await open(page, 'memory');
  await expect(page.locator('[data-action="memory-fit"]')).toHaveCount(0);
  await expect(page.locator('[data-action="memory-refresh"]')).toHaveCount(0);
  await expect(page.locator('[data-action="memory-add"][data-type="Markdown"]')).toHaveCount(1);
});

// The desktop app's window: its web view gives the page invokeCSharpAction(...), and the app hands the page its update
// state (DesktopBridge in SemanticRouter.Desktop). Here a stand-in records what the page sends.
const desktopState = (state) => window.dispatchEvent(new CustomEvent('semantic-router-desktop', { detail: (window.semanticRouterDesktop = state) }));

test('in a browser there is no update banner and no About entry', async ({ page }) => {
  await open(page, 'overview');
  await page.locator('#more-toggle').click();
  await expect(page.locator('#theme-select')).toBeVisible();
  await expect(page.locator('#desktop-about')).toBeHidden();
  await expect(page.locator('#desktop-settings')).toBeHidden();
  await expect(page.locator('#update-banner')).toBeHidden();
});

test('in the desktop app the dashboard opens About and offers an update until Not now', async ({ page }) => {
  await page.addInitScript(() => {
    window.sent = [];
    window.invokeCSharpAction = (body) => window.sent.push(JSON.parse(body).action);
  });
  const sent = () => page.evaluate(() => window.sent);
  await open(page, 'overview');
  expect(await sent()).toEqual(['ready']);
  const banner = page.locator('#update-banner');
  await expect(banner).toBeHidden();

  await page.locator('#more-toggle').click();
  await page.locator('#desktop-about').click();
  expect(await sent()).toEqual(['ready', 'about']);
  if (await page.locator('#more').isHidden()) await page.locator('#more-toggle').click();
  await page.locator('#desktop-settings').click();
  expect(await sent()).toEqual(['ready', 'about', 'settings']);

  const ready = { offer: '0.6.0', downloading: false, headline: 'Version 0.6.0 is ready to install.', detail: 'It is installed when you quit the app, or now with Restart to update.', canRestart: true };
  await page.evaluate(desktopState, ready);
  await expect(banner).toBeVisible();
  await expect(banner).toContainText('Version 0.6.0 is ready to install.');
  await banner.getByRole('button', { name: 'Restart to update' }).click();
  expect(await sent()).toEqual(['ready', 'about', 'settings', 'restart-to-update']);

  // Downloading for Restart to update: said, with nothing to press.
  await page.evaluate(desktopState, { ...ready, downloading: true, headline: 'Downloading version 0.6.0…', detail: null, canRestart: false });
  await expect(banner).toContainText('Downloading version 0.6.0…');
  await expect(banner.getByRole('button')).toHaveCount(0);

  // Not now: gone for this version, also after a reload, and back for the next one.
  await page.evaluate(desktopState, ready);
  await banner.getByRole('button', { name: 'Not now' }).click();
  await expect(banner).toBeHidden();
  await page.addInitScript((state) => { window.semanticRouterDesktop = state; }, ready);
  await page.reload();
  await expect(page.locator('#view h1').first()).toBeVisible();
  await expect(banner).toBeHidden();
  await page.evaluate(desktopState, { ...ready, offer: '0.7.0', headline: 'Version 0.7.0 is ready to install.' });
  await expect(banner).toContainText('Version 0.7.0');
});

// In the app window on a Mac there is no Edit menu, so WebKit does nothing with ⌘C, ⌘V, ⌘X, ⌘A and ⌘Z: the page does
// them (#210). Chromium on Linux does not act on ⌘ either, so it stands in for that web view here.
test('on a Mac, the app window does the editing shortcuts itself', async ({ page, context }) => {
  await context.grantPermissions(['clipboard-read', 'clipboard-write']);
  await page.addInitScript(() => {
    window.invokeCSharpAction = () => {};
    Object.defineProperty(Navigator.prototype, 'platform', { get: () => 'MacIntel' });
  });
  await open(page, 'overview');
  await page.evaluate(() => {
    for (const [id, type] of [['field', 'text'], ['secret', 'password']]) {
      const input = Object.assign(document.createElement('input'), { id, type, value: 'sk-pasted-key' });
      document.body.prepend(input);
    }
  });
  const field = page.locator('#field');
  const clipboard = () => page.evaluate(() => navigator.clipboard.readText());

  await field.focus();
  await page.keyboard.press('Meta+a');
  expect(await field.evaluate((e) => [e.selectionStart, e.selectionEnd])).toEqual([0, 13]);
  await page.keyboard.press('Meta+c');
  await expect.poll(clipboard).toBe('sk-pasted-key');

  await field.evaluate((e) => e.setSelectionRange(13, 13));
  await page.keyboard.press('Meta+v');
  await expect(field).toHaveValue('sk-pasted-keysk-pasted-key');
  await page.keyboard.press('Meta+z');
  await expect(field).toHaveValue('sk-pasted-key');

  await field.evaluate((e) => e.setSelectionRange(0, 3));
  await page.keyboard.press('Meta+x');
  await expect(field).toHaveValue('pasted-key');
  await expect.poll(clipboard).toBe('sk-');

  // A password field is not copied from.
  await page.locator('#secret').focus();
  await page.keyboard.press('Meta+a');
  await page.keyboard.press('Meta+c');
  expect(await clipboard()).toBe('sk-');
});
