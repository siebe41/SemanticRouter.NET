// End-to-end tests of keeping a draft safe and saying what happened: the save bars, a configuration changed under the
// draft, problems tied to their fields, removals with Undo, busy buttons, load errors, the Activity table holding still,
// the status pill and the read-only role. Answers the router does not give yet (or that are hard to provoke) are
// mocked with page.route, so the tests hold for routers with and without them.
const fs = require('fs');
const path = require('path');
const { test: base, expect } = require('@playwright/test');

const fixture = JSON.parse(fs.readFileSync(path.join(__dirname, 'fixtures', 'semantic-router.json'), 'utf8')
  .replace(/__STUB__/g, `http://127.0.0.1:${Number(process.env.E2E_PORT || 5190) + 1}`));

// As in dashboard.spec.js: a test fails when the page throws or logs an error. Mocked failures (a 500 the test asks
// for) log a failed resource in the console, which those tests allow.
const test = base.extend({
  page: async ({ page }, use) => {
    const errors = [];
    page.on('pageerror', (e) => errors.push(`pageerror: ${e.message}`));
    page.on('console', (m) => { if (m.type() === 'error' && !/Failed to load resource/.test(m.text())) errors.push(`console: ${m.text()}`); });
    await use(page);
    expect(errors, 'the dashboard logged errors').toEqual([]);
  },
});

test.beforeEach(async ({ request, page }) => {
  const reset = await request.put('/router/api/config?persist=false', { data: fixture });
  expect(reset.ok(), await reset.text()).toBeTruthy();
  await page.addInitScript(() => localStorage.removeItem('semantic-router.theme'));
});

async function open(page, view) {
  await page.goto(`/router#/${view}`);
  await expect(page.locator('#view h1').first()).toBeVisible();
}

/** Answers GET api/status with the real status, changed by edit. */
async function patchStatus(page, edit) {
  await page.route('**/router/api/status', async (route) => {
    const res = await route.fetch();
    const json = await res.json();
    edit(json);
    await route.fulfill({ response: res, json });
  });
}

const dialog = (page) => page.locator('dialog#ask');

test('the save bar counts the changes, offers Apply & save first and shows the diff', async ({ page }) => {
  await open(page, 'routes');
  await page.locator('.item-head', { hasText: 'smalltalk' }).click();
  await page.locator('textarea[data-bind="Routes.0.Utterances"]').fill('hello there\ngood morning');
  const bar = page.locator('#save-bar');
  await expect(bar).toContainText('1 unapplied change');
  await expect(bar.locator('[data-action="apply"]')).toHaveText('Apply only');
  await expect(bar.locator('[data-action="apply"]')).toHaveAttribute('title', /restart undoes it/);
  await expect(bar.locator('.btn.primary')).toHaveText('Apply & save');
  await page.locator('input[data-bind="Routes.0.Name"]').fill('chitchat'); // a rename is one change
  await expect(bar).toContainText('2 unapplied changes');
  await bar.getByRole('button', { name: 'Review' }).click();
  await expect(dialog(page).locator('pre.diff')).toContainText('good morning');
  await expect(dialog(page).locator('pre.diff .diff-del').first()).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(dialog(page)).toBeHidden();
  await expect(bar.getByRole('button', { name: 'Review' })).toBeFocused();
});

test('a running configuration that is not saved says so, with Save and Revert for the admin', async ({ page }) => {
  await patchStatus(page, (s) => { s.unsaved = true; s.source = 'Memory'; });
  let saved = 0;
  await page.route('**/router/api/config/save', async (route) => {
    saved++;
    const res = await route.fetch({ method: 'GET', url: route.request().url().replace(/\/save$/, '') });
    await route.fulfill({ response: res, json: { ...(await res.json()), store: 'semantic-router.json' } });
  });
  await open(page, 'overview');
  const bar = page.locator('#unsaved-bar');
  await expect(bar).toContainText('Running configuration not saved');
  await page.locator('#more-toggle').click();
  await expect(page.locator('#brand-sub')).toContainText('not saved');
  await page.locator('#more-toggle').click();
  await bar.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('#toast')).toContainText('Saved to semantic-router.json');
  expect(saved).toBe(1);
  // Revert asks first.
  await bar.getByRole('button', { name: 'Revert to saved' }).click();
  await expect(dialog(page)).toContainText('Revert to the saved configuration?');
  await dialog(page).getByRole('button', { name: 'Cancel' }).click();
  await expect(dialog(page)).toBeHidden();
});

