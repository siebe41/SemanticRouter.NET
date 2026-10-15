// The settings how-to (~4 min): where the settings live, adding a model LM Studio just downloaded, a paid API with its
// prices, fixing a misrouted prompt with Evaluate, a local-only profile, a budget, and doctor. Every change is made on
// the dashboard the way a person would make it, with the pointer on screen, and applied live.

// An obviously fake key, put together here so the repository holds no key-shaped string.
const fakeKey = ['sk', 'demo', 'not', 'a', 'real', 'key', '0042'].join('-');
const MISROUTED = 'explain step by step how to shard this database';

module.exports = async function settings(k) {
  const { sleep } = k;
  k.pointer = true;

  // Off camera: install, init, start; a couple of requests on the Claude subscription, so Usage has figures.
  await k.startRouter();
  await Promise.all([
    k.send('the invoice job deadlocks under load, find the root cause'),
    k.send('design a scalable architecture for a multi-tenant SaaS platform'),
  ]);
  await k.stage('layout', 'zoom');
  await k.go('json');

  const applyAndSave = async () => {
    await k.dashClick('[data-action="apply-save"]');
    await k.frame().locator('.toast:has-text("Applied")').last().waitFor({ timeout: 15000 });
  };

  await k.record();

  // Title.
  let line = k.say('title');
  await k.stage('title', 'Tuning it for your own models', 'How-to · settings', false);
  await line;
  await k.stage('hideCard');
  await sleep(400);

  // 1. Where settings live.
  line = k.say('where');
  await k.stage('caption', 'One file, and every page edits it', 'draft → Apply: live, no restart → Apply &amp; save: written to the file');
  await k.highlight('#json-editor');
  await sleep(5500);
  await k.dashClick('summary:has-text("Where does the configuration come from?")');
  await k.highlight('details:has(summary:has-text("Where does the configuration come from?"))');
  await line;
  await k.unhighlight();

  // 2. A model LM Studio just downloaded.
  k.lmstudio.addModel('google/gemma-3-12b');
  line = k.say('discover');
  await k.stage('caption', 'A new model in LM Studio', 'Models → Discover models → Add → Apply &amp; save');
  await k.nav('models');
  await k.dashClick('[data-action="lineup-discover"][data-connector="lmstudio"]');
  const gemma = 'tr:has-text("google/gemma-3-12b")';
  await k.frame().locator(gemma).first().waitFor();
  await k.highlight(gemma);
  await sleep(3500);
  await k.unhighlight();
  await k.dashCheck(`${gemma} input[type=checkbox]`);
  await k.dashClick('[data-action="lineup-add"]');
  await sleep(600);
  await applyAndSave();
  await line;

  // 3. A paid API: the connector.
  line = k.say('connector');
  await k.stage('caption', 'A paid API too', 'Connectors → Add connector · paid-api.example stands in for any OpenAI-compatible API');
  await k.nav('connectors');
  await k.dashClick('[data-action="connector-add"]');
  await k.dashType('.item.open [data-action-input="connector-rename"]', 'paid-api');
  await k.dashType('[data-bind="Connectors.paid-api.BaseAddress"]', 'http://paid-api.example:4000');
  await k.dashType('[data-bind="Connectors.paid-api.ApiKey"]', fakeKey);
  await k.highlight('label.field:has([data-bind="Connectors.paid-api.ApiKey"])');
  await line;
  await k.unhighlight();

  // 4. Its models and prices.
  line = k.say('prices');
  await k.stage('caption', 'Discover its models, and set their prices', 'cost per million tokens: what cheapest capable weighs');
  await applyAndSave();
  await k.dashClick('[data-action="lineup-discover"][data-connector="paid-api"]');
  const deepseek = 'tr:has-text("deepseek/deepseek-chat-v3")';
  await k.frame().locator(deepseek).first().waitFor();
  await k.dashCheck(`${deepseek} input[type=checkbox]`);
  await k.dashClick('[data-action="lineup-add"]');
  await sleep(500);
  await k.nav('models');
  await k.dashClick('.item:has(.title:text-is("paid-api-deepseek-chat-v3")) .item-head');
  await k.dashType('.item.open [data-bind$=".InputCostPerMillionTokens"]', '0.27');
  await k.dashType('.item.open [data-bind$=".OutputCostPerMillionTokens"]', '1.10');
  await applyAndSave();
  await k.highlight('.item:has(.title:text-is("paid-api-deepseek-chat-v3")) .item-head');
  await line;
  await k.unhighlight();

  // 5. Fixing a misroute with Evaluate.
  line = k.say('evaluate');
  await k.stage('caption', 'A prompt on the wrong tier?', 'Evaluate → Run → Add to route… → it runs again');
  await k.nav('evaluate');
  await k.dashClick('[data-action="eval-run"]:not([data-calibrate])');
  await k.frame().locator('.card.stat').first().waitFor();
  await k.highlight('.card.stat >> nth=0');
  await sleep(2500);
  const miss = `tr:has-text("${MISROUTED}")`;
  await k.dashScroll(miss);
  await k.highlight(miss);
  await sleep(2500);
  await k.unhighlight();
  await k.dashClick(`${miss} [data-action="eval-add-open"]`);
  await k.dashSelect('#add-route', 'deep-reasoning');
  await k.dashClick('[data-action="eval-add-save"]');
  await k.frame().locator('.card.stat:has-text("85 of 88")').first().waitFor({ timeout: 30000 });
  await k.dashScroll('.card.stat >> nth=0');
  await k.highlight('.card.stat >> nth=0');
  await line;
  await k.unhighlight();

  // 6. A local-only profile for a project.
  line = k.say('profiles');
  await k.stage('caption', 'Keep a project on this machine', 'Profiles → local only · a rule picks it by project');
  await k.nav('profiles');
  await k.dashClick('[data-action="profile-add"]');
  await k.dashType('.item.open [data-action-input="profile-rename"]', 'private');
  await k.dashCheck('[data-bind="Profiles.private.LocalOnly"]');
  await k.dashClick('[data-action="rule-profile-add"]');
  await k.dashSelect('[data-bind="Middleware.ProfileRules.0.Profile"]', 'private');
  await k.dashType('[data-bind="Middleware.ProfileRules.0.Projects"]', 'billing*');
  await applyAndSave();
  await k.dashType('#profile-prompt', 'refactor the invoice service');
  await k.dashClick('[data-action="profiles-compare"]');
  const compare = '.card:has(h2:has-text("What would each profile do?")) table';
  await k.frame().locator(compare).first().waitFor();
  await k.highlight(compare);
  await line;
  await k.unhighlight();

  // 7. A budget.
  line = k.say('budget');
  await k.stage('caption', 'Cap what a paid API can spend', 'Usage → Budgets · spent: the router skips it until the period resets');
  await k.nav('usage');
  await k.highlight('.card.stat:has-text("subscription use")');
  await sleep(3500);
  await k.unhighlight();
  await k.dashClick('[data-action="budget-add"]');
  await k.dashType('[data-bind="Usage.Budgets.0.Name"]', 'paid-api-daily');
  await k.dashSelect('[data-bind="Usage.Budgets.0.Connector"]', 'paid-api');
  await k.dashType('[data-bind="Usage.Budgets.0.MaxCostUsd"]', '2');
  await applyAndSave();
  await k.highlight('tr:has([data-bind="Usage.Budgets.0.Name"])');
  await line;
  await k.unhighlight();

  // 8. Doctor.
  await k.stage('hideCaption');
  await k.stage('layout', 'setup');
  await sleep(600);
  line = k.say('doctor');
  await k.stage('caption', 'When in doubt: doctor', 'checks the configuration, embeddings and every connector · calls no model');
  await k.type('router', 'semantic-router doctor');
  await k.waitFor('router', /checks passed|problem|warning/i);
  await line;
  await sleep(800);

  // End.
  await k.stage('hideCaption');
  line = k.say('end');
  await k.stage('end');
  await line;
  await sleep(1500);
};

// The stand-in paid API's models (kit.start serves them at paid-api.example).
module.exports.paidApi = ['deepseek/deepseek-chat-v3', 'qwen/qwen3-235b-a22b'];
