// End-to-end tests of the dashboard: every page, the main editing flows, keyboard and screen-reader basics, themes
// (built-in and drop-in files) and screenshots. Each test starts from the fixture configuration.
const fs = require('fs');
const path = require('path');
const { test: base, expect } = require('@playwright/test');

const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'semantic-router.json'), 'utf8')
  .replace(/__STUB__/g, `http://127.0.0.1:${Number(process.env.E2E_PORT || 5190) + 1}`));

const PAGES = ['overview', 'live', 'activity', 'memory', 'usage', 'learned', 'tiers', 'routes', 'evaluate', 'models', 'connectors', 'routing', 'profiles', 'embeddings', 'gateway', 'clients', 'service', 'json', 'audit', 'feedback'];
// The rail's sections and the tabs each one shows, in order (SECTIONS in js/core.js).
const SECTIONS = {
  Monitor: ['Overview', 'Live', 'Activity', 'Usage', 'Memory'],
  Routing: ['Tiers', 'Routes', 'Rules', 'Profiles', 'Evaluate', 'Router learned'],
  Providers: ['Models', 'Connectors', 'Embeddings'],
  Gateway: ['Settings', 'Connect clients'],
  Admin: ['Service', 'JSON', 'Audit log'],
};
const THEMES = ['machine-dark', 'machine-light', 'terminal'];

// Every test fails if the page throws or logs an error (a Content-Security-Policy violation is one), or asks any host
// but the router for anything: the dashboard bundles its fonts and must work offline.
const test = base.extend({
  page: async ({ page, baseURL }, use) => {
    const errors = [];
    const own = new URL(baseURL).origin;
    page.on('request', (r) => {
      const url = r.url();
      if (!url.startsWith('data:') && !url.startsWith('blob:') && new URL(url).origin !== own) errors.push(`request to another host: ${url}`);
    });
    page.on('pageerror', (e) => errors.push(`pageerror: ${e.message}`));
    page.on('console', (m) => { if (m.type() === 'error') errors.push(`console: ${m.text()}`); });
    await use(page);
    expect(errors, 'the dashboard logged errors').toEqual([]);
  },
});

test.beforeEach(async ({ request, page }) => {
  const reset = await request.put('/router/api/config?persist=false', { data: fixture });
  expect(reset.ok(), await reset.text()).toBeTruthy();
  await page.addInitScript(() => { if (!sessionStorage.getItem('e2e-theme-kept')) localStorage.removeItem('semantic-router.theme'); });
});

async function open(page, view) {
  await page.goto(`/router#/${view}`);
  await expect(page.locator('#view h1').first()).toBeVisible();
}

/** Picks a theme in the picker, which sits in the More panel at the foot of the rail. */
async function pickTheme(page, theme) {
  if (await page.locator('#more').isHidden()) await page.locator('#more-toggle').click();
  await page.selectOption('#theme-select', theme);
}

async function expandAll(page) {
  const closed = page.locator('.item-head[aria-expanded="false"]');
  for (let i = 0; i < 50 && await closed.count() > 0; i++) await closed.first().click();
}

/** Visible form controls and buttons without an accessible name. */
async function unnamedControls(page) {
  return page.evaluate(() => {
    const text = (el) => (el && el.textContent || '').trim();
    const named = (el) => {
      if ((el.getAttribute('aria-label') || '').trim()) return true;
      const by = el.getAttribute('aria-labelledby');
      if (by && by.split(/\s+/).some((id) => text(document.getElementById(id)))) return true;
      if (el.labels && Array.from(el.labels).some((l) => text(l))) return true;
      if ((el.getAttribute('title') || '').trim()) return true;
      if ((el.tagName === 'BUTTON' || el.getAttribute('role') === 'button' || el.tagName === 'A') && text(el)) return true;
      return false;
    };
    return Array.from(document.querySelectorAll('input:not([type=hidden]), select, textarea, button, [role=button]'))
      .filter((el) => el.getClientRects().length > 0 && !named(el))
      .map((el) => el.outerHTML.slice(0, 140));
  });
}

test('every page renders, has a heading and labels every control', async ({ page }) => {
  const unlabelled = {};
  for (const view of PAGES) {
    await open(page, view);
    // Every page but Feedback (at the foot of the rail) is one tab of a section.
    if (view !== 'feedback') await expect(page.locator(`#tabs a[data-view="${view}"]`)).toHaveAttribute('aria-current', 'page');
    await expandAll(page);
    const found = await unnamedControls(page);
    if (found.length) unlabelled[view] = found;
  }
  expect(unlabelled, 'controls without an accessible name, by page').toEqual({});
});

test('the audit log lists changes with who made them and from where', async ({ page }) => {
  // beforeEach applied the fixture through the admin API from this machine.
  await open(page, 'audit');
  const table = page.locator('#view table[aria-label="Audit log"]');
  await expect(table.locator('tbody tr').first()).toContainText('this machine');
  await expect(table).toContainText('applied the configuration');
  await expect(table).toContainText('PUT /router/api/config');
});

test('the Service page says where the router listens and that --urls decides it', async ({ page }) => {
  // server.js starts the router with --urls: that wins over the saved setting, and the page says so.
  await open(page, 'service');
  await expect(page.locator('#tabs a.active')).toHaveText('Service');
  await expect(page.locator('#view')).toContainText(/the --urls http:\/\/127\.0\.0\.1:/i);
  await expect(page.getByRole('radio', { name: /This machine only/ })).toBeChecked();
  await expect(page.locator('#view button[data-id="restart"]')).toBeVisible();
  await expect(page.locator('#view h2', { hasText: 'Background service' })).toBeVisible();
});