test('a router without the save endpoints shows the note but no buttons after the first try', async ({ page }) => {
  await patchStatus(page, (s) => { s.unsaved = true; });
  await page.route('**/router/api/config/save', (route) => route.fulfill({ status: 404, json: { title: 'Not Found' } }));
  await open(page, 'overview');
  const bar = page.locator('#unsaved-bar');
  await bar.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.locator('#toast')).toContainText('cannot save from here');
  await expect(bar).toContainText('Running configuration not saved');
  await expect(bar.getByRole('button')).toHaveCount(0);
});

test('Apply sends the version the draft started from; a 409 offers reload, compare or apply anyway', async ({ page }) => {
  const sent = [];
  await page.route(/\/router\/api\/config\?/, async (route) => {
    const url = new URL(route.request().url());
    sent.push(url.searchParams.get('baseVersion'));
    if (route.request().method() === 'PUT' && sent.length === 1) {
      await route.fulfill({ status: 409, json: { title: 'The configuration changed since you loaded it.', currentVersion: 99, actor: 'admin token', at: new Date().toISOString(), source: 'admin API' } });
    } else await route.continue();
  });
  await open(page, 'routes');
  const version = String((await (await page.request.get('/router/api/config')).json()).version);
  await page.locator('.item-head', { hasText: 'smalltalk' }).click();
  await page.locator('textarea[data-bind="Routes.0.Utterances"]').fill('hello there\ngood evening');
  await page.locator('#save-bar [data-action="apply"]').click();
  const notice = page.locator('#draft-notice');
  await expect(notice).toContainText('Configuration changed by admin token');
  await expect(notice).toContainText('admin API');
  expect(sent[0]).toBe(version);
  await notice.getByRole('button', { name: 'Compare' }).click();
  await expect(dialog(page).locator('pre.diff')).toContainText('good evening');
  await dialog(page).getByRole('button', { name: 'Close' }).click();
  await notice.getByRole('button', { name: 'Apply mine anyway' }).click();
  await expect(page.locator('#toast')).toContainText('Applied');
  expect(sent[1]).toBe('99');
  await expect(notice).toBeHidden();
  const config = (await (await page.request.get('/router/api/config')).json()).config;
  expect(config.Routes[0].Utterances).toContain('good evening');
});

test('a change made elsewhere reloads a clean draft and warns about a dirty one', async ({ page, request }) => {
  await open(page, 'routes');
  // Clean: the page takes the new configuration on the next refresh.
  const renamed = JSON.parse(JSON.stringify(fixture)); renamed.Routes[1].Name = 'design';
  await request.put('/router/api/config?persist=false', { data: renamed });
  await expect(page.locator('.item-head', { hasText: 'design' })).toBeVisible({ timeout: 12000 });
  // Dirty: the draft stays and the notice says what happened.
  await page.locator('.item-head', { hasText: 'smalltalk' }).click();
  await page.locator('input[data-bind="Routes.0.Name"]').fill('chat');
  renamed.Routes[1].Name = 'systems';
  await request.put('/router/api/config?persist=false', { data: renamed });
  await expect(page.locator('#draft-notice')).toContainText('Configuration changed', { timeout: 12000 });
  await expect(page.locator('input[data-bind="Routes.0.Name"]')).toHaveValue('chat');
  await page.locator('#draft-notice').getByRole('button', { name: 'Reload (lose your changes)' }).click();
  await expect(page.locator('.item-head', { hasText: 'systems' })).toBeVisible();
  await expect(page.locator('#save-bar')).toBeHidden();
});

test('an utterance added from Evaluate keeps unapplied edits on top of the new configuration', async ({ page }) => {
  await page.route('**/router/api/routes/*/utterances', async (route) => {
    const res = await route.fetch();
    await route.fulfill({ response: res, json: { ...(await res.json()), saved: false } });
  });
  await open(page, 'models');
  await page.locator('.item-head', { hasText: 'local-small' }).click();
  await page.locator('input[data-bind="Models.0.Priority"]').fill('7');
  await page.evaluate(() => { location.hash = '#/evaluate'; });
  await page.locator('#evaluation-text').fill('{"prompt":"what is the weather like","tier":4,"route":"architecture"}');
  await page.locator('[data-action="eval-run"]').first().click();
  await page.locator('[data-action="eval-add-open"]').first().click();
  await page.locator('#add-route').selectOption('architecture');
  await page.locator('[data-action="eval-add-save"]').click();
  await expect(page.locator('#toast')).toContainText('applied, not saved');
  await expect(page.locator('#toast')).toContainText('kept on top');
  await page.evaluate(() => { location.hash = '#/models'; });
  await expect(page.locator('input[data-bind="Models.0.Priority"]')).toHaveValue('7');
  await expect(page.locator('#save-bar')).toContainText('1 unapplied change');
});

