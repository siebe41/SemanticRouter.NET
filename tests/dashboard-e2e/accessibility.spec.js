// WCAG 2.2 A and AA, checked with axe-core on every page, in every built-in theme, on a desktop and a phone.
// Every expandable item and <details> is opened first, so what they hold is checked too. axe runs through
// page.evaluate, which the dashboard's Content Security Policy (script-src 'self') does not block.
const fs = require('fs');
const path = require('path');
const { test, expect } = require('@playwright/test');

const stub = `http://127.0.0.1:${Number(process.env.E2E_PORT || 5190) + 1}`;
const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'semantic-router.json'), 'utf8').replace(/__STUB__/g, stub));
const axeSource = fs.readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8');

const PAGES = ['overview', 'live', 'activity', 'usage', 'memory', 'tiers', 'routes', 'routing', 'profiles', 'evaluate', 'learned', 'models', 'connectors', 'embeddings', 'gateway', 'clients', 'json', 'audit', 'feedback'];
const THEMES = ['machine-dark', 'machine-light', 'terminal'];
const SIZES = { desktop: { width: 1360, height: 900 }, phone: { width: 390, height: 844 } };
const RULES = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];

test.beforeEach(async ({ request }) => {
  const reset = await request.put('/router/api/config?persist=false', { data: fixture });
  expect(reset.ok(), await reset.text()).toBeTruthy();
  // Activity, so the tables, the feed and the charts have rows.
  for (const prompt of ['hello there, how are you?', 'Write a Python function that parses ISO dates']) {
    await request.post('/v1/chat/completions', { data: { model: 'auto', messages: [{ role: 'user', content: prompt }] } });
  }
});

// The other suites count activity and take screenshots: leave none behind.
test.afterEach(async ({ request }) => {
  await request.delete('/router/api/activity');
});

for (const [size, viewport] of Object.entries(SIZES)) {
  for (const theme of THEMES) {
    test(`every page meets WCAG 2.2 AA: ${theme} on a ${size}`, async ({ browser }) => {
      test.setTimeout(120_000);
      const context = await browser.newContext({ viewport, baseURL: test.info().project.use.baseURL });
      await context.addInitScript((t) => { try { localStorage.setItem('semantic-router.theme', t); } catch { /* no storage */ } }, theme);
      const page = await context.newPage();
      const found = {};
      for (const view of PAGES) {
        await page.goto(`/router#/${view}`);
        await expect(page.locator('#view h1').first()).toBeVisible();
        const closed = page.locator('.item-head[aria-expanded="false"]:visible');
        for (let i = 0; i < 50 && await closed.count() > 0; i++) await closed.first().click();
        await page.evaluate(() => document.querySelectorAll('#view details').forEach((d) => { d.open = true; }));
        await page.evaluate(axeSource);
        const violations = await page.evaluate((tags) => window.axe.run(document, { runOnly: { type: 'tag', values: tags }, resultTypes: ['violations'] }).then((r) => r.violations), RULES);
        for (const v of violations) {
          (found[view] = found[view] || []).push(`${v.id} (${v.impact}): ${v.help} — ${v.nodes.slice(0, 3).map((n) => n.target.join(' ')).join(', ')}`);
        }
      }
      await context.close();
      expect(found, 'axe-core violations, by page').toEqual({});
    });
  }
}