test('a metrics user sees only the figures and never asks for prompts or the configuration', async ({ page }) => {
  // The router here admits this machine as admin; the page is told it signed in with the metrics token.
  await page.route('**/router/api/session', (route) => route.request().method() === 'GET'
    ? route.fulfill({ json: { role: 'Metrics', tokenRequired: true, tokensConfigured: true, identity: 'metrics token' } })
    : route.continue());
  const asked = [];
  page.on('request', (r) => { const u = new URL(r.url()); if (u.pathname.startsWith('/router/api/')) asked.push(u.pathname); });
  await open(page, 'routes');
  await expect(page.locator('#view h1')).toHaveText('Overview');
  await expect(page.locator('#nav a:visible')).toHaveText(['Monitor']);
  await expect(page.locator('#tabs a:visible')).toHaveText(['Overview', 'Usage']);
  await expect(page.locator('#sidebar a[data-view="feedback"]')).toBeHidden();
  await expect(page.locator('#banner')).toContainText('metrics');
  await expect(page.locator('#view')).not.toContainText('Try a prompt');
  await page.locator('#tabs a', { hasText: 'Usage' }).click();
  await expect(page.locator('#view h1')).toHaveText('Usage');
  await expect(page.locator('#view')).not.toContainText('Reference model');
  expect(asked.filter((p) => /\/(config|activity|events|tuning|memory|audit)$/.test(p))).toEqual([]);
});

test('the page has landmarks, a skip link and marks the current page', async ({ page }) => {
  await open(page, 'routes');
  await expect(page.locator('nav[aria-label="Sections"]')).toBeVisible();
  await expect(page.locator('nav[aria-label="Pages"]')).toBeVisible();
  await expect(page.locator('main#view')).toBeVisible();
  await expect(page.locator('#nav a[aria-current="true"]')).toHaveText('Routing');
  await expect(page.locator('#tabs a[aria-current="page"]')).toHaveText('Routes');
  await page.keyboard.press('Tab');
  await expect(page.locator('.skip-link')).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.locator('main#view')).toBeFocused();
});

test('the rail groups the pages into sections, each with its pages as tabs', async ({ page }) => {
  await open(page, 'overview');
  await expect(page.locator('#nav a')).toHaveText(Object.keys(SECTIONS));
  for (const [section, tabs] of Object.entries(SECTIONS)) {
    await page.locator('#nav a', { hasText: section }).click();
    await expect(page.locator('#nav a[aria-current="true"]')).toHaveText(section);
    await expect(page.locator('#tabs a')).toHaveText(tabs);
    await expect(page.locator('#tabs a[aria-current="page"]')).toHaveText(tabs[0]);
  }
  // A section reopens on the tab last open in it.
  await page.locator('#nav a', { hasText: 'Routing' }).click();
  await page.locator('#tabs a', { hasText: 'Profiles' }).click();
  await expect(page.locator('#view h1').first()).toHaveText('Profiles');
  await page.locator('#nav a', { hasText: 'Monitor' }).click();
  await page.locator('#nav a', { hasText: 'Routing' }).click();
  await expect(page.locator('#tabs a[aria-current="page"]')).toHaveText('Profiles');
  // Report an issue (the Feedback page) is in no section: no tabs.
  await expect(page.locator('#sidebar a[data-view="feedback"]')).toHaveText('Report an issue');
  await page.locator('#sidebar a[data-view="feedback"]').click();
  await expect(page.locator('#view h1')).toHaveText('Report an issue');
  await expect(page.locator('#tabs')).toBeHidden();
  await expect(page.locator('#nav a[aria-current]')).toHaveCount(0);
});

test('every page keeps its address, so links from the tray, notices and webhooks still work', async ({ page }) => {
  for (const [view, section, tab] of [['learned', 'Routing', 'Router learned'], ['connectors', 'Providers', 'Connectors'], ['routing', 'Routing', 'Rules'], ['clients', 'Gateway', 'Connect clients']]) {
    await open(page, view);
    await expect(page.locator('#nav a[aria-current="true"]')).toHaveText(section);
    await expect(page.locator('#tabs a[aria-current="page"]')).toHaveText(tab);
  }
});

test('the rail fits a small window without scrolling', async ({ page }) => {
  // The desktop app on a 1366 × 768 laptop leaves about 620 px for the page.
  await page.setViewportSize({ width: 1280, height: 600 });
  await open(page, 'overview');
  const nav = page.locator('#nav');
  expect(await nav.evaluate((el) => el.scrollHeight - el.clientHeight), 'the rail scrolls').toBeLessThanOrEqual(0);
  await expect(page.locator('#nav a').last()).toBeInViewport();
  await expect(page.locator('#more-toggle')).toBeInViewport();
});

test('More shows the version and the theme picker, and closes on Escape or a click elsewhere', async ({ page }) => {
  await open(page, 'overview');
  const toggle = page.locator('#more-toggle');
  await expect(page.locator('#more')).toBeHidden();
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');
  await expect(page.locator('#lib-version')).toContainText('SemanticRouter');
  await expect(page.locator('#brand-sub')).not.toHaveText('loading…');
  await expect(page.locator('#theme-select')).toBeVisible();
  expect(await unnamedControls(page), 'every control in More has an accessible name').toEqual([]);
  await page.keyboard.press('Escape');
  await expect(page.locator('#more')).toBeHidden();
  await expect(toggle).toBeFocused();
  await toggle.click();
  await page.locator('#view h1').first().click();
  await expect(page.locator('#more')).toBeHidden();
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
});