test('problems are shown next to their fields, and each listed problem links to its field', async ({ page }) => {
  await page.route(/\/router\/api\/config\?/, (route) => route.request().method() === 'PUT'
    ? route.fulfill({ status: 400, json: { valid: false, errors: ['Routes.1.Name: two routes are called smalltalk.', 'Models.1.Tier: no such tier.'], issues: [{ path: 'Routes.1.Name', message: 'Two routes are called smalltalk; rename one.' }, { path: 'Models.1.Tier', message: 'Pick a tier that exists.' }] } })
    : route.continue());
  await open(page, 'routes');
  await page.locator('.item-head', { hasText: 'architecture' }).click();
  await page.locator('input[data-bind="Routes.1.Name"]').fill('smalltalk');
  await page.locator('.item-head').nth(1).click(); // closed again: the problem opens what it needs
  await expect(page.locator('.item-head').nth(1)).toHaveAttribute('aria-expanded', 'false');
  await page.locator('#save-bar [data-action="apply-save"]').click();
  const field = page.locator('input[data-bind="Routes.1.Name"]');
  await expect(field).toHaveAttribute('aria-invalid', 'true');
  await expect(field).toBeFocused();
  const id = (await field.getAttribute('aria-describedby')).split(' ').pop();
  await expect(page.locator(`#${id}`)).toHaveText('Two routes are called smalltalk; rename one.');
  await expect(page.locator('#toast')).toContainText('Not applied: 2 problems');
  // The other problem is on the Models page: its link goes there and focuses the field, its item opened.
  await page.locator('#problems a', { hasText: 'Pick a tier that exists.' }).click();
  await expect(page.locator('#view h1')).toHaveText('Models');
  const tier = page.locator('select[data-bind="Models.1.Tier"]');
  await expect(tier).toBeFocused();
  await expect(tier).toHaveAttribute('aria-invalid', 'true');
  // The "Not applied" toast stays until it is dismissed.
  await page.waitForTimeout(500);
  await expect(page.locator('#toast')).toContainText('Not applied: 2 problems');
});

test('an error page from a proxy shows what it said, not only the status', async ({ page }) => {
  await page.route(/\/router\/api\/config\?/, (route) => route.request().method() === 'PUT'
    ? route.fulfill({ status: 502, contentType: 'text/html', body: '<html><body><h1>Bad gateway</h1><p>The upstream did not answer.</p></body></html>' })
    : route.continue());
  await open(page, 'routes');
  await page.locator('.item-head', { hasText: 'smalltalk' }).click();
  await page.locator('input[data-bind="Routes.0.Name"]').fill('chat');
  await page.locator('#save-bar [data-action="apply"]').click();
  await expect(page.locator('#problems')).toContainText('Bad gateway The upstream did not answer. (HTTP 502)');
});

