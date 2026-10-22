// What users, gateway clients, model backends and note files send is shown as text, never as markup (#178). A marker
// that breaks out of a quoted attribute and opens an element goes into every name and text they control; then every
// page is opened with its items expanded, and the marker must not have become an element, run, or tripped the
// Content-Security-Policy.
const fs = require('fs');
const path = require('path');
const { test: base, expect } = require('@playwright/test');

const stub = `http://127.0.0.1:${Number(process.env.E2E_PORT || 5190) + 1}`;
const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'semantic-router.json'), 'utf8').replace(/__STUB__/g, stub));

const PAGES = ['overview', 'live', 'activity', 'usage', 'memory', 'tiers', 'routes', 'routing', 'profiles', 'evaluate', 'learned', 'models', 'connectors', 'embeddings', 'gateway', 'clients', 'service', 'json', 'audit', 'feedback'];

// Closes a double- or single-quoted attribute and opens an <img> whose handler sets a flag. No "." in it, so a name that
// carries it is still one segment of a data-bind path.
const X = `"'><img src=x onerror=__xss=1>`;
const marked = (s) => s + X;

// As in the other specs: a test fails if the page throws, logs an error (a refused inline handler is one) or asks
// another host for anything. Every page also records Content-Security-Policy violations.
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
    await page.addInitScript(() => {
      window.__csp = [];
      document.addEventListener('securitypolicyviolation', (e) => window.__csp.push(`${e.violatedDirective}: ${e.sample || e.blockedURI}`));
    });
    await use(page);
    expect(errors, 'the dashboard logged errors').toEqual([]);
  },
});

/** The fixture, with the marker in the names and text an admin types: a connector (its models follow), a route, a profile, a virtual model, a budget. */
function markedConfig() {
  const c = JSON.parse(JSON.stringify(fixture));
  const [first] = Object.keys(c.Connectors);
  const connector = marked('local');
  c.Connectors = Object.fromEntries(Object.entries(c.Connectors).map(([k, v]) => [k === first ? connector : k, v]));
  for (const m of c.Models) {
    if (m.Connector === first) m.Connector = connector;
    m.Description = X;
  }
  c.Routes.push({ Name: marked('route'), Tier: 1, Description: X, Utterances: [marked('a marked utterance')] });
  c.Profiles = { ...(c.Profiles || {}), [marked('profile')]: { Description: X } };
  c.Middleware = { ...(c.Middleware || {}) };
  c.Middleware.VirtualModels = { ...(c.Middleware.VirtualModels || {}), [marked('virtual')]: { Profile: marked('profile'), Description: X } };
  c.Usage = { ...(c.Usage || {}) };
  c.Usage.Budgets = [...(c.Usage.Budgets || []), { Name: marked('budget'), Profile: marked('profile'), Period: 'Day', MaxCostUsd: 1 }];
  return c;
}

test.beforeEach(async ({ request }) => {
  const reset = await request.put('/router/api/config?persist=false', { data: markedConfig() });
  expect(reset.ok(), await reset.text()).toBeTruthy();
  // Activity, Live and Usage: a prompt, a profile and a model name as a gateway client sends them.
  const res = await request.post('/v1/chat/completions', {
    headers: { 'X-Router-Profile': marked('profile') },
    data: { model: marked('virtual'), messages: [{ role: 'user', content: marked('a prompt from a client') }] },
  });
  expect(res.ok(), await res.text()).toBeTruthy();
});

test.afterEach(async ({ request }) => {
  await request.delete('/router/api/activity');
  await request.put('/router/api/config?persist=false', { data: fixture });
});

async function open(page, view) {
  await page.goto(`/router#/${view}`);
  await expect(page.locator('#view h1').first()).toBeVisible();
}

async function expandAll(page) {
  const closed = page.locator('.item-head[aria-expanded="false"]:visible');
  for (let i = 0; i < 50 && await closed.count() > 0; i++) await closed.first().click();
  await page.evaluate(() => document.querySelectorAll('#view details').forEach((d) => { d.open = true; }));
}

/** Nothing the marker carried became an element, ran, or was refused by the Content-Security-Policy. */
async function expectInert(page, where) {
  const found = await page.evaluate(() => ({
    ran: window.__xss !== undefined,
    elements: document.querySelectorAll('img[src="x"], [onerror]').length,
    csp: window.__csp,
  }));
  expect(found, where).toEqual({ ran: false, elements: 0, csp: [] });
}

