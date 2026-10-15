// The vertical short (~45 s, 9:16 for Shorts, Reels and LinkedIn), recorded on the vertical stage in one take: a hook
// and what the router is, then Claude Code full width with four turns routed (small local model, local coder, Claude,
// failover at the session limit), the decision under the window and captions that follow the voice word by word.
//
//   NARRATION=narration/short.json python tts.py chatterbox out/voice-short <voice sample>
//   NARRATION=narration/short.json python words.py out/voice-short
//   node record.js short out/frames-short out/voice-short
//   node build.js out/frames-short out/voice-short out/semantic-router-short.mp4 narration/short.json
const fs = require('fs');

module.exports = async function short(k) {
  const { router, sleep } = k;

  // Off camera: the router running, Claude Code started and pointed at it.
  await k.startRouter();
  k.run('claude', `export ANTHROPIC_BASE_URL=${router} ANTHROPIC_AUTH_TOKEN=semantic-router CLAUDE_CODE_MAX_CONTEXT_TOKENS=200000; clear; claude --model semantic-router`);
  await k.waitFor('claude', /Claude Code v/);
  await k.stage('layout', 'vterm');

  // The hook card is up before the first frame (it is the thumbnail): a small prompt, then the frontier model it
  // would go to, struck out for the local model that answers it for free.
  await k.stage('card', `
    <div class="tagline" style="font-size: 68px">Why pay frontier prices<br>for small prompts?</div>
    <div class="bubble fade"><span class="p">›</span> hi! what can you do?</div>
    <div class="down fade">↓</div>
    <div class="pick cloud fade"><b>Claude Opus</b><small>the most expensive model</small></div>
    <div class="pick local fade"><b>a small local model</b><small>free · just as good for this</small></div>`);
  await sleep(1200);

  await k.record();

  let line = k.say('hook');
  await k.cue('hook', 'ask'); await k.stage('reveal', 0);
  await k.cue('hook', 'doesn'); await k.stage('reveal', 1); await k.stage('reveal', 2);
  await k.cue('hook', 'so');
  await k.stage('mark', '.pick.cloud', 'struck');
  await k.stage('reveal', 3);
  await line;
  await sleep(500);

  // What it is: the name, then where prompts go, each as it is named.
  line = k.say('intro');
  await k.stage('card', `
    <div class="brand">{logo}<div class="name">SemanticRouter<span>.NET</span></div></div>
    <div class="tagline fade">Every prompt goes to the cheapest model that can answer it.</div>
    <div class="row">
      <div class="pill l fade"><b>Local models</b><small>LM Studio · Ollama · vLLM</small></div>
      <div class="pill s fade"><b>Your subscriptions</b><small>Claude Code · Copilot · Codex</small></div>
      <div class="pill a fade"><b>Paid APIs</b><small>OpenAI · Anthropic · Gemini</small></div>
    </div>`);
  await k.cue('intro', 'sends'); await k.stage('reveal', 0);
  await k.cue('intro', 'local'); await k.stage('reveal', 1);
  await k.cue('intro', 'subscription'); await k.stage('reveal', 2);
  await k.cue('intro', 'paid'); await k.stage('reveal', 3);
  await line;
  await sleep(900);
  await k.stage('hideCard');
  await sleep(500);

  // The decision, under the window: which model answered, and what that means.
  const chip = (e, note) => {
    if (!e) return '';
    const failed = (e.sentTo || []).filter((m) => m !== e.servedBy).map((m) => `<span class="fail">${m}</span> `).join('');
    return `<b>${failed}<span class="${k.isLocal(e.servedBy) ? 'local' : 'cloud'}">→ ${e.servedBy}</span></b><small>tier ${e.tier} · ${note}</small>`;
  };
  // Each prompt is typed while the last decision is still up, and sent as its line starts, so the voice never waits
  // for the typing and the decision lands while it is being talked about.
  const turn = async (key, headline, prompt, done, note, typeFirst = true) => {
    if (typeFirst) await k.type('claude', prompt, false, 1.6);
    line = k.say(key);
    const caption = k.stage('caption', headline, '');
    if (typeFirst) k.shells.claude.write('\r'); else await k.type('claude', prompt, true, 1.6);
    await k.waitFor('claude', done);
    await caption;
    await k.stage('sub', chip(await k.decision(prompt), note));
    await Promise.all([line, sleep(1000)]);
  };
  await turn('hello', 'A simple question', 'hi! what can you do?', /What would you like to work on/, 'local · free · stays on this machine', false);
  await turn('code', 'A quick coding task', 'write a unit test for the InvoiceTotal function', /Assert\.Equal/, 'the local coder · free');
  await turn('hard', 'A hard debugging question', 'the invoice job deadlocks under load, find the root cause', /three-file change/, 'your Claude subscription');

  // Claude's session limit runs out mid-conversation.
  fs.writeFileSync(k.limitFile, '');
  await turn('limit', 'Claude hits its session limit', 'why does the cache serve stale data after a deploy?', /Startup\.cs/, 'Claude cools down until it resets');
  await sleep(1200);

  // End.
  await k.stage('hideCaption');
  line = k.say('end');
  await k.stage('end');
  await line;
  await sleep(1500);
};

module.exports.vertical = true;
module.exports.shells = [['claude', 60, 22]];
// Only the small model is in memory at the start, so it answers the simple question; the coder loads for the coding task.
module.exports.loaded = ['qwen/qwen3-8b'];