test('model aliases are rows: duplicates are flagged, order can change, Claude Code defaults keep yours', async ({ page, request }) => {
  await open(page, 'gateway');
  await page.locator('[data-action="alias-add"]').click();
  await expect(page.locator('[data-row="alias-0"]')).toBeFocused();
  await page.locator('[data-row="alias-0"]').fill('*opus*');
  await page.locator('[data-row="alias-0"]').press('Tab');
  await page.locator('[data-action-input="alias-value"][data-index="0"]').fill('family:opus');
  await page.locator('[data-action="alias-add"]').click();
  await page.locator('[data-row="alias-1"]').fill('*OPUS*');
  await page.locator('[data-row="alias-1"]').press('Tab');
  // Two rows, the second flagged: nothing merged away.
  await expect(page.locator('[data-action-input="alias-key"]')).toHaveCount(2);
  await expect(page.locator('[data-row="alias-1"]')).toHaveAttribute('aria-invalid', 'true');
  await expect(page.locator('#alias-problem-1')).toContainText('Row 1 has the same name');
  await page.locator('#save-bar [data-action="apply"]').click();
  await expect(page.locator('#toast')).toContainText('Not applied: 1 problem');
  await page.locator('[data-row="alias-1"]').fill('claude-*');
  await page.locator('[data-row="alias-1"]').press('Tab');
  await expect(page.locator('[data-row="alias-1"]')).not.toHaveAttribute('aria-invalid', 'true');
  // Order: the second glob moves up.
  await page.getByRole('button', { name: 'Move claude-* up' }).click();
  await expect(page.locator('[data-row="alias-0"]')).toHaveValue('claude-*');
  // The defaults add what is missing and leave *opus* → family:opus alone.
  await page.locator('[data-action="alias-claude"]').click();
  await expect(page.locator('#toast')).toContainText('kept your *opus* → family:opus');
  await page.locator('#save-bar [data-action="apply"]').click();
  await expect(page.locator('#toast')).toContainText('Applied');
  const aliases = (await (await request.get('/router/api/config')).json()).config.Middleware.ModelAliases;
  expect(Object.keys(aliases).slice(0, 2)).toEqual(['claude-*', '*opus*']);
  expect(aliases['*opus*']).toBe('family:opus');
  expect(aliases['*haiku*']).toBe('auto:cheapest');
});

test('removing a route can be undone, and the next route does not open in its place', async ({ page }) => {
  await open(page, 'routes');
  await page.locator('.item-head', { hasText: 'smalltalk' }).click();
  await page.locator('.item.open [data-action="route-remove"]').click();
  await expect(page.locator('.item')).toHaveCount(1);
  await expect(page.locator('.item-head', { hasText: 'architecture' })).toHaveAttribute('aria-expanded', 'false');
  await expect(page.locator('.item-head', { hasText: 'architecture' })).toBeFocused();
  await page.locator('#toast button', { hasText: 'Undo' }).click();
  await expect(page.locator('.item')).toHaveCount(2);
  await expect(page.locator('.item').first()).toContainText('smalltalk');
  await expect(page.locator('.item-head', { hasText: 'smalltalk' })).toHaveAttribute('aria-expanded', 'true');
  await expect(page.locator('#save-bar')).toBeHidden();
});

test('removing a connector that models use asks whether to remove them too, and Undo puts both back', async ({ page }) => {
  await open(page, 'connectors');
  await page.locator('.item-head', { hasText: 'local' }).first().click();
  await page.locator('.item.open [data-action="connector-remove"]').click();
  await expect(dialog(page)).toContainText('1 model uses it: local-small');
  await dialog(page).getByRole('button', { name: 'Remove it too' }).click();
  await expect(page.locator('#toast')).toContainText('Removed connector local and 1 model');
  await page.locator('#tabs a[data-view="models"]').click();
  await expect(page.locator('.item', { hasText: 'local-small' })).toHaveCount(0);
  await page.locator('#toast button', { hasText: 'Undo' }).click();
  await expect(page.locator('.item', { hasText: 'local-small' })).toHaveCount(1);
  await expect(page.locator('#save-bar')).toBeHidden();
});

test('a profile that rules name: keep them, or remove them too', async ({ page }) => {
  const withRule = JSON.parse(JSON.stringify(fixture));
  withRule.Middleware.ProfileRules = [{ Profile: 'private', Clients: ['laptop'] }];
  await page.request.put('/router/api/config?persist=false', { data: withRule });
  await open(page, 'profiles');
  await page.locator('.item-head', { hasText: 'private' }).click();
  await page.locator('.item.open [data-action="profile-remove"]').click();
  await expect(dialog(page)).toContainText('1 rule names it');
  await dialog(page).getByRole('button', { name: 'Remove it too' }).click();
  await expect(page.locator('select[data-bind^="Middleware.ProfileRules"]')).toHaveCount(0);
  await page.locator('#toast button', { hasText: 'Undo' }).click();
  await expect(page.locator('select[data-bind="Middleware.ProfileRules.0.Profile"]')).toHaveValue('private');
});

test('a refused rename says why', async ({ page }) => {
  await open(page, 'connectors');
  await page.locator('.item-head', { hasText: 'local' }).first().click();
  const name = page.locator('.item.open input[data-action-input="connector-rename"]');
  await name.fill('cloud');
  await name.press('Tab');
  await expect(page.locator('#toast')).toContainText('There is already a connector named cloud.');
  await expect(name).toHaveValue('local');
});

