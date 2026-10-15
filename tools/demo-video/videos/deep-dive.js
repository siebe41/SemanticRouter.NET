// The deep dive (~5 min): how a prompt gets routed. The router is installed and started off camera with the `init`
// configuration plus secret detection; the video tours the dashboard (routes, the prompt tester, complexity, tiers,
// strategies), then Claude Code (a tool round trip, a callout, a credential kept local), failover, and how decisions
// explain themselves (Activity, Evaluate).
//
// Not shown until siebe41/SemanticRouter.NET#130 is fixed: the conversation floor ("ok, go ahead" keeping a task's
// tier), which doesn't carry over between Claude Code turns yet.
const fs = require('fs');

// An obviously fake GitHub-style token (short enough to fit the Activity table), put together here so the repository
// holds no token-shaped string.
const fakeKey = ['ghp', 'D3mo0notArealT0ken0F0rTheVide0xyz123'].join('_');

const REGEX = 'write a regex that matches email addresses';
const REGEX_REASONED = 'write a regex that matches email addresses.\n1. explain step by step why each part is needed\n'
  + '2. compare the trade-offs against a stricter pattern\n3. prove it rejects quoted local parts\n4. analyze the root cause of false positives';

module.exports = async function deepDive(k) {
  const { router, sleep } = k;

  // Off camera: install, init, start; secret detection on; Claude Code started in its terminal.
  await k.startRouter((config) => { config.Middleware.LocalOnlyWhen.Secrets = true; });
  // X-Router-Project labels the project in Activity (without it, Claude Code's metadata blob shows: #124).
  k.run('claude', `export ANTHROPIC_BASE_URL=${router} ANTHROPIC_AUTH_TOKEN=semantic-router CLAUDE_CODE_MAX_CONTEXT_TOKENS=200000 ANTHROPIC_CUSTOM_HEADERS='X-Router-Project: billing'; clear; claude --model semantic-router`);
  await k.waitFor('claude', /Claude Code v/);
  await k.stage('layout', 'dash');
  await k.go('routes');

  // The tester: type a prompt, pick a strategy, classify, wait for the result.
  const classify = async (prompt, model = 'auto') => {
    await k.dashScroll('.tester', 'start');
    await k.dashSelect('#tester-model', model);
    await k.dashType('#tester-prompt', prompt);
    await sleep(300);
    await k.dashClick('[data-action="tester-classify"]');
    await k.frame().locator('.tester .result-line').first().waitFor();
    await sleep(300);
    await k.dashScroll('.tester', 'end');
  };

  await k.record();

  // Title and the two questions.
  let line = k.say('title');
  await k.stage('title', 'How a prompt gets routed', 'Deep dive', false);
  await line;
  line = k.say('two-questions');
  await k.stage('diagram');
  await line;
  await sleep(600);

  // 1. What does the prompt need?
  await k.stage('chapter', 1, 'What does the prompt need?', 'routes · similarity · complexity');
  await sleep(2200);
  await k.stage('hideCard');
  await sleep(500);

  line = k.say('routes');
  await k.stage('caption', 'Routes: example prompts, each with the tier it needs', 'Routes page · 8 routes from init');
  await sleep(1500);
  await k.highlight('text=complex-code');
  await sleep(1200);
  await k.dashClick('text=complex-code');
  await sleep(600);
  await k.unhighlight();
  await line;

  await k.go('overview');
  line = k.say('tester');
  await k.stage('caption', 'Try a prompt: classify it without calling a model', 'Overview → Try a prompt');
  await classify('find the memory leak in the cache service');
  await k.highlight('.tester .result-line');
  await sleep(2600);
  await k.highlight('.tester >> text=signals: >> xpath=..');
  await line;
  await k.unhighlight();

  line = k.say('simple');
  await k.stage('caption', 'A small coding task → tier 2', 'simple-code · the local coder ranks first');
  await classify(REGEX);
  await k.highlight('.tester .result-line');
  await line;
  await sleep(400);

  line = k.say('complex');
  await k.stage('caption', 'Same ask, more to reason through → complexity raises the tier', 'length · code · structure · reasoning words → tier 3 at ≥ 0.65');
  await k.unhighlight();
  await classify(REGEX_REASONED);
  await k.highlight('.tester .result-line');
  await line;
  await sleep(600);
  await k.unhighlight();

  // 2. Who should answer?
  await k.stage('hideCaption');
  await k.stage('chapter', 2, 'Who should answer?', 'tiers · strategy');
  await sleep(2200);
  await k.go('tiers');
  await k.stage('hideCard');
  line = k.say('tiers');
  await k.stage('caption', 'Every model sits on a tier', 'Tiers page · who needs what, who serves what');
  await k.dashScroll('text=Who needs what', 'start');
  await k.highlight('text=nothing');
  await line;
  await k.unhighlight();

  await k.go('overview');
  line = k.say('strategy');
  await k.stage('caption', 'A strategy ranks the models that qualify', 'cheapest capable (default) · auto:best · auto:fastest · auto:priority');
  await classify(REGEX);
  await k.highlight('.tester >> text=Models, in order >> xpath=..');
  await sleep(4200);
  await k.stage('sub', '<span class="cloud">auto:best</span> · the most capable model first');
  await classify(REGEX, 'auto:best');
  await k.highlight('.tester >> text=Models, in order >> xpath=..');
  await line;
  await sleep(600);
  await k.unhighlight();

  // 3. Coding agents.
  await k.stage('hideCaption');
  await k.stage('chapter', 3, 'Coding agents', 'tools · tool results · callouts');
  await k.go('live');
  await k.stage('layout', 'split');
  await sleep(2000);
  await k.stage('hideCard');

  line = k.say('agents');
  await k.stage('caption', 'Every Claude Code turn carries tools', 'only tool-capable models are candidates');
  await line;

  line = k.say('toolloop');
  await k.stage('caption', 'A tool round trip stays on one model', '');
  const asked = await k.ask('what does this project do? read the README', /billing service/);
  const loop = (await k.activity()).find((e) => e.toolContinuation && e.servedBy);
  await k.stage('sub', `${k.describe(asked)}  ·  Read → tool result → <span class="local">${loop ? loop.servedBy : asked?.servedBy}</span> again (ConversationAffinity)`);
  await Promise.all([line, sleep(2500)]);

  line = k.say('callout');
  await k.stage('caption', '@opus, @local, @tier2: you decide', '');
  let e = await k.ask(`@opus ${REGEX}`, /RFC 5322/);
  await k.stage('sub', `${k.describe(e)}<span class="tag">@opus</span>`);
  await Promise.all([line, sleep(2500)]);

  // 4. Privacy and failure.
  await k.stage('hideCaption');
  await k.stage('chapter', 4, 'Privacy and failure', 'secret detection · cooldowns · failover');
  await sleep(2200);
  await k.stage('hideCard');
  line = k.say('privacy');
  await k.stage('caption', 'A credential in the conversation → it stays local', 'Middleware:LocalOnlyWhen:Secrets · even after @opus');
  e = await k.ask(`why does this call fail? curl -H "Authorization: Bearer ${fakeKey}" https://api.billing.example/v1/invoices`, /rotate this one/);
  await k.stage('sub', `${k.describe(e)}<span class="tag local">local only · secret detected</span>`);
  await Promise.all([line, sleep(2500)]);

  // Claude's session limit runs out; an OpenAI client's hard prompt fails over, and Endpoints shows the cooldown.
  await k.stage('hideCaption');
  fs.writeFileSync(k.limitFile, '');
  await k.send('design a scalable architecture for a multi-tenant SaaS platform');
  await k.stage('layout', 'dash');
  await k.go('overview');
  line = k.say('failover');
  await k.stage('caption', 'Out of quota → cool down → the next model takes over', 'Overview → Endpoints');
  await sleep(1200);
  await k.dashScroll('h2:has-text("Endpoints")', 'start');
  await k.highlight('.card:has(h2:has-text("Endpoints")) >> text=claude-cli >> xpath=ancestor::tr');
  await line;
  await sleep(800);
  await k.unhighlight();

  // 5. Seeing why.
  await k.stage('hideCaption');
  await k.stage('chapter', 5, 'Seeing why', 'activity · evaluate');
  await sleep(2200);
  await k.go('activity');
  await k.dashSelect('#activity-filter', 'degraded');
  await k.stage('hideCard');
  line = k.say('activity');
  await k.stage('caption', 'Every decision explains itself', 'route · score · tier · every model it tried');
  await sleep(1200);
  await k.highlight('text=degraded >> xpath=ancestor::tr');
  await line;
  await k.unhighlight();

  await k.go('evaluate');
  line = k.say('evaluate');
  await k.stage('caption', 'Evaluate: how often is the router right?', 'a labelled set of prompts · no model is called');
  await k.dashClick('[data-action="eval-run"]:not([data-calibrate])');
  await k.frame().locator('.card.stat').first().waitFor();
  await sleep(600);
  await k.dashScroll('.card.stat', 'center');
  await k.highlight('.card.stat');
  await line;
  await sleep(600);
  await k.unhighlight();

  // End.
  await k.stage('hideCaption');
  line = k.say('end');
  await k.stage('end', 'Next: tuning it for your own models');
  await line;
  await sleep(1800);
};