test('expandable items work with the keyboard and show focus', async ({ page }) => {
  await open(page, 'routes');
  const head = page.locator('.item-head').first();
  await expect(head).toHaveAttribute('aria-expanded', 'false');
  await head.focus();
  await page.keyboard.press('Enter');
  await expect(head).toHaveAttribute('aria-expanded', 'true');
  await expect(page.locator('.item.open textarea').first()).toBeVisible();
  await page.keyboard.press(' ');
  await expect(head).toHaveAttribute('aria-expanded', 'false');
  // Keyboard focus is visible: an outline, in every theme.
  for (const theme of THEMES) {
    await pickTheme(page, theme);
    await head.focus();
    await page.keyboard.press('Shift+Tab');
    await page.keyboard.press('Tab');
    const outline = await head.evaluate((el) => getComputedStyle(el).outlineStyle);
    expect(outline, `focus outline in ${theme}`).not.toBe('none');
  }
});

test('editing a route and applying it changes the running router', async ({ page, request }) => {
  await open(page, 'routes');
  await page.locator('.item-head', { hasText: 'smalltalk' }).click();
  const utterances = page.locator('textarea[data-bind="Routes.0.Utterances"]');
  await utterances.fill('hello there\nthanks a lot\nhow are you\ngood morning');
  await expect(page.locator('#save-bar')).toBeVisible();
  await page.locator('#save-bar [data-action="apply"]').click();
  await expect(page.locator('#toast')).toContainText('Applied');
  await expect(page.locator('#save-bar')).toBeHidden();
  const config = await (await request.get('/router/api/config')).json();
  expect(config.config.Routes[0].Utterances).toContain('good morning');
});

test('adding a model puts it in the catalog', async ({ page, request }) => {
  await open(page, 'models');
  await page.locator('[data-action="model-add"]').click();
  const name = page.locator('.item.open input[data-bind$=".Name"]').first();
  await name.fill('stub-medium');
  await page.locator('.item.open input[data-bind$=".Model"]').first().fill('stub-small');
  await page.locator('#save-bar [data-action="apply"]').click();
  await expect(page.locator('#toast')).toContainText('Applied');
  const models = (await (await request.get('/router/api/status')).json()).models.map((m) => m.name);
  expect(models).toContain('stub-medium');
});

test('discovering a connector\'s models adds the chosen ones on the chosen tier', async ({ page, request }) => {
  // Discovery asks with the draft's settings (POST api/connectors/discover). A router without that endpoint is asked
  // by connector name instead, as the dashboard does after a 404, without the 404 the browser would log as an error.
  await page.route('**/router/api/connectors/discover', async (r) => {
    const draft = await r.fetch();
    if (draft.status() !== 404 && draft.status() !== 405) return r.fulfill({ response: draft });
    const { name } = r.request().postDataJSON();
    return r.fulfill({ response: await r.fetch({ url: r.request().url().replace('/connectors/discover', `/connectors/${encodeURIComponent(name)}/discover`), postData: '' }) });
  });
  await open(page, 'models');
  const group = page.locator('section.model-group', { has: page.locator('h2', { hasText: /^local$/ }) });
  await group.locator('[data-action="lineup-discover"]').click();
  const lineup = group.locator('.lineup');
  await expect(lineup).toContainText('in the catalog as local-small');
  // The stub is neither LM Studio nor Ollama, so nothing is ticked for you: it could be a proxy to a cloud API.
  await expect(lineup).toContainText('does not forward prompts to a cloud API');
  await lineup.locator('input[aria-label="Add stub-large"]').check();
  expect(await unnamedControls(page), 'every lineup control has an accessible name').toEqual([]);
  await lineup.locator('input[aria-label="Catalog name for stub-large"]').fill('local-big');
  await lineup.locator('select[aria-label="Tier for stub-large"]').selectOption('3');
  await lineup.locator('[data-action="lineup-add"]').click();
  await expect(group.locator('.item', { hasText: 'local-big' })).toContainText('T3');
  await expect(lineup).toContainText('in the catalog as local-big');
  await page.locator('#save-bar [data-action="apply"]').click();
  await expect(page.locator('#toast')).toContainText('Applied');
  const config = (await (await request.get('/router/api/config')).json()).config;
  expect(config.Models.find((m) => m.Name === 'local-big')).toMatchObject({ Connector: 'local', Model: 'stub-large', Tier: 3, IsLocal: true });
});

test('looking up prices fills in a connector\'s cloud models, cache prices included', async ({ page, request }) => {
  // The router would ask openrouter.ai; the test answers for it.
  let asked = null;
  await page.route('**/router/api/prices/lookup', (r) => {
    asked = r.request().postDataJSON();
    return r.fulfill({ json: { source: 'openrouter.ai', fetched: new Date().toISOString(), problem: null, prices: [{ id: 'openai/stub-large', sameProvider: false, input: 2, output: 10, cacheRead: 0.2, cacheWrite: 2.5, contextWindow: 400000 }] } });
  });
  await open(page, 'models');
  const local = page.locator('section.model-group', { has: page.locator('h2', { hasText: /^local$/ }) });
  await expect(local.locator('[data-action="prices-lookup"]'), 'local models are never looked up').toHaveCount(0);
  const cloud = page.locator('section.model-group', { has: page.locator('h2', { hasText: /^cloud$/ }) });
  await cloud.locator('[data-action="prices-lookup"]').click();
  await expect(page.locator('#toast')).toContainText('Prices from openrouter.ai: cloud-large $2 / $10 per M (cache read $0.2, write $2.5)');
  await expect(page.locator('#toast')).toContainText('cloud may charge differently');
  expect(asked).toEqual({ models: [{ model: 'stub-large', type: 'OpenAICompatible', preset: 'Custom', baseAddress: expect.stringContaining('http://127.0.0.1:') }] });
  await expect(cloud.locator('.item', { hasText: 'cloud-large' })).toContainText('$2/$10 per M');
  await page.locator('#save-bar [data-action="apply"]').click();
  await expect(page.locator('#toast')).toContainText('Applied');
  const config = (await (await request.get('/router/api/config')).json()).config;
  expect(config.Models.find((m) => m.Name === 'cloud-large')).toMatchObject({ InputCostPerMillionTokens: 2, OutputCostPerMillionTokens: 10, CacheReadCostPerMillionTokens: 0.2, CacheWriteCostPerMillionTokens: 2.5 });
});

