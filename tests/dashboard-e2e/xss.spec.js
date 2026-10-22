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

// Closes a double- or single-quoted attribute or a <textarea> and opens an <img> whose handler sets a flag. No "." in it,
// so a name that carries it is still one segment of a data-bind path.
const X = `"'></textarea><img src=x onerror=__xss=1>`;
const marked = (s) => s + X;

// As in the other specs: a test fails if the page throws, logs an error (a refused inline handler is one) or asks
// another host for anything. Every page also records Content-Security-Policy violations.
const test = base.extend({
  page: async ({ page, baseURL }, use) => {
    const errors = [];
    const own = new URL(baseURL).origin;
    // The dashboard's API calls still waiting for an answer, without the Live page's event stream, which never ends.
    page.inflight = new Set();
    const settled = (r) => page.inflight.delete(r);
    page.on('request', (r) => {
      const url = r.url();
      if (!url.startsWith('data:') && !url.startsWith('blob:') && new URL(url).origin !== own) errors.push(`request to another host: ${url}`);
      else if (url.includes('/router/api/') && !url.includes('/router/api/events')) page.inflight.add(r);
    });
    page.on('requestfinished', settled);
    page.on('requestfailed', settled);
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

// A connector of each other type, so the Connectors page draws every branch of its form: the Cli and Copilot fields, the
// Anthropic ones, and the Change / Clear buttons of a key that is set or a reference. Only the page sees them (the router
// never builds them, so no vendor CLI or Copilot runtime can start); the rest of the configuration is the router's own.
const EXTRA_CONNECTORS = {
  [marked('cli')]: { Type: 'Cli', Preset: 'Custom', Executable: X, ToolCalling: 'Prompted', Arguments: [X], QuotaErrorPatterns: [X], EnvironmentVariables: { SR_E2E: X }, RemoveEnvironmentVariables: [X] },
  [marked('copilot')]: { Type: 'Copilot', Executable: X, HomeDirectory: X },
  [marked('anthropic')]: { Type: 'Anthropic', BaseAddress: stub, ApiKey: '***', AnthropicVersion: X, Headers: { 'X-E2E': X }, ForwardHeaders: [X] },
  [marked('reference')]: { Type: 'OpenAICompatible', BaseAddress: stub, ApiKey: '${env:SR_E2E_KEY}' },
};
/** Fields of those connectors and the value each must show: a sample of every branch, inputs and textareas. */
const EXTRA_FIELDS = [
  [`Connectors.${marked('cli')}.Executable`, X],
  [`Connectors.${marked('cli')}.Arguments`, X],
  [`Connectors.${marked('cli')}.EnvironmentVariables`, `SR_E2E=${X}`],
  [`Connectors.${marked('copilot')}.HomeDirectory`, X],
  [`Connectors.${marked('anthropic')}.AnthropicVersion`, X],
  [`Connectors.${marked('anthropic')}.ForwardHeaders`, X],
];

test.beforeEach(async ({ request, page }) => {
  const reset = await request.put('/router/api/config?persist=false', { data: markedConfig() });
  expect(reset.ok(), await reset.text()).toBeTruthy();
  await page.route('**/router/api/config', async (r) => {
    if (r.request().method() !== 'GET') return r.fallback();
    const json = await (await r.fetch()).json();
    json.config.Connectors = { ...json.config.Connectors, ...EXTRA_CONNECTORS };
    await r.fulfill({ json });
  });
  // The Add connector menu's look for providers on this machine: slow, and nothing in it comes from this test.
  await page.route('**/router/api/setup', (r) => r.fulfill({ json: { needed: false, detected: [], presets: [] } }));
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
  // A view that fetches its data draws a placeholder first, and after the first page a hash change keeps the document,
  // so the load state says nothing: wait for the placeholder to go and for the API calls to be answered.
  await expect(page.locator('#view'), `#/${view} is still loading`).not.toContainText(/Loading…|Scanning memory sources…/);
  await expect.poll(() => [...page.inflight].map((r) => r.url()), { message: `#/${view} is still fetching` }).toEqual([]);
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
  await open(page, 'usage');
  await expect(page.locator('#view td b', { hasText: marked('budget') })).toHaveText(marked('budget'));
  await open(page, 'connectors');
  await expect(page.locator('#view .item-head .title').first()).toHaveText(marked('local'));
  await expandAll(page);
  const values = await page.locator('#view [data-bind]').evaluateAll((els) => els.map((e) => [e.dataset.bind, e.value]));
  for (const field of EXTRA_FIELDS) expect(values).toContainEqual(field);
  const paths = await page.locator('#view [data-action^="secret-"]').evaluateAll((els) => els.map((e) => e.dataset.path));
  for (const name of [marked('anthropic'), marked('reference')]) expect(paths.filter((p) => p === `Connectors.${name}.ApiKey`)).toHaveLength(2);
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
  await field(`Connectors.${marked('cli')}.Executable`).fill('cli-edited');
  await field(`Connectors.${marked('copilot')}.HomeDirectory`).fill('home-edited');
  await field(`Connectors.${marked('anthropic')}.AnthropicVersion`).fill('2024-01-01');
  await open(page, 'profiles');
  await expandAll(page);
  await field(`Profiles.${marked('profile')}.Description`).fill('edited');
  // The draft holds both edits under the right names, and the Review dialog shows them as text.
  const draft = await page.evaluate(async () => (await import('/router/js/core.js')).state.config);
  expect(draft.Connectors[marked('local')].RequestPath).toBe('/v2/chat');
  expect(draft.Connectors[marked('cli')].Executable).toBe('cli-edited');
  expect(draft.Connectors[marked('copilot')].HomeDirectory).toBe('home-edited');
  expect(draft.Connectors[marked('anthropic')].AnthropicVersion).toBe('2024-01-01');
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
