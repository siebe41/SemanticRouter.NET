// node --test (npm test): which frame stills.js takes for each line, and the placeholder voice's timing.
const test = require('node:test');
const assert = require('node:assert/strict');
const { frameTimes, pick } = require('./stills');
const { seconds, evenWords, silence } = require('./placeholder-voice');

const framesText = "file '00000.jpg'\nduration 1.5000\nfile '00001.jpg'\nduration 2.0000\nfile '00002.jpg'\nduration 1.0000\nfile '00002.jpg'\n";

test('frames start where the one before them ends', () => {
  assert.deepEqual(frameTimes(framesText), [{ file: '00000.jpg', start: 0 }, { file: '00001.jpg', start: 1.5 }, { file: '00002.jpg', start: 3.5 }]);
});

test('a still is the frame on screen just before its beat ends: the next line, or its own end', () => {
  const frames = frameTimes(framesText);
  const stills = pick(frames, [{ key: 'a', start: 0, duration: 1 }, { key: 'b', start: 1.7, duration: 0.5 }, { key: 'c', start: 3.6, duration: 0.2 }]);
  assert.deepEqual(stills.map((s) => [s.file, s.at]), [['00000.jpg', 1.4], ['00001.jpg', 3.3], ['00002.jpg', 3.6]]);
});

test('a placeholder line lasts as long as its words take to say', () => {
  assert.equal(seconds('one two three four five six seven eight nine ten eleven twelve thirteen.'), 5.25);
  const words = evenWords('a b c d', 2);
  assert.deepEqual(words.at(-1), ['d', 1.5, 2]);
  assert.equal(silence(1).length, 44 + 24000 * 2);
});