test('names and text from the configuration and from gateway clients stay text on every page', async ({ page }) => {
  test.setTimeout(120_000);
  for (const view of PAGES) {
    await open(page, view);
    await expandAll(page);
    await expectInert(page, `#/${view}`);
  }
  // The marker is on the pages as text, so the pages above did render it.
  await open(page, 'connectors');
  await expect(page.locator('#view .item-head .title').first()).toHaveText(marked('local'));
  await open(page, 'profiles');
  await expandAll(page);
  const descriptions = await page.locator('#view [data-bind$=".Description"]').evaluateAll((els) => els.map((e) => [e.dataset.bind, e.value]));
  expect(descriptions).toContainEqual([`Profiles.${marked('profile')}.Description`, X]);
  expect(descriptions).toContainEqual([`Middleware.VirtualModels.${marked('virtual')}.Description`, X]);
  await open(page, 'activity');
  await expect(page.locator('#view')).toContainText(marked('a prompt from a client'));
  await page.locator('#view tr[data-entry-id]').first().click();
  await expectInert(page, 'the activity entry opened');
});

test('an edited field still binds to a connector and a profile whose name needs escaping', async ({ page }) => {
  const field = (bind) => page.locator(`#view [data-bind="${bind.replace(/["\\]/g, '\\$&')}"]`);
  await open(page, 'connectors');
  await expandAll(page);
  await field(`Connectors.${marked('local')}.RequestPath`).fill('/v2/chat');
  await open(page, 'profiles');
  await expandAll(page);
  await field(`Profiles.${marked('profile')}.Description`).fill('edited');
  // The draft holds both edits under the right names, and the Review dialog shows them as text.
  const draft = await page.evaluate(async () => (await import('/router/js/core.js')).state.config);
  expect(draft.Connectors[marked('local')].RequestPath).toBe('/v2/chat');
  expect(draft.Profiles[marked('profile')].Description).toBe('edited');
  await page.locator('[data-action="draft-review"]').first().click();
  await expect(page.locator('#ask')).toContainText(JSON.stringify(marked('local')));
  await expectInert(page, 'the Review dialog');
});

test('text from model backends and note files stays text', async ({ page }) => {
  // Note titles, paths, tags and links come from files on disk; the lineup and its notes from a backend's model list.
  const note = (id, extra) => ({ id, title: marked(id), source: marked('vault'), path: marked('notes/a'), tags: [X], aliases: [X], ...extra });
  await page.route('**/router/api/memory', (r) => r.fulfill({ json: {
    nodes: [note('one'), note('two'), { id: 'gone', title: marked('gone'), missing: true }],
    links: [[0, 1], [0, 2]],
    sources: [{ name: marked('vault'), kind: 'Markdown', notes: 2, skipped: 0, error: X }],
    exposure: { one: { cloudContent: 1, cloudReference: 0, localContent: 0, localReference: 1, lastShared: new Date().toISOString(), models: { [marked('model')]: 1 } } },
    trackSharing: true, mode: 'Dispatch', loadedAt: new Date().toISOString(),
  } }));
  await page.route('**/router/api/connectors/discover', (r) => r.fulfill({ json: {
    models: [marked('model')], note: X, server: marked('server'), loading: 'OnDemand', loadingNote: X, familiesWarning: X, families: [X],
    lineup: [{ id: marked('model'), kind: 'Chat', listed: true, loaded: true, inCatalog: marked('model'), recommended: true, reason: X,
      suggestion: { Name: marked('model'), Connector: marked('local'), Model: marked('model'), Tier: 1, Capabilities: ['chat'], IsLocal: true, Enabled: true } }],
  } }));
  await page.route('**/router/api/digest', async (r) => {
    const json = await (await r.fetch()).json();
    json.notices = [{ kind: X, title: X, detail: X, link: 'javascript:__xss=1' }];
    json.referenceModel = marked('reference');
    await r.fulfill({ json });
  });

  await open(page, 'memory');
  await expect(page.locator('#view')).toContainText(marked('vault'));
  await page.locator('#view [data-action="memory-select"]').first().click();
  await expect(page.locator('#memory-side h3').first()).toHaveText(marked('one'));
  await expectInert(page, '#/memory');

  await open(page, 'connectors');
  await expandAll(page);
  await page.locator('[data-action="lineup-discover"]').first().click();
  await expect(page.locator('#view')).toContainText(marked('server'));
  await expectInert(page, 'the discovered lineup');

  await open(page, 'overview');
  await expect(page.locator('.notices a', { hasText: 'decide' })).toHaveAttribute('href', '#/overview');
  await expectInert(page, '#/overview');
});