test('adding a route opens it and puts the cursor in its name', async ({ page }) => {
  await open(page, 'routes');
  await page.locator('[data-action="route-add"]').click();
  const name = page.locator('input[data-bind="Routes.2.Name"]');
  await expect(name).toBeFocused();
  await page.keyboard.type('cooking');
  await expect(name).toHaveValue('cooking');
});

test('a button is busy while its action runs, and a second click is not sent', async ({ page }) => {
  let puts = 0;
  await page.route(/\/router\/api\/config\?/, async (route) => {
    if (route.request().method() === 'PUT') { puts++; await new Promise((r) => setTimeout(r, 800)); }
    await route.continue();
  });
  await open(page, 'routes');
  await page.locator('.item-head', { hasText: 'smalltalk' }).click();
  await page.locator('input[data-bind="Routes.0.Name"]').fill('chat');
  const apply = page.locator('#save-bar [data-action="apply"]');
  await apply.click();
  await expect(apply).toHaveAttribute('aria-busy', 'true');
  await expect(apply).toBeDisabled();
  await apply.dispatchEvent('click');
  await expect(page.locator('#toast')).toContainText('Applied');
  expect(puts).toBe(1);
});

test('usage that fails to load says why, offers Retry and does not ask again by itself', async ({ page }) => {
  let asked = 0;
  await page.route(/\/router\/api\/usage\?/, (route) => { asked++; return route.fulfill({ status: 500, json: { title: 'The usage store is not available.' } }); });
  await open(page, 'usage');
  await expect(page.locator('#view')).toContainText("Couldn't load usage: The usage store is not available.");
  await page.waitForTimeout(1500);
  expect(asked).toBe(1);
  await page.getByRole('button', { name: 'Retry' }).click();
  await expect.poll(() => asked).toBe(2);
  await expect(page.locator('#view')).toContainText("Couldn't load usage");
});

test('trends that fail to load say why instead of asking again on every render', async ({ page }) => {
  let asked = 0;
  await page.route(/\/router\/api\/activity\/series/, (route) => { asked++; return route.fulfill({ status: 503, json: { title: 'Busy.' } }); });
  await open(page, 'overview');
  await expect(page.locator('#view')).toContainText("Couldn't load trends: Busy.");
  await page.waitForTimeout(6000); // one status refresh: it re-renders the Overview, and backs off the trends
  expect(asked).toBe(1);
});

test('the Overview keeps an opened table open across its refresh', async ({ page }) => {
  await open(page, 'overview');
  const summary = page.locator('summary', { hasText: 'Show as a table' });
  await summary.click();
  await page.locator('#view h1').evaluate((e) => { e.dataset.before = 'yes'; });
  await page.waitForFunction(() => !document.querySelector('#view h1').dataset.before, null, { timeout: 15000 });
  await expect(page.locator('details', { has: page.locator('summary', { hasText: 'Show as a table' }) })).toHaveAttribute('open', '');
});

test('the Activity table holds still while a label is being written, and counts what arrives', async ({ page, request }) => {
  const send = async (prompt) => expect((await request.post('/v1/chat/completions', { data: { model: 'auto', messages: [{ role: 'user', content: prompt }] } })).ok()).toBeTruthy();
  await send('hello there, first');
  await open(page, 'activity');
  await page.getByRole('button', { name: 'Label' }).first().click();
  await expect(page.locator('#label-form')).toBeVisible();
  await page.locator('#label-form textarea').fill('hello there, my own words');
  await send('hello there, second');
  await expect(page.locator('#activity-new')).toContainText('1 new entry', { timeout: 12000 });
  await expect(page.locator('#label-form textarea')).toHaveValue('hello there, my own words');
  await page.locator('[data-action="label-cancel"]').click();
  await expect(page.locator('#view table.activity-table')).toContainText('hello there, second');
});

test('the status pill shows the health in its dot, with a short word on a phone', async ({ page }) => {
  await patchStatus(page, (s) => { s.models.forEach((m) => { m.enabled = false; }); });
  await page.setViewportSize({ width: 390, height: 800 });
  await open(page, 'overview');
  const dot = page.locator('#status-pill .dot').first();
  await expect(dot).toHaveClass(/danger/);
  await expect(dot).toHaveAttribute('aria-label', /No model is enabled/);
  await expect(page.locator('#status-pill .pill-short')).toHaveText('0 models');
  await expect(page.locator('#status-pill .pill-short')).toBeVisible();
});

