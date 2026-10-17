// Privacy (~1:30): what leaves your machine. A vault of notes on the Memory page; Claude Code reads a note and a local
// model answers (the Live page shows the note stayed local); Opus is asked to review it and the note goes to the cloud
// (the map marks it); a credential that slips into a prompt (not recommended) keeps the conversation local, even with
// @opus; Activity records all of it.
const fs = require('fs');
const path = require('path');
const { writeNotes } = require('../notes');

// An obviously fake GitHub-style token, put together here so the repository holds no token-shaped string.
const fakeKey = ['ghp', 'D3mo0notArealT0ken0F0rTheVide0xyz123'].join('_');

module.exports = async function privacy(k) {
  const { router, sleep } = k;
  k.pointer = true;

  // Off camera: the notes, the router reading them, secret detection on, Claude Code started with the notes folder.
  writeNotes(path.join(k.home, 'notes'));
  // A memory source is a folder the router reads, which the admin API changes only when the router allows it.
  k.env.SemanticRouter__Dashboard__AllowProcessSettings = 'true';
  await k.startRouter((config) => {
    config.Memory = config.Memory || {};
    config.Memory.Sources = [{ Name: 'notes', Type: 'Markdown', Path: '~/notes' }];
    config.Middleware.LocalOnlyWhen.Secrets = true;
  });
  k.run('claude', `export ANTHROPIC_BASE_URL=${router} ANTHROPIC_AUTH_TOKEN=semantic-router CLAUDE_CODE_MAX_CONTEXT_TOKENS=200000 ANTHROPIC_CUSTOM_HEADERS='X-Router-Project: billing'; clear; claude --model semantic-router --add-dir ~/notes`);
  await k.waitFor('claude', /Claude Code v/);
  await k.stage('layout', 'zoom');
  await k.go('memory');
  await k.frame().locator('text=11 notes').first().waitFor({ timeout: 30000 });

  await k.record();

  // Title.
  let line = k.say('title');
  await k.stage('title', 'What leaves your machine', 'Privacy', false);
  await line;
  await k.stage('hideCard');
  await sleep(400);

  // The map.
  line = k.say('map');
  await k.stage('caption', 'Your notes, mapped', 'Memory page · an Obsidian vault · 11 notes, 22 links');
  await k.highlight('.memory-layout');
  await line;
  await k.unhighlight();

  // A local model reads a note.
  await k.stage('hideCaption');
  await k.stage('layout', 'split');
  await k.go('live');
  line = k.say('local');
  await k.stage('caption', 'Claude Code reads a note', 'a local model answers · the note stays on this machine');
  let e = await k.ask('summarize my billing migration note', /cutover is the weekend/);
  await k.stage('sub', k.describe(e));
  await k.highlight('.card:has(h2:has-text("Memory in requests"))');
  await line;
  await k.unhighlight();

  // Opus reviews it: the note goes with the conversation.
  line = k.say('cloud');
  await k.stage('caption', 'Ask Opus to review it', 'the note goes along with the conversation → the cloud');
  e = await k.ask('@opus review that plan for risks we missed', /dual-writing/);
  await k.stage('sub', k.describe(e));
  await k.highlight('.card:has(h2:has-text("Memory in requests"))');
  await sleep(2500);
  await k.unhighlight();
  await k.stage('layout', 'zoom');
  await k.go('memory');
  await k.highlight('.memory-layout');
  await line;
  await k.unhighlight();

  // A credential keeps it local.
  await k.stage('hideCaption');
  await k.stage('layout', 'split');
  await k.go('live');
  line = k.say('secret');
  await k.stage('caption', "Don't put secrets in prompts. If one slips in, it stays local", 'secret detection on (Middleware:LocalOnlyWhen:Secrets) · even after @opus');
  e = await k.ask(`@opus why does this call fail? curl -H "Authorization: Bearer ${fakeKey}" https://api.billing.example/v1/invoices`, /rotate this one/);
  await k.stage('sub', `${k.describe(e)}<span class="tag local">local only · secret detected</span>`);
  await Promise.all([line, sleep(2500)]);

  // The record.
  await k.stage('hideCaption');
  await k.stage('layout', 'zoom');
  await k.go('activity');
  line = k.say('audit');
  await k.stage('caption', 'Every request, on the record', 'Activity · which notes each request carried · what a credential kept local');
  await k.highlight('tr:has(.badge:has-text("→ cloud"))');
  await sleep(3500);
  await k.highlight('tr:has(.badge:has-text("local: secret"))');
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