test('renaming a connector renames it in the models that use it', async ({ page, request }) => {
  await open(page, 'connectors');
  await page.locator('.item-head', { hasText: 'local' }).first().click();
  await page.locator('.item.open input[data-action-input="connector-rename"]').fill('lmstudio');
  await page.locator('#tabs a[data-view="models"]').click();
  await expect(page.locator('.item', { hasText: 'local-small' })).toContainText('via lmstudio');
  await page.locator('#save-bar [data-action="apply"]').click();
  await expect(page.locator('#toast')).toContainText('Applied');
  const config = (await (await request.get('/router/api/config')).json()).config;
  expect(Object.keys(config.Connectors)).toContain('lmstudio');
  expect(config.Models.find((m) => m.Name === 'local-small').Connector).toBe('lmstudio');
});

test('an Anthropic connector\'s prompt caching settings are saved', async ({ page, request }) => {
  await open(page, 'connectors');
  await page.locator('.item-head', { hasText: 'local' }).first().click();
  await page.locator('.item.open select[data-bind="Connectors.local.Type"]').selectOption('Anthropic');
  await page.locator('.item.open select[data-bind="Connectors.local.PromptCaching"]').selectOption('Off');
  await page.locator('.item.open select[data-bind="Connectors.local.CacheTtl"]').selectOption('1h');
  await page.locator('#save-bar [data-action="apply"]').click();
  await expect(page.locator('#toast')).toContainText('Applied');
  const config = (await (await request.get('/router/api/config')).json()).config;
  expect(config.Connectors.local).toMatchObject({ Type: 'Anthropic', PromptCaching: 'Off', CacheTtl: '1h' });
});

test("an installed package's connector type is offered and shown by its settings", async ({ page, request }) => {
  // The host has the Copilot package, so api/status lists its type after the built-in ones; the page names none of them.
  const { connectorTypes } = await (await request.get('/router/api/status')).json();
  expect(connectorTypes.installed).toContain('Copilot');
  await open(page, 'connectors');
  await page.locator('.item-head', { hasText: 'local' }).first().click();
  const type = page.locator('.item.open select[data-bind="Connectors.local.Type"]');
  await expect(type.locator('option')).toHaveText([...connectorTypes.builtIn, ...connectorTypes.installed]);
  await type.selectOption('Copilot');
  // The Copilot package does not call a base address (its factory leaves UsesBaseAddress off), so it gets the fields of
  // a program, not the HTTP ones, although the connector still has the stub's address.
  expect(connectorTypes.usesBaseAddress).not.toContain('Copilot');
  await expect(page.locator('.item.open input[data-bind="Connectors.local.Executable"]')).toBeVisible();
  await expect(page.locator('.item.open input[data-bind="Connectors.local.HomeDirectory"]')).toBeVisible();
  await expect(page.locator('.item.open input[data-bind="Connectors.local.RequestPath"]')).toHaveCount(0);
  await expect(page.locator('.item.open input[data-bind="Connectors.local.BaseAddress"]')).toHaveCount(0);
});

test("a packaged connector type that calls its base address gets the HTTP fields", async ({ page }) => {
  // The host has no such package, so api/status is given one: "LocalGateway", whose factory sets UsesBaseAddress.
  await page.route('**/router/api/status', async (route) => {
    const res = await route.fetch();
    const json = await res.json();
    json.connectorTypes.installed.push('LocalGateway');
    json.connectorTypes.usesBaseAddress.push('LocalGateway');
    await route.fulfill({ response: res, json });
  });
  await open(page, 'connectors');
  await page.locator('.item-head', { hasText: 'local' }).first().click();
  await page.locator('.item.open select[data-bind="Connectors.local.Type"]').selectOption('LocalGateway');
  await expect(page.locator('.item.open input[data-bind="Connectors.local.BaseAddress"]')).toBeVisible();
  await expect(page.locator('.item.open input[data-bind="Connectors.local.ApiKey"]')).toBeVisible();
  await expect(page.locator('.item.open input[data-bind="Connectors.local.Executable"]')).toHaveCount(0);
  await expect(page.locator('.item.open input[data-bind="Connectors.local.AllowRemoteClients"]')).toHaveCount(0);
});

test("a packaged embedding provider that does not run in this process gets the endpoint fields", async ({ page }) => {
  // "HostedEmbeddings" stands for a package that calls a vendor's API: its factory leaves RunsInProcess off.
  await page.route('**/router/api/status', async (route) => {
    const res = await route.fetch();
    const json = await res.json();
    json.embeddings.available.push('HostedEmbeddings');
    json.embeddings.models.push({ provider: 'HostedEmbeddings', name: 'hosted-small', description: 'A hosted model.', suggestedThreshold: 0.5, downloadBytes: 0 });
    await route.fulfill({ response: res, json });
  });
  await open(page, 'embeddings');
  await page.locator('select[data-bind="Embeddings.Provider"]').selectOption('HostedEmbeddings');
  await expect(page.locator('input[data-bind="Embeddings.Endpoint"]')).toBeVisible();
  await expect(page.locator('input[data-bind="Embeddings.ApiKey"]')).toBeVisible();
  await expect(page.locator('input[data-bind="Embeddings.ModelPath"]')).toHaveCount(0);
  await expect(page.locator('select[data-bind="Embeddings.Model"]')).toHaveCount(0);
});

