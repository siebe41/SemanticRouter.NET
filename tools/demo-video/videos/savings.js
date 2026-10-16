// "What it saves you" (~1 min): a day of agentic coding sent through the router off camera, then the Usage page and
// the Overview's weekly digest. Every figure on screen is the router's own accounting of that traffic.
//
// The day is what Claude Code sends: five agent sessions, each one prompt that runs for dozens of steps (read a file,
// grep, edit, run the tests), every step resending the whole conversation, which grows from Claude Code's system prompt
// until Claude Code compacts it near the end of the 200k window; most of that is cache reads, as the stand-ins report
// it (prompt-cache.js), and each step writes a few thousand tokens of thinking and tool calls. In between, quick questions and small edits. Claude's
// session limit runs out after the third session, so the last two move to a paid API.
const fs = require('fs');
const path = require('path');

const repo = path.join(__dirname, '../../..');
// Claude Code's own instructions and tool definitions come to about 16k tokens on every request.
const docs = ['providers.md', 'dashboard.md'].map((f) => fs.readFileSync(path.join(repo, 'docs', f), 'utf8')).join('\n\n');
const SYSTEM = docs.slice(0, 64000);
const TOOLS = ['Read', 'Grep', 'Edit', 'Bash'].map((name) => ({ name, description: `Claude Code's ${name} tool.`, input_schema: { type: 'object', properties: {} } }));
// What the tools return: real source files from this repository, as a coding agent reads them.
const sources = [];
(function walk(dir) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (e.isDirectory() && !/^(bin|obj|node_modules|\.)/.test(e.name)) walk(path.join(dir, e.name));
    else if (e.name.endsWith('.cs')) sources.push(path.join(dir, e.name));
  }
})(path.join(repo, 'src'));
sources.sort();

// The sessions, each one prompt and how many steps it runs; the limit hits after the third.
const SESSIONS = [
  ['the invoice job deadlocks under load, find the root cause and fix it', 80],
  ['add idempotency keys to the ledger replay, with tests', 70],
  ['migrate InvoiceJob to the new event store API and update its callers', 90],
  ['why does the cache serve stale data after a deploy? fix it', 35],
  ['the FX client returns stale rates under load, find the root cause', 30],
];
// Between sessions: quick questions and small edits, each once (the router counts a prompt sent again soon as a sign
// the first answer fell short).
const QUICK = ['hi! what can you do?', 'summarize this function in one sentence', 'translate this error message into plain English',
  'format this JSON', 'write a commit message for this change', 'shorten this log message', 'fix the spelling in this comment',
  'make this sentence friendlier', 'summarize the last test run', 'write a unit test for the InvoiceTotal function',
  'write a regex that matches email addresses', 'add XML docs to the public methods', 'write a bash script that backs up the database',
  'write a SQL query for unpaid invoices', 'list the files this change touches', 'thanks, that worked', 'what does HTTP 409 mean?',
  'what is the default port?', 'convert this loop to LINQ', 'extract this block into a method'];

const usd = (n) => (n < 0.01 ? `$${n.toFixed(4)}` : `$${n.toFixed(2)}`);

