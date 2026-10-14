// End-to-end tests of the dashboard: every page, the main editing flows, keyboard and screen-reader basics, themes
// (built-in and drop-in files) and screenshots. Each test starts from the fixture configuration.
const fs = require('fs');
const path = require('path');
const { test: base, expect } = require('@playwright/test');

const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'semantic-router.json'), 'utf8')
  .replace(/__STUB__/g, `http://127.0.0.1:${Number(process.env.E2E_PORT || 5190) + 1}`));

const PAGES = ['overview', 'live', 'activity', 'memory', 'usage', 'learned', 'tiers', 'routes', 'evaluate', 'models', 'connectors', 'routing', 'profiles', 'embeddings', 'gateway', 'clients', 'json'];
const THEMES = ['machine-dark', 'machine-light', 'terminal'];

// Every test fails if the page throws or logs an error. The web fonts are left out so that runs do not depend on a
// third-party CDN, and screenshots use fonts every Linux runner has.
const test = base.extend({
  page: async ({ page }, use) => {
    const errors = [];
    await page.route(/fonts\.bunny\.net/, (route) => route.fulfill({ status: 200, contentType: 'text/css', body: '' }));
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
    await expandAll(page);
    const found = await unnamedControls(page);
    if (found.length) unlabelled[view] = found;
  }
  expect(unlabelled, 'controls without an accessible name, by page').toEqual({});
});

test('the page has landmarks, a skip link and marks the current page', async ({ page }) => {
  await open(page, 'routes');
  await expect(page.locator('nav[aria-label="Pages"]')).toBeVisible();
  await expect(page.locator('main#view')).toBeVisible();
  await expect(page.locator('#nav a[aria-current="page"]')).toHaveText('Routes');
  await page.keyboard.press('Tab');
  await expect(page.locator('.skip-link')).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.locator('main#view')).toBeFocused();
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
    await page.selectOption('#theme-select', theme);
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

test('renaming a connector renames it in the models that use it', async ({ page, request }) => {
  await open(page, 'connectors');
  await page.locator('.item-head', { hasText: 'local' }).first().click();
  await page.locator('.item.open input[data-action-input="connector-rename"]').fill('lmstudio');
  await page.locator('#nav a[data-view="models"]').click();
  await expect(page.locator('.item', { hasText: 'local-small' })).toContainText('via lmstudio');
  await page.locator('#save-bar [data-action="apply"]').click();
  await expect(page.locator('#toast')).toContainText('Applied');
  const config = (await (await request.get('/router/api/config')).json()).config;
  expect(Object.keys(config.Connectors)).toContain('lmstudio');
  expect(config.Models.find((m) => m.Name === 'local-small').Connector).toBe('lmstudio');
});

test('validation lists problems and discard restores the configuration', async ({ page }) => {
  await open(page, 'routes');
  await page.locator('.item-head', { hasText: 'architecture' }).click();
  await page.locator('input[data-bind="Routes.1.Name"]').fill('smalltalk');
  await page.locator('#save-bar [data-action="validate"]').click();
  await expect(page.locator('ul.errors')).toContainText('smalltalk');
  await page.locator('#save-bar [data-action="discard"]').click();
  await expect(page.locator('#save-bar')).toBeHidden();
  await expect(page.locator('.item-head', { hasText: 'architecture' })).toBeVisible();
});

test('the prompt tester classifies and sends a prompt through the gateway', async ({ page }) => {
  await open(page, 'overview');
  await page.locator('#tester-prompt').fill('hello there, how are you?');
  await page.locator('[data-action="tester-classify"]').click();
  await expect(page.locator('.tester')).toContainText('smalltalk');
  await expect(page.locator('.tester')).toContainText('local-small');
  await page.locator('[data-action="tester-send"]').click();
  await expect(page.locator('.tester')).toContainText('Hello from the stub backend.');
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
  await page.selectOption('#theme-select', 'machine-light');
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
    await page.selectOption('#theme-select', 'dropped-in');
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
    await page.locator('#nav a[data-view="routes"]').click();
    await expect(page.locator('#view h1')).toHaveText('Routes');
    await expect(page.locator('#nav')).toBeHidden();
    await toggle.click();
    await page.keyboard.press('Escape');
    await expect(page.locator('#nav')).toBeHidden();
    await expect(toggle).toBeFocused();
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
