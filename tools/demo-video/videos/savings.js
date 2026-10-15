// "What it saves you" (~1 min): a day's worth of requests sent through the router off camera (quick questions, small
// coding tasks, hard debugging; Claude's session limit hits partway, so the rest spill over to a paid API), then the
// Usage page and the Overview's weekly digest. Every figure on screen is the router's own accounting of that traffic.
const fs = require('fs');
const path = require('path');

// What a coding agent sends along with each prompt: instructions and project context, about 12k tokens (within the
// local models' 32k context).
const docs = path.join(__dirname, '../../../docs');
const context = ['clients.md', 'providers.md'].map((f) => fs.readFileSync(path.join(docs, f), 'utf8')).join('\n\n').slice(0, 48000);

// Every prompt once: the router counts the same prompt sent again soon as a sign the first answer fell short.
const QUICK = ['hi! what can you do?', 'summarize this function in one sentence', 'translate this error message into plain English',
  'rename the variable total to subtotal', 'what does this pattern match?', 'format this JSON', 'thanks, that worked',
  'write a commit message for this change', 'what does HTTP 409 mean?', 'shorten this log message', 'is this date format ISO 8601?',
  'list the files this change touches', 'fix the spelling in this comment', 'what time zone does the server use?',
  'explain what this flag does', 'make this sentence friendlier', 'what is the default port?', 'summarize the last test run'];
const CODE = ['write a unit test for the InvoiceTotal function', 'write a regex that matches email addresses',
  'add null checks to ParseInvoice', 'convert this loop to LINQ', 'add XML docs to the public methods',
  'write a bash script that backs up the database', 'add a CancellationToken to SendInvoiceAsync', 'write a SQL query for unpaid invoices',
  'turn this class into a record', 'add retry with backoff to the HTTP client', 'write a test for the currency rounding',
  'extract this block into a method'];
const HARD = ['the invoice job deadlocks under load, find the root cause', 'design a scalable architecture for a multi-tenant SaaS platform',
  'why does the cache serve stale data after a deploy?', 'review this migration plan for data loss risks, step by step',
  'explain step by step how to shard this database', 'find the race condition in the ledger writer and prove the fix',
  'plan the split of the billing monolith into services', 'diagnose the memory growth in the export worker'];

const usd = (n) => (n < 0.01 ? `$${n.toFixed(4)}` : `$${n.toFixed(2)}`);

module.exports = async function savings(k) {
  const { sleep, router } = k;
  k.pointer = true;

  // Off camera: the router with a paid API, and Opus's API price as the yardstick for "saved".
  await k.startRouter((config) => {
    config.Connectors['paid-api'] = { Type: 'OpenAICompatible', BaseAddress: 'http://paid-api.example:4000', ApiKey: ['sk', 'demo', 'not', 'a', 'real', 'key'].join('-') };
    config.Models.push(
      { Name: 'paid-api-deepseek-chat-v3', Connector: 'paid-api', Model: 'deepseek/deepseek-chat-v3', Tier: 3, Capabilities: ['chat', 'code', 'tools'], InputCostPerMillionTokens: 0.27, OutputCostPerMillionTokens: 1.1, Enabled: true },
      { Name: 'claude-opus-api', Connector: 'paid-api', Model: 'claude-opus-5-5', Tier: 4, Capabilities: ['chat', 'code'], InputCostPerMillionTokens: 4, OutputCostPerMillionTokens: 20, Enabled: false,
        Description: "Opus through Anthropic's API: the yardstick for what routing saved" },
    );
    config.Usage.ReferenceModel = 'claude-opus-api';
  });

  // A day of requests, as an agent sends them. Most are quick or small; a few are hard.
  const ask = (prompt) => fetch(`${router}/v1/chat/completions`, {
    method: 'POST', headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ model: 'auto', messages: [{ role: 'system', content: context }, { role: 'user', content: prompt }] }),
  }).then((r) => r.text());
  // Mixed through the day, about three quick questions and two small tasks for each hard one.
  const day = [];
  for (let i = 0; i < 6; i++) day.push(...QUICK.slice(i * 3, i * 3 + 3), ...CODE.slice(i * 2, i * 2 + 2), HARD[i]);
  for (const prompt of day.slice(0, 24)) await ask(prompt);
  // Claude's session limit runs out mid-afternoon: the rest of the day's hard prompts go elsewhere.
  fs.writeFileSync(k.limitFile, '');
  for (const prompt of [...day.slice(24), HARD[6], HARD[7]]) await ask(prompt);

  const figures = async (by) => k.api('GET', `/usage?window=day&by=${by}`);
  const byLocation = await figures('location');
  const total = byLocation.totals;
  const local = byLocation.groups.find((g) => g.key === 'local')?.totals ?? { requests: 0 };
  const byModel = await figures('model');
  const paidRow = byModel.groups.find((g) => g.key === 'paid-api-deepseek-chat-v3')?.totals;

  await k.stage('layout', 'zoom');
  await k.go('usage');
  await k.record();

  // Title.
  let line = k.say('title');
  await k.stage('title', 'What it saves you', 'Savings', false);
  await line;
  await k.stage('hideCard');
  await sleep(400);

  // Local.
  line = k.say('local');
  await k.stage('caption', `${local.requests} of ${total.requests} requests answered on this machine`, 'local models · free · nothing left the machine');
  await k.dashClick('[data-action="usage-set"][data-key="window"][data-value="day"]');
  await k.dashClick('[data-action="usage-set"][data-key="by"][data-value="location"]');
  await k.frame().locator('td:text-is("local")').first().waitFor();
  await k.highlight('td:text-is("local") >> xpath=..');
  await line;
  await k.unhighlight();

  // Subscription value.
  line = k.say('subscription');
  await k.stage('caption', `${usd(total.subscriptionValueUsd)} of Claude use, at API prices`, 'included in your subscription · not spent');
  await k.highlight('.card.stat:has-text("subscription use")');
  await line;
  await k.unhighlight();

  // The real spend.
  line = k.say('paid');
  await k.stage('caption', `${usd(total.costUsd)} actually spent`, `paid-api-deepseek-chat-v3 · ${paidRow?.requests ?? 0} requests after Claude's session limit`);
  await k.highlight('.card.stat:has-text("spent, at catalog prices")');
  await sleep(2500);
  await k.unhighlight();
  await k.dashClick('[data-action="usage-set"][data-key="by"][data-value="model"]');
  await k.frame().locator('td:text-is("paid-api-deepseek-chat-v3")').first().waitFor();
  await k.highlight('td:text-is("paid-api-deepseek-chat-v3") >> xpath=..');
  await line;
  await k.unhighlight();

  // Saved, and the weekly total.
  line = k.say('saved');
  await k.stage('caption', `${usd(total.referenceCostUsd - total.costUsd)} saved`, 'against sending everything to Opus through its API · Usage:ReferenceModel');
  await k.highlight('.card.stat:has-text("saved compared with")');
  await sleep(3500);
  await k.nav('overview');
  await k.highlight('.digest');
  await line;
  await sleep(600);
  await k.unhighlight();

  // End.
  await k.stage('hideCaption');
  line = k.say('end');
  await k.stage('end');
  await line;
  await sleep(1500);
};

module.exports.paidApi = ['deepseek/deepseek-chat-v3', 'claude-opus-5-5'];