test("a connector type in the draft is matched however it is spelled", async ({ page }) => {
  // Use JSON puts the parsed text into the draft as it is: "cli" in lower case, or the old enum number.
  await open(page, 'json');
  const editor = page.locator('#json-editor');
  const config = JSON.parse(await editor.inputValue());
  config.Connectors.lower = { Type: 'cli', Preset: 'Codex' };
  config.Connectors.number = { Type: 1 };
  await editor.fill(JSON.stringify(config, null, 2));
  await page.locator('[data-action="json-use"]').click();
  await open(page, 'models');
  await expect(page.locator('#view')).not.toContainText('Could not render');
  await open(page, 'connectors');
  await page.locator('.item-head', { hasText: 'lower' }).click();
  await expect(page.locator('.item.open select[data-bind="Connectors.lower.Type"]')).toHaveValue('Cli');
  await expect(page.locator('.item.open select[data-bind="Connectors.lower.Preset"]')).toBeVisible();
  await expect(page.locator('.item.open select[data-bind="Connectors.lower.ToolCalling"]')).toBeVisible();
  await page.locator('.item-head', { hasText: 'number' }).click();
  await expect(page.locator('.item.open select[data-bind="Connectors.number.Type"]')).toHaveValue('1');
  await expect(page.locator('.item-head', { hasText: 'number' }).locator('.badge.danger', { hasText: 'not installed' })).toBeVisible();
});

test("an installed package's embedding provider offers its models", async ({ page, request }) => {
  const { embeddings } = await (await request.get('/router/api/status')).json();
  const kind = embeddings.runsInProcess.find((k) => !embeddings.unavailable[k]);
  test.skip(!kind, 'no installed in-process embedding provider can run on this machine');
  const models = embeddings.models.filter((m) => m.provider === kind);
  await open(page, 'embeddings');
  await page.locator('select[data-bind="Embeddings.Provider"]').selectOption(kind);
  const model = page.locator('select[data-bind="Embeddings.Model"]');
  await expect(model.locator('option')).toHaveCount(models.length);
  await expect(model).toHaveValue(models[0].name);
  await expect(page.locator('input[data-bind="Embeddings.ModelPath"]')).toBeVisible();
});

test('validation lists problems and discard restores the configuration', async ({ page }) => {
  await open(page, 'routes');
  await page.locator('.item-head', { hasText: 'architecture' }).click();
  await page.locator('input[data-bind="Routes.1.Name"]').fill('smalltalk');
  await page.locator('#save-bar [data-action="validate"]').click();
  await expect(page.locator('ul.errors')).toContainText('smalltalk');
  // Discard asks in the page first, then offers Undo.
  await page.locator('#save-bar [data-action="discard"]').click();
  await expect(page.locator('dialog#ask')).toContainText('Discard 1 change?');
  await page.locator('dialog#ask button', { hasText: 'Discard' }).click();
  await expect(page.locator('#save-bar')).toBeHidden();
  await expect(page.locator('.item-head', { hasText: 'architecture' })).toBeVisible();
  await page.locator('#toast button', { hasText: 'Undo' }).click();
  await expect(page.locator('#save-bar')).toBeVisible();
  await expect(page.locator('input[data-bind="Routes.1.Name"]')).toHaveValue('smalltalk');
});

test('the prompt tester classifies and sends a prompt through the gateway', async ({ page }) => {
  await open(page, 'overview');
  await page.locator('#tester-prompt').fill('hello there, how are you?');
  await page.locator('[data-action="tester-classify"]').click();
  await expect(page.locator('.tester')).toContainText('smalltalk');
  await expect(page.locator('.tester')).toContainText('local-small');
  await page.locator('#tester-prompt').fill('@tier3 hello there, how are you?');
  await page.locator('[data-action="tester-classify"]').click();
  await expect(page.locator('.tester .result-line .badge.accent')).toHaveText('@tier3');
  // The reason in words, with the enum value in its tooltip.
  await expect(page.locator('.tester')).toContainText('asked for with a callout');
  await expect(page.locator('.tester [title="Reason: ExplicitCallout"]')).toBeVisible();
  await page.locator('#tester-prompt').fill('hello there, how are you?');
  await page.locator('[data-action="tester-send"]').click();
  await expect(page.locator('.tester')).toContainText('Hello from the stub backend.');
});

test('a status refresh does not interrupt typing in the prompt tester', async ({ page }) => {
  await open(page, 'overview');
  await page.locator('#tester-model').selectOption('auto:best');
  await page.locator('#tester-prompt').click();
  await page.keyboard.type('write a regex ');
  // Wait for the next refresh to re-render the page: the field is then a new element.
  await page.locator('#tester-prompt').evaluate((e) => { e.dataset.before = 'yes'; });
  await page.waitForFunction(() => !document.getElementById('tester-prompt').dataset.before, null, { timeout: 15000 });
  await page.keyboard.type('that matches email addresses');
  await expect(page.locator('#tester-prompt')).toHaveValue('write a regex that matches email addresses');
  await expect(page.locator('#tester-model')).toHaveValue('auto:best');
});

test('the endpoints card and the top bar show the host in use, its state and figures', async ({ page }) => {
  const stub = `127.0.0.1:${Number(process.env.E2E_PORT || 5190) + 1}`;
  await open(page, 'overview');
  await page.locator('#tester-prompt').fill('hello there, how are you?');
  await page.locator('[data-action="tester-send"]').click();
  await expect(page.locator('.tester')).toContainText('Hello from the stub backend.');
  await page.reload();

  const row = page.locator('#endpoints tr').filter({ has: page.locator('td:first-child b', { hasText: /^local$/ }) });
  await expect(row).toContainText(stub);
  await expect(row).toContainText('in use');
  await expect(row.locator('.badge.ok').first()).toHaveText('ok');
  await expect(row).toContainText('100%');
  await expect(page.locator('#status-pill .endpoint-now')).toContainText(stub);

  await row.locator('[data-action="endpoint-check"]').click();
  await expect(row).toContainText('passed');
  await expect(page.locator('#endpoints')).not.toContainText('sk-e2e-not-a-real-key');
});

