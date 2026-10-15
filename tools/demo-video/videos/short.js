// The vertical short (~45 s, 9:16 for Shorts, Reels and LinkedIn), recorded on the vertical stage in one take: Claude
// Code full width, four turns routed (small local model, local coder, Claude, failover at the session limit), with the
// decision under the window and captions that follow the voice word by word.
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
  await sleep(1200);

  await k.record();

  // The hook.
  let line = k.say('hook');
  await k.stage('title', 'Stop paying frontier prices<br>for small prompts.');
  await line;
  await k.stage('hideCard');
  await sleep(500);

  // The decision, under the window: which model answered, and what that means.
  const chip = (e, note) => {
    if (!e) return '';
    const failed = (e.sentTo || []).filter((m) => m !== e.servedBy).map((m) => `<span class="fail">${m}</span> `).join('');
    return `<b>${failed}<span class="${k.isLocal(e.servedBy) ? 'local' : 'cloud'}">→ ${e.servedBy}</span></b><small>tier ${e.tier} · ${note}</small>`;
  };
  const turn = async (key, headline, prompt, done, note, hold = 900) => {
    line = k.say(key);
    await k.stage('caption', headline, '');
    const e = await k.ask(prompt, done);
    await k.stage('sub', chip(e, note));
    await Promise.all([line, sleep(2200)]);
    await sleep(hold);
  };
  await turn('hello', 'A simple question', 'hi! what can you do?', /What would you like to work on/, 'local · free · stays on this machine', 300);
  await turn('code', 'A quick coding task', 'write a unit test for the InvoiceTotal function', /Assert\.Equal/, 'the local coder · free', 300);
  await turn('hard', 'A hard debugging question', 'the invoice job deadlocks under load, find the root cause', /three-file change/, 'your Claude subscription');

  // Claude's session limit runs out mid-conversation.
  fs.writeFileSync(k.limitFile, '');
  await turn('limit', 'Claude hits its session limit', 'why does the cache serve stale data after a deploy?', /Startup\.cs/, 'Claude cools down until it resets', 1000);

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
