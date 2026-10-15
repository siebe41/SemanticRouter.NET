// Records one of the videos in videos/ with the kit (kit.js): real router, real dashboard, real Claude Code; only the
// models are stand-ins. The narration (tts.py, from narration/<video>.json) times every beat.
//
//   node record.js <video> <frames-dir> <voice-dir>      e.g. node record.js intro out/frames out/voice
//
// See kit.js for DEMO_HOME, DEMO_FEED and DEMO_REAL_CLAUDE.
const path = require('path');
const kit = require('./kit');

const [video = 'intro', out = 'out/frames', voice = 'out/voice'] = process.argv.slice(2);
const scenes = require(`./videos/${video}`);

(async () => {
  const k = await kit.start({ out: path.resolve(out), voiceDir: path.resolve(voice), shells: scenes.shells, loaded: scenes.loaded, paidApi: scenes.paidApi });
  try {
    await scenes(k);
  } finally {
    await k.finish();
  }
  process.exit(0);
})().catch((err) => { console.error(err); process.exit(1); });