test('changing the usage window or grouping keeps the figures until the new ones arrive', async ({ page }) => {
  await open(page, 'usage');
  await expect(page.locator('#view .card.stat').first()).toBeVisible();
  await page.route(/\/router\/api\/usage\?/, async (route) => { await new Promise((r) => setTimeout(r, 700)); await route.continue(); });
  const byRoute = page.waitForResponse(/\/router\/api\/usage\?window=day&by=route/);
  await page.click('[data-action="usage-set"][data-key="window"][data-value="day"]');
  await page.click('[data-action="usage-set"][data-key="by"][data-value="route"]');
  expect(await page.locator('#view .card.stat').count(), 'the page blanked while loading').toBeGreaterThan(0);
  await expect(page.locator('#view')).not.toContainText('Loading…');
  await byRoute;
  await expect(page.locator('#view h2', { hasText: 'By route' })).toBeVisible();
  await expect(page.locator('[data-action="usage-set"][data-key="window"][data-value="day"]')).toHaveClass(/\bon\b/);
});

test('the service card shows where the router runs, the base URLs for clients and its health', async ({ page }) => {
  const base = `http://localhost:${Number(process.env.E2E_PORT || 5190)}`;
  await open(page, 'overview');

  const card = page.locator('#service');
  await expect(card.locator('.card-head .badge')).toHaveText('running');
  await expect(card).toContainText(`${base}/v1`);
  await expect(card.locator('[data-action="copy-text"]').first()).toHaveAttribute('data-text', `${base}/v1`);
  await expect(card).toContainText('this machine only');
  // Where it runs is folded into "About this router", further down.
  const about = page.locator('details.about');
  await expect(about).not.toHaveAttribute('open', '');
  await about.locator('summary').click();
  await expect(about).toContainText('pid');
  await expect(about).toContainText('127.0.0.1');
});

test('router learned runs tuning, pauses and resumes learning', async ({ page }) => {
  await open(page, 'learned');
  await expect(page.locator('#view')).toContainText('applies changes');
  await page.locator('[data-action="tuning-run"]').click();
  await expect(page.locator('#view')).toContainText('Waiting for labelled prompts');
  await page.locator('[data-action="tuning-pause"]').click();
  await expect(page.locator('#view .badge', { hasText: 'paused' })).toBeVisible();
  await expect(page.locator('[data-action="tuning-run"]')).toBeDisabled();
  await page.locator('[data-action="tuning-resume"]').click();
  await expect(page.locator('#view')).toContainText('applies changes');
  await open(page, 'overview');
  await expect(page.locator('#view')).toContainText('Router learned');
});

test('the theme picker switches themes and remembers the choice', async ({ page }) => {
  await open(page, 'overview');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'machine-dark');
  await pickTheme(page, 'machine-light');
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'machine-light');
  const background = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
  expect(background).toBe('rgb(255, 255, 255)');
  await page.evaluate(() => sessionStorage.setItem('e2e-theme-kept', '1'));
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'machine-light');
  await expect(page.locator('#theme-select')).toHaveValue('machine-light');
});

test('a theme file dropped into the themes directory appears after a reload', async ({ page, request }) => {
  const listing = await (await request.get('/router/api/themes')).json();
  expect(listing.directory).toBeTruthy();
  const ocean = listing.themes.find((t) => t.id === 'ocean');
  expect(ocean).toMatchObject({ name: 'Ocean', builtIn: false, missingTokens: [], contrastIssues: [] });
  expect(listing.themes.find((t) => t.id === 'half-done').missingTokens).toContain('--accent');

  const file = path.join(listing.directory, 'dropped-in.css');
  fs.writeFileSync(file, fs.readFileSync(path.join(__dirname, 'fixtures', 'themes', 'ocean.css'), 'utf8').replace('"ocean"', '"dropped-in"').replace('#0b1726', '#202020'));
  try {
    await open(page, 'overview');
    await expect(page.locator('#theme-select option[value="dropped-in"]')).toHaveText('Dropped In');
    await pickTheme(page, 'dropped-in');
    await expect.poll(() => page.evaluate(() => getComputedStyle(document.body).backgroundColor)).toBe('rgb(32, 32, 32)');
    await expect(page.locator('#theme-select option[value="half-done"]')).toContainText('⚠');
  } finally {
    fs.rmSync(file, { force: true });
  }
});

test('every built-in theme meets WCAG AA contrast', async ({ request }) => {
  const listing = await (await request.get('/router/api/themes')).json();
  for (const theme of listing.themes.filter((t) => t.builtIn)) {
    expect(theme.contrastIssues, theme.id).toEqual([]);
  }
});

/** Sends a prompt through the gateway, so the Live feed and the Activity table have a row for it. */
async function route(request, prompt, headers = {}) {
  const res = await request.post('/v1/chat/completions', { headers, data: { model: 'auto', messages: [{ role: 'user', content: prompt }] } });
  expect(res.ok(), await res.text()).toBeTruthy();
}

