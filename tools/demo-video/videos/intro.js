// The intro (~1:50): install, init and start the router on camera, point Claude Code at it, watch each turn routed
// (local small model, local coder, Claude), a Claude session limit with failover, and the dashboard.
const fs = require('fs');

module.exports = async function intro(k) {
  const { router, sleep } = k;
  await k.record();

  // 1. Title.
  let line = k.say('title');
  await k.stage('title');
  await line;
  await k.stage('hideCard');
  await k.stage('layout', 'setup');
  await sleep(900);

  // 2. Setup, for real: install, init, start.
  line = k.say('install');
  await k.stage('caption', '1. Install the tool', 'one command · .NET 10');
  await k.type('router', 'dotnet tool install -g SemanticRouter.Host');
  await k.waitFor('router', /successfully installed/);
  await line;

  line = k.say('init');
  await k.stage('caption', '2. init finds your models', 'LM Studio · Ollama · the claude, codex and copilot CLIs · writes a starting configuration');
  await sleep(400);
  await k.type('router', 'semantic-router init');
  await k.waitFor('router', /Next: /);
  await line;

  line = k.say('start');
  await k.stage('caption', '3. Start the router', 'OpenAI and Anthropic APIs on localhost:5080 · dashboard on /router');
  await sleep(300);
  await k.type('router', 'semantic-router');
  await k.waitFor('router', /is listening on/);
  await k.stage('dash', `${router}/router/#/live`);
  await line;

  // 3. Claude Code, in a second terminal.
  await k.stage('layout', 'connect');
  await sleep(500);
  line = k.say('connect');
  await k.stage('caption', '4. Point Claude Code at it', 'the router\'s address · "semantic-router" as the model · Claude Code works as before');
  // The model name is what Claude Code shows in its header: "semantic-router" says who decides (any name the router
  // does not know routes as auto). The context window it cannot look up for that name is declared, so it does not warn.
  await k.type('claude', `export ANTHROPIC_BASE_URL=${router}`);
  await k.type('claude', 'export ANTHROPIC_AUTH_TOKEN=semantic-router');
  await k.type('claude', 'export CLAUDE_CODE_MAX_CONTEXT_TOKENS=200000');
  await k.type('claude', 'claude --model semantic-router');
  await k.waitFor('claude', /Claude Code v/);
  await line;
  await k.stage('layout', 'split');
  await sleep(1100);

  // 4. Routing, turn by turn.
  const turn = async (key, headline, prompt, done, hold = 1400) => {
    line = k.say(key);
    await k.stage('caption', headline, '');
    const e = await k.ask(prompt, done);
    await k.stage('sub', k.describe(e));
    await Promise.all([line, sleep(2600)]);
    await sleep(hold);
    return e;
  };
  await turn('hello', 'A simple question → a small local model', 'hi! what can you do?', /What would you like to work on/, 600);
  await turn('code', 'A small coding task → the local coder', 'write a unit test for the InvoiceTotal function', /Assert\.Equal/);
  await turn('hard', 'A hard debugging question → Claude', 'the invoice job deadlocks under load, find the root cause', /three-file change/);

  // 5. The Claude session limit runs out: cooldown and failover, inside the same Claude Code session.
  fs.writeFileSync(k.limitFile, '');
  line = k.say('limit');
  await k.stage('caption', 'Claude hits its session limit → you keep working', '');
  const e = await k.ask('why does the cache serve stale data after a deploy?', /Startup\.cs/);
  await k.stage('sub', `${k.describe(e)}<span class="tag">Claude cools down until it resets</span>`);
  await Promise.all([line, sleep(3000)]);
  await sleep(1200);

  // 6. The dashboard, with other clients on the same endpoint.
  await k.stage('hideCaption');
  await k.stage('layout', 'dash');
  line = k.say('dashboard');
  await sleep(900);
  await k.stage('caption', 'Every decision, live', 'any OpenAI or Anthropic client can use the same endpoint');
  for (const prompt of ['summarize this email in two lines', 'translate this sentence to French: the meeting moved to Friday',
    'write a regex that matches email addresses', 'thanks a lot!', 'rewrite this paragraph to sound more professional']) {
    k.send(prompt);
    await sleep(700);
  }
  await line;
  await sleep(600);

  // 7. End card.
  await k.stage('hideCaption');
  line = k.say('end');
  await k.stage('end');
  await line;
  await sleep(1800);
};

// Only the small model is in memory at the start, so it answers the simple question; the coder loads for the first
// coding task (with both loaded, the router would send everything local to the more capable coder).
module.exports.loaded = ['qwen/qwen3-8b'];