module.exports = async function savings(k) {
  const { sleep, router } = k;
  k.pointer = true;

  // Off camera: the router with a paid API, and Opus's API price as the yardstick for "saved".
  await k.startRouter((config) => {
    config.Connectors['paid-api'] = { Type: 'OpenAICompatible', BaseAddress: 'http://paid-api.example:4000', ApiKey: ['sk', 'demo', 'not', 'a', 'real', 'key'].join('-') };
    config.Models.push(
      { Name: 'paid-api-deepseek-chat-v3', Connector: 'paid-api', Model: 'deepseek/deepseek-chat-v3', Tier: 3, Capabilities: ['chat', 'code', 'tools'], InputCostPerMillionTokens: 0.27, OutputCostPerMillionTokens: 1.1, CacheReadCostPerMillionTokens: 0.07, ContextWindow: 128000, Enabled: true },
      { Name: 'claude-opus-api', Connector: 'paid-api', Model: 'claude-opus-5-5', Tier: 4, Capabilities: ['chat', 'code'], InputCostPerMillionTokens: 4, OutputCostPerMillionTokens: 20, CacheReadCostPerMillionTokens: 0.2, CacheWriteCostPerMillionTokens: 5, Enabled: false,
        Description: "Opus through Anthropic's API: the yardstick for what routing saved" },
    );
    config.Usage.ReferenceModel = 'claude-opus-api';
  });

  // A day of agentic coding, as Claude Code sends it (the Anthropic Messages API).
  const send = (messages) => fetch(`${router}/v1/messages`, {
    method: 'POST', headers: { 'content-type': 'application/json', 'anthropic-version': '2023-06-01' },
    body: JSON.stringify({ model: 'semantic-router', max_tokens: 32000, system: SYSTEM, tools: TOOLS, messages }),
  }).then((r) => r.text());
  let seed = 7;
  const random = () => ((seed = (seed * 1103515245 + 12345) % 2147483648) / 2147483648);
  let call = 0;
  const step = () => {
    // A file read, a search or a test run: about 1,000 to 6,000 tokens of output.
    const file = sources[Math.floor(random() * sources.length)];
    let text = fs.readFileSync(file, 'utf8');
    while (text.length < 24000) text += `\n${fs.readFileSync(sources[Math.floor(random() * sources.length)], 'utf8')}`;
    const length = 4000 + Math.floor(random() * 20000);
    const start = Math.floor(random() * Math.max(1, text.length - length));
    const name = ['Read', 'Read', 'Grep', 'Edit', 'Bash'][Math.floor(random() * 5)];
    const id = `toolu_demo_${++call}`;
    return [
      { role: 'assistant', content: [{ type: 'text', text: `Checking ${path.relative(repo, file)}.` }, { type: 'tool_use', id, name, input: { file_path: file } }] },
      { role: 'user', content: [{ type: 'tool_result', tool_use_id: id, content: `${text.slice(start, start + length)}\n(end of tool output)` }] },
    ];
  };
  const quick = async (n) => { for (const prompt of QUICK.splice(0, n)) await send([{ role: 'user', content: prompt }]); };
  for (const [s, [prompt, steps]] of SESSIONS.entries()) {
    await quick(4);
    // Claude's session limit runs out after the third session: the rest of the day goes elsewhere.
    if (s === 3) fs.writeFileSync(k.limitFile, '');
    let messages = [{ role: 'user', content: prompt }];
    for (let i = 0; i < steps; i++) {
      await send(messages);
      messages.push(...step());
      // As the context fills, Claude Code compacts: the conversation so far becomes a summary (early enough here that the
      // afternoon's sessions fit the paid model's 128k window).
      if (JSON.stringify(messages).length / 4 > 110000) {
        await send([...messages, { role: 'user', content: 'Your task is to create a detailed summary of the conversation so far.' }]);
        messages = [{ role: 'user', content: prompt }, { role: 'assistant', content: `Summary of the work so far: ${'The investigation continues. '.repeat(1100)}` },
          { role: 'user', content: 'Continue from the summary.' }, ...step()];
      }
    }
    await send(messages);
  }
  await quick(QUICK.length);

  const figures = async (by) => k.api('GET', `/usage?window=day&by=${by}`);
  const byLocation = await figures('location');
  const total = byLocation.totals;
  const local = byLocation.groups.find((g) => g.key === 'local')?.totals ?? { requests: 0 };
  const byModel = await figures('model');
  const paidRow = byModel.groups.find((g) => g.key === 'paid-api-deepseek-chat-v3')?.totals;
  const onClaude = byModel.groups.filter((g) => g.key.startsWith('claude-')).reduce((n, g) => n + g.totals.requests, 0);
  console.log(JSON.stringify({ total, byModel: byModel.groups.map((g) => [g.key, g.totals.requests, g.totals.inputTokens, g.totals.outputTokens, g.totals.costUsd, g.totals.subscriptionValueUsd]) }));

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
  await k.stage('caption', `${local.requests} quick questions answered on this machine`, 'a local model · free · no Claude limits used');
  await k.dashClick('[data-action="usage-set"][data-key="window"][data-value="day"]');
  await k.dashClick('[data-action="usage-set"][data-key="by"][data-value="location"]');
  await k.frame().locator('td:text-is("local")').first().waitFor();
  await k.highlight('td:text-is("local") >> xpath=..');
  await line;
  await k.unhighlight();

  // Subscription value.
  line = k.say('subscription');
  await k.stage('caption', `${usd(total.subscriptionValueUsd)} of Claude use, at API prices`,
    `3 agent sessions · ${onClaude} requests · ${(total.totalTokens / 1e6).toFixed(0)}M tokens in the day · included in your subscription`);
  await k.highlight('.card.stat:has-text("subscription use")');
  await line;
  await k.unhighlight();

  // The real spend.
  line = k.say('paid');
  await k.stage('caption', `${usd(total.costUsd)} actually spent`, `2 agent sessions after Claude's session limit · paid-api-deepseek-chat-v3 · ${paidRow?.requests ?? 0} requests`);
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
  await k.stage('caption', `${usd(total.referenceCostUsd - total.costUsd)} saved`,
    `${usd(total.referenceCostUsd)} if all ${total.requests} requests went to Opus through its API − ${usd(total.costUsd)} spent · Usage:ReferenceModel`);
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
module.exports.fullAnswers = true;