/** How far the page scrolls sideways: 0 when everything fits the window. */
const sideways = (page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

// A pasted build URL (not a credential, so it is not masked) and the kind of JSON user id Claude Code sends.
const LONG_TOKEN = 'https://example.com/builds/' + 'a1b2c3d4e5f6'.repeat(8);
const LONG_PROJECT = JSON.stringify({ device_id: 'f'.repeat(64), account_uuid: '12345678-1234-1234-1234-123456789abc' });

// At 1680 px a short decision used to fit on one line and a long one wrapped its reason under the header: now every row
// has the same lines at every width.
for (const width of [1360, 1680]) {
  test(`every decision on Live has the same lines, with the latency on the reason line (${width} px)`, async ({ page, request }) => {
    await route(request, 'hello there, how are you?');
    await route(request, 'please design a scalable distributed architecture for an event sourcing system with CQRS, snapshots and replay across regions');
    await page.setViewportSize({ width, height: 900 });
    await open(page, 'live');
    const rows = page.locator('#live-feed .feed-row');
    await expect(rows.nth(1)).toBeVisible();
    for (const row of await rows.all()) {
      const box = async (sel) => (await row.locator(sel).first().boundingBox());
      const [t, tier, why, ms, prompt, self] = [await box('.t'), await box('.tier'), await box('.why'), await box('.ms'), await box('.prompt'), await row.boundingBox()];
      expect(Math.abs((tier.y + tier.height / 2) - (t.y + t.height / 2)), 'the tier is on the header line').toBeLessThan(6);
      expect(why.y, 'the reason is below the header line').toBeGreaterThanOrEqual(t.y + t.height - 1);
      expect(Math.abs(ms.y - why.y), 'the latency is on the reason line').toBeLessThan(4);
      expect(self.x + self.width - (ms.x + ms.width), 'the latency is right-aligned').toBeLessThan(12);
      expect(prompt.y, 'the prompt is below the reason line').toBeGreaterThanOrEqual(why.y + why.height - 1);
    }
  });
}

for (const width of [1000, 1100, 1200]) {
  test(`Live fits a ${width} px window without scrolling sideways`, async ({ page, request }) => {
    await route(request, 'summarize this text: ' + 'the ledger replays events twice and the customer account number is wrong on every invoice '.repeat(3));
    await page.setViewportSize({ width, height: 900 });
    await open(page, 'live');
    await expect(page.locator('#live-feed .feed-row').first()).toBeVisible();
    await expect(page.locator('#live-graph svg')).toBeVisible();
    expect(await sideways(page), 'horizontal overflow').toBeLessThanOrEqual(0);
    const card = await page.locator('#live-graph').boundingBox();
    const graph = await page.locator('#live-graph svg').boundingBox();
    expect(graph.x + graph.width, 'the graph fits its panel').toBeLessThanOrEqual(card.x + card.width + 1);
  });
}

test('a long unbroken token in a prompt or project keeps the Activity table on screen', async ({ page, request }) => {
  await route(request, 'hello there ' + LONG_TOKEN, { 'X-Router-Project': LONG_PROJECT });
  await page.setViewportSize({ width: 1440, height: 900 });
  for (const view of ['activity', 'overview']) {
    await open(page, view);
    const row = page.locator('#view table.activity-table tr', { hasText: 'example.com/builds' }).first();
    await expect(row).toBeVisible();
    expect(await sideways(page), `horizontal overflow on ${view}`).toBeLessThanOrEqual(0);
    const wrap = row.locator('xpath=ancestor::div[contains(@class, "table-wrap")]');
    expect(await wrap.evaluate((el) => el.scrollWidth - el.clientWidth), `the ${view} table scrolls sideways`).toBeLessThanOrEqual(0);
    const box = await wrap.boundingBox();
    const served = await row.locator('td.served').boundingBox();
    const ms = await row.locator('td.num').boundingBox();
    expect(served.width, 'Served by has room').toBeGreaterThanOrEqual(100);
    expect(ms.x + ms.width, 'the ms column is inside the table').toBeLessThanOrEqual(box.x + box.width + 1);
    await expect(row.locator('td.served')).toContainText(/local-small|cloud-large/);
  }
});

test('Report on an Activity entry opens a pre-filled misroute issue without the prompt', async ({ page, request }) => {
  await route(request, 'hello there, my-private-words-123');
  await page.addInitScript(() => { window.__opened = []; window.open = (url) => { window.__opened.push(url); return null; }; });
  await open(page, 'activity');
  await page.locator('#view table.activity-table tr', { hasText: 'my-private-words-123' }).first().getByRole('button', { name: 'Report' }).click();
  await expect(page.locator('#view h1')).toHaveText('Report an issue');
  await expect(page.locator('#feedback-form')).toContainText('Route: smalltalk');
  await page.getByLabel('What the prompt really needed, and why').fill('Small talk; tier 1 was right.');
  await page.getByRole('button', { name: 'Open the issue on GitHub' }).click();
  const [url] = await page.evaluate(() => window.__opened);
  const issue = new URL(url);
  expect(issue.origin + issue.pathname).toBe('https://github.com/siebe41/SemanticRouter.NET/issues/new');
  expect(issue.searchParams.get('template')).toBe('misroute.yml');
  expect(issue.searchParams.get('decision')).toContain('Route: smalltalk');
  expect(issue.searchParams.get('answered-by')).toBe('local-small');
  expect(issue.searchParams.get('expected')).toContain('tier 1 was right');
  expect(issue.searchParams.get('version')).toBeTruthy();
  expect(url).not.toContain('my-private-words-123');
  expect(issue.searchParams.has('prompt')).toBeFalsy();
});

test.describe('on a phone', () => {
  test.use({ viewport: { width: 390, height: 844 } });

  test('the navigation collapses behind a menu button', async ({ page }) => {
    await open(page, 'overview');
    await expect(page.locator('#nav')).toBeHidden();
    const toggle = page.locator('#nav-toggle');
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await toggle.click();
    await expect(page.locator('#nav')).toBeVisible();
    await expect(toggle).toHaveAttribute('aria-expanded', 'true');
    await page.locator('#nav a', { hasText: 'Routing' }).click();
    await expect(page.locator('#view h1').first()).toHaveText('Tiers');
    await expect(page.locator('#nav')).toBeHidden();
    await page.locator('#tabs a', { hasText: 'Routes' }).click();
    await expect(page.locator('#view h1')).toHaveText('Routes');
    await toggle.click();
    await page.keyboard.press('Escape');
    await expect(page.locator('#nav')).toBeHidden();
    await expect(toggle).toBeFocused();
  });

  test('the tabs scroll sideways inside their strip and show the current one', async ({ page }) => {
    await open(page, 'learned');
    expect(await sideways(page), 'horizontal overflow').toBeLessThanOrEqual(0);
    await expect(page.locator('#tabs a[aria-current="page"]')).toBeInViewport({ ratio: 1 });
  });
});

test.describe('screenshots', () => {
  const masks = (page) => [page.locator('#status-pill'), page.locator('#brand-sub'), page.locator('#lib-version')];
  for (const theme of THEMES) {
    for (const [device, viewport] of [['desktop', { width: 1360, height: 900 }], ['phone', { width: 390, height: 844 }]]) {
      test(`${theme} on ${device}`, async ({ page }) => {
        await page.setViewportSize(viewport);
        await page.addInitScript((t) => { localStorage.setItem('semantic-router.theme', t); sessionStorage.setItem('e2e-theme-kept', '1'); }, theme);
        await open(page, 'routes');
        await page.locator('.item-head', { hasText: 'smalltalk' }).click();
        await page.addStyleTag({ content: ':root, :root[data-theme] { --font-heading: "DejaVu Sans", sans-serif; --font-body: "DejaVu Sans", sans-serif; --font-mono: "DejaVu Sans Mono", monospace; }' });
        await page.evaluate(() => document.fonts.ready);
        await expect(page).toHaveScreenshot(`${theme}-${device}.png`, { mask: masks(page) });
      });
    }
  }
});

test('the audit log shows the limit chosen while an earlier load was still on its way', async ({ page }) => {
  // #301: a load still running dropped the new one, and its older answer filled the table under the new limit.
  let loads = 0;
  await page.route('**/router/api/audit?limit=*', async (route) => {
    const limit = new URL(route.request().url()).searchParams.get('limit');
    if (limit === '200' && ++loads > 1) {
      await new Promise((resolve) => setTimeout(resolve, 1500));
      return route.fulfill({ json: { entries: [] } });
    }
    return route.fulfill({ response: await route.fetch() });
  });
  await open(page, 'audit');
  const table = page.locator('#view table[aria-label="Audit log"]');
  await expect(table).toContainText('applied the configuration');

  const late = page.waitForResponse((r) => r.url().includes('limit=200'));
  await page.locator('#view [data-action="audit-load"]').click();
  const wanted = page.waitForRequest((r) => r.url().includes('limit=1000'));
  await page.locator('#audit-limit').selectOption('1000');
  await wanted;
  await late;

  await expect(page.locator('#audit-limit')).toHaveValue('1000');
  await expect(table).toContainText('applied the configuration');
});

test('the address the page was loaded from is marked unencrypted over http', async ({ page }) => {
  // #311: with no addresses from the server, the page's own was always marked encrypted, hiding the warning.
  await open(page, 'overview');
  const fallback = await page.evaluate(async () => (await import('/router/js/service.js')).addresses({})[0]);
  expect(fallback.encrypted).toBe(false);
});

test('a suggested model without a name is added under its id when that name is taken', async ({ page }) => {
  // #314: the clash loop built names from the empty Name, adding "  -2".
  await open(page, 'overview');
  const names = await page.evaluate(async () => {
    const { state } = await import('/router/js/core.js');
    const { actions } = await import('/router/js/actions.js');
    state.lineups = [{ connector: 'local', lineup: [{ id: 'local-small', selected: true, suggestion: { Name: '  ', Connector: 'local', Model: 'local-small', Tier: 1 } }] }];
    actions['lineup-add']({ dataset: { index: '0' } });
    return state.config.Models.map((m) => m.Name);
  });
  expect(names).toContain('local-small-2');
});

test('the memory map highlights no link to a node the filter hides', async ({ page }) => {
  // #316: the focused node's links were drawn in the accent colour even when the other end was hidden.
  await open(page, 'overview');
  const highlighted = await page.evaluate(async () => {
    const canvas = document.createElement('canvas');
    canvas.style.cssText = 'position:fixed;left:0;top:0;width:400px;height:300px';
    document.body.append(canvas);
    const ctx = canvas.getContext('2d');
    const strokes = [];
    let lines = 0;
    const proto = CanvasRenderingContext2D.prototype;
    ctx.beginPath = function () { lines = 0; return proto.beginPath.call(this); };
    ctx.lineTo = function (...a) { lines++; return proto.lineTo.apply(this, a); };
    ctx.stroke = function (...a) { strokes.push({ alpha: this.globalAlpha, lines }); return proto.stroke.apply(this, a); };
    const graph = window.MemoryGraph.create(canvas, {});
    graph.setData([{ id: 'a', title: 'A' }, { id: 'b', title: 'B' }, { id: 'c', title: 'C' }], [[0, 1], [0, 2]]);
    graph.setVisible((n) => n.id !== 'b');
    graph.select('a');
    await new Promise((resolve) => setTimeout(resolve, 300));
    graph.destroy();
    canvas.remove();
    return strokes.filter((s) => Math.abs(s.alpha - 0.95) < 0.01).map((s) => s.lines);
  });
  expect(highlighted.length).toBeGreaterThan(0);
  expect(highlighted.every((n) => n === 1), `links drawn in each highlight pass: ${highlighted}`).toBe(true);
});
