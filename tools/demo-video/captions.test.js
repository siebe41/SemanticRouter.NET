// node --test (npm test): the subtitles build.js writes.
const test = require('node:test');
const assert = require('node:assert/strict');
const { cues, srt, written } = require('./captions');

const lines = { title: 'Install the dot net tool. Then run in-it: it finds L M Studio.' };
const timeline = [{ key: 'title', start: 10, duration: 6 }];

test('a line is shown as written, not as the voice says it', () => {
  assert.equal(written(lines.title), 'Install the .NET tool. Then run init: it finds LM Studio.');
  assert.equal(written('Semantic Router dot net.'), 'SemanticRouter.NET.');
});

test('without measured words, each sentence gets its share of the line by characters', () => {
  const list = cues(timeline, lines);
  assert.deepEqual(list.map(([, , text]) => text), ['Install the .NET tool.', 'Then run init:', 'it finds LM Studio.']);
  // 22, 14 and 19 characters of 55, over 6 seconds from 10 s.
  assert.equal(list[0][0], 10);
  assert.ok(Math.abs(list[0][1] - (10 + 6 * 22 / 55)) < 1e-9);
  assert.ok(Math.abs(list[2][1] - 16) < 1e-9);
});

test('measured words time each sentence from its first word to the next sentence', () => {
  const words = {
    title: [['Install', 0.2, 0.5], ['the', 0.5, 0.6], ['dot', 0.6, 0.8], ['net', 0.8, 1], ['tool.', 1, 1.4],
      ['Then', 2, 2.2], ['run', 2.2, 2.4], ['in-it:', 2.4, 2.9],
      ['it', 3.1, 3.2], ['finds', 3.2, 3.5], ['L', 3.5, 3.6], ['M', 3.6, 3.7], ['Studio.', 3.7, 4.3]],
  };
  assert.deepEqual(cues(timeline, lines, words), [
    [10.2, 12, 'Install the .NET tool.'],
    [12, 13.1, 'Then run init:'],
    [13.1, 14.3, 'it finds LM Studio.'],
  ]);
});

test('measured words for text that has since changed are not used', () => {
  const words = { title: [['Install', 0.2, 0.5], ['it.', 0.5, 1]] };
  assert.deepEqual(cues(timeline, lines, words), cues(timeline, lines));
});

test('cues become an .srt file', () => {
  assert.equal(srt([[0.2414, 2.2, 'One.'], [62.2, 3725.5, 'Two.']]),
    '1\n00:00:00,241 --> 00:00:02,200\nOne.\n\n2\n00:01:02,200 --> 01:02:05,500\nTwo.\n');
});