test('the read-only role sees forms it cannot change and no Send', async ({ page }) => {
  await page.route('**/router/api/session', (route) => route.request().method() === 'GET'
    ? route.fulfill({ json: { role: 'ReadOnly', tokenRequired: true, tokensConfigured: true, identity: 'read-only token' } })
    : route.continue());
  await open(page, 'routes');
  await expect(page.locator('[data-action="route-add"]')).toBeHidden();
  await page.locator('.item-head', { hasText: 'smalltalk' }).click();
  await expect(page.locator('input[data-bind="Routes.0.Name"]')).toBeDisabled();
  await expect(page.locator('.item.open [data-action="route-remove"]')).toBeHidden();
  await page.locator('#nav a', { hasText: 'Monitor' }).click();
  await expect(page.locator('[data-action="tester-send"]')).toBeDisabled();
  await expect(page.locator('#save-bar')).toBeHidden();
});

test('the JSON editor keeps typed text across pages and asks before replacing it', async ({ page }) => {
  await open(page, 'json');
  const editor = page.locator('#json-editor');
  await editor.fill('{ "FallbackTier": 2 }');
  await page.locator('#tabs a[data-view="audit"]').click();
  await page.locator('#tabs a[data-view="json"]').click();
  await expect(editor).toHaveValue('{ "FallbackTier": 2 }');
  await page.locator('[data-action="json-baseline"]').click();
  await expect(dialog(page)).toContainText('Replace the text in the editor?');
  await dialog(page).getByRole('button', { name: 'Keep my text' }).click();
  await expect(editor).toHaveValue('{ "FallbackTier": 2 }');
});

test('a draft moves onto a changed configuration unless both changed the same setting', async ({ page }) => {
  await open(page, 'overview');
  const r = await page.evaluate(async () => {
    const { rebase, countChanges } = await import('/router/js/draft.js');
    const base = { FallbackTier: 1, Routes: [{ Name: 'a', Tier: 1, Utterances: ['x', 'y'] }, { Name: 'b', Tier: 2, Utterances: [] }], Connectors: { local: { Timeout: '00:00:00' } } };
    const copy = () => JSON.parse(JSON.stringify(base));
    const mine = copy(); mine.Routes[0].Utterances.push('mine'); mine.Routes[1].Name = 'b2'; mine.Connectors.local.Timeout = '00:01:00';
    const theirs = copy(); theirs.Routes[0].Utterances.push('theirs'); theirs.Routes.push({ Name: 'c', Tier: 3, Utterances: [] }); theirs.FallbackTier = 2;
    const clash = copy(); clash.Connectors.local.Timeout = '00:02:00';
    return { merged: rebase(base, mine, theirs), clash: rebase(base, mine, clash), renamed: countChanges(base, { ...copy(), Routes: [base.Routes[0], { ...base.Routes[1], Name: 'b2' }] }) };
  });
  expect(r.merged.ok).toBe(true);
  expect(r.merged.value.FallbackTier).toBe(2);
  expect(r.merged.value.Routes.map((x) => x.Name)).toEqual(['a', 'b2', 'c']);
  expect(r.merged.value.Routes[0].Utterances).toEqual(['x', 'y', 'theirs', 'mine']);
  expect(r.merged.value.Connectors.local.Timeout).toBe('00:01:00');
  expect(r.clash.ok).toBe(false);
  expect(r.renamed).toBe(1);
});

test('signing in again keeps the draft', async ({ page }) => {
  await open(page, 'routes');
  await page.locator('.item-head', { hasText: 'smalltalk' }).click();
  await page.locator('input[data-bind="Routes.0.Name"]').fill('chat');
  // The session ends: the next refresh gets 401 and the sign-in form replaces the page.
  await page.route('**/router/api/status', (route) => route.fulfill({ status: 401, json: { title: 'Sign in.' } }));
  await expect(page.locator('#signin-form')).toBeVisible({ timeout: 12000 });
  await page.unroute('**/router/api/status');
  await page.route('**/router/api/session', (route) => route.fulfill({ json: { role: 'Admin', tokenRequired: true, tokensConfigured: true } }));
  await page.locator('#signin-token').fill('token');
  await page.locator('#signin-form button[type="submit"]').click();
  await expect(page.locator('input[data-bind="Routes.0.Name"]')).toHaveValue('chat');
  await expect(page.locator('#save-bar')).toContainText('1 unapplied change');
});
