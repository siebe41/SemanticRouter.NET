# Demo videos

Records narrated videos of SemanticRouter.NET from scripts, so they can be re-recorded whenever the product changes.

| Video | Script | Narration | Length |
|---|---|---|---|
| **Intro**: install, `init`, start, Claude Code routed turn by turn, failover, the dashboard | [videos/intro.js](videos/intro.js) | [narration/intro.json](narration/intro.json) | ~1:50 |
| **Deep dive**: how a prompt gets routed (routes, the prompt tester, complexity, tiers, strategies, a tool round trip, callouts, secret detection, failover, Activity, Evaluate) | [videos/deep-dive.js](videos/deep-dive.js) | [narration/deep-dive.json](narration/deep-dive.json) | ~3:05 |
| **How-to: the settings**: where they live, a model LM Studio just downloaded, a paid API and its prices, fixing a misroute with Evaluate, a local-only profile, a budget, `doctor` | [videos/settings.js](videos/settings.js) | [narration/settings.json](narration/settings.json) | ~2:10 |
| **What it saves you**: a day of traffic (local, a Claude subscription, a paid API after the session limit), then Usage and the weekly digest; every figure is the router's own | [videos/savings.js](videos/savings.js) | [narration/savings.json](narration/savings.json) | ~0:45 |
| **Privacy**: the Memory map of a notes vault, a note read by a local model and then sent to Opus, a credential keeping a conversation local, Activity as the record | [videos/privacy.js](videos/privacy.js) (vault: [notes.js](notes.js)) | [narration/privacy.json](narration/privacy.json) | ~1:00 |

| **Short** (vertical, 1080×1920, for Shorts, Reels and LinkedIn): Claude Code full width, four turns routed and a failover in one take, the decision under the window, captions that follow the voice word by word | [videos/short.js](videos/short.js) | [narration/short.json](narration/short.json) | ~0:45 |

The short records on the vertical stage (`stage.html?vertical`, chosen by `module.exports.vertical` in its script).
Its captions are timed to the voice: after `tts.py`, run
`NARRATION=narration/short.json python words.py out/voice-short` (speech-to-text, faster-whisper) to write the times
of every word into the voice folder.

[kit.js](kit.js) holds what they share (the demo user, the stand-in models, the shells, the stage, the dashboard
helpers, the narration clock), [stage.html](stage.html) is what the camera sees, and [PLAN.md](PLAN.md) lists the
videos still to make.

### The intro

| Time | Narration | On screen |
|---|---|---|
| 0:00 | Most prompts don't need a frontier model… | Title card |
| 0:13 | Setup takes about a minute… | `dotnet tool install -g SemanticRouter.Host` |
| 0:17 | Then run init… | `semantic-router init` finds LM Studio's models and the Claude Code CLI |
| 0:30 | Start the router… | `semantic-router` and its startup banner |
| 0:37 | Point Claude Code at the router… | a second terminal: `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN`, `claude --model semantic-router` |
| 0:49 | A simple question… | Claude Code on the left, the Live dashboard on the right; → local small model |
| 0:55 | A small coding task… | → local coder model |
| 1:03 | A hard debugging question… | → Claude Opus |
| 1:14 | When you hit your Claude session limit… | Claude cools down, the turn fails over to the local coder |
| 1:23 | The dashboard shows every decision… | the dashboard full screen, with OpenAI clients on the same endpoint |
| 1:35 | Open source, MIT licensed… | End card with the three commands |

## What is real

Everything except the models. The terminals are real shells in pseudo-terminals, shown with xterm.js; the tool is
installed with `dotnet tool install`; the configuration is the one `semantic-router init` writes; Claude Code is the
real CLI, pointed at the router the way [docs/clients.md](../../docs/clients.md) says; routing, cooldowns, failover
and the dashboard are the router's own.

The models are stand-ins, so a recording needs no GPU and spends no subscription:

- [lmstudio-stub.js](lmstudio-stub.js) answers as LM Studio on `localhost:1234`, with `qwen/qwen3-8b` and
  `qwen/qwen3-coder-30b`.
- [fake-claude/claude](fake-claude/claude) is on the demo user's `PATH`. The router's Claude Code CLI calls (print
  mode, no session persistence) get answers in Claude Code's `stream-json` format, or its session-limit message once
  `record.js` switches it. Everything else, such as you typing `claude`, runs the real Claude Code.
- Both answer from [replies.js](replies.js), so an answer reads the same whichever model the router picks.

Captions show what the router decided, read from its activity log during the take, so they say what really
happened.

## Recording

Needs Linux, the .NET 10 SDK, Node 22, Chromium for Playwright, ffmpeg with libx264, Claude Code, and Python for the
narration. The narration uses [Chatterbox](https://github.com/resemble-ai/chatterbox), an open (MIT) TTS model that
runs locally and can speak in your own voice from a 10–30 second sample. [Kokoro](https://github.com/thewh1teagle/kokoro-onnx)
is the lighter alternative.

```bash
cd tools/demo-video
npm install

# 1. Narration (once per video, or after editing its narration file): writes out/voice/*.wav and durations.json.
#    About a minute per line on a 4-core CPU. Add a voice sample to speak in that voice: … out/voice me.wav
pip install chatterbox-tts
NARRATION=narration/intro.json python tts.py chatterbox out/voice
#    or, faster and flatter: pip install kokoro-onnx soundfile, download kokoro-v1.0.onnx and voices-v1.0.bin from
#    github.com/thewh1teagle/kokoro-onnx/releases/tag/model-files-v1.0, then
#    python tts.py kokoro out/voice kokoro-v1.0.onnx voices-v1.0.bin

# 2. The package to install on camera. Without DEMO_FEED the take installs the release on nuget.org.
dotnet pack ../../src/SemanticRouter.Host -c Release -o out/feed -p:Version=0.3.0

# 3. Record (as long as the video), then encode with the narration mixed in.
DEMO_FEED=out/feed node record.js intro out/frames out/voice
node build.js out/frames out/voice out/semantic-router-demo.mp4 narration/intro.json
```

`build.js` writes the MP4 (1920×1080, 30 fps, H.264 and AAC) and an `.srt` with the narration as subtitles.

`record.js` uses ports 1234 and 5080 and a throw-away home directory for the demo user, `DEMO_HOME` (default
`/home/demo`, deleted at the start of every take), so the terminals show `~/billing` and nothing of the recording
machine. The demo shells get a minimal environment: none of your variables or credentials. `DEMO_REAL_CLAUDE` names
the Claude Code binary if `claude` on your `PATH` is not it.

For the deep dive, use `deep-dive` and `narration/deep-dive.json` in the same commands. It installs and starts the
router off camera (with secret detection turned on through the admin API), so it starts on the title card.

For the settings how-to, use `settings` and `narration/settings.json`. It clicks and types on the dashboard with a
pointer on screen, and adds a stand-in paid API, served as `paid-api.example:4000` (a reserved domain, so it is never
mistaken for a real provider, and not this machine's name, so the router treats it as a cloud API). That name needs
`127.0.0.1 paid-api.example` in `/etc/hosts`; the kit adds the line when it may write the file.

To change the story, edit the lines in the video's narration file (then run `tts.py` again; every beat waits for its
line, and `TTS_ONLY=hello,code` redoes just those lines), the prompts and captions in its script under `videos/`,
and the answers in [replies.js](replies.js). Narration reads best written the way people talk: short sentences,
contractions, a question now and then.

The voice sample for cloning: 15–30 seconds of you talking, one take, a quiet room, a headset or phone mic at normal
distance, as if explaining the router to a colleague. WAV or M4A, no music or other voices. Chatterbox uses about
10 seconds of it, so cut the stretch whose tone fits the video and keep it under `out/` (ignored by git; a voice
sample doesn't belong in the repository):

```bash
ffmpeg -i recording.m4a -ss 11 -to 26.5 -ac 1 -ar 24000 -af "highpass=f=70,loudnorm=I=-20:TP=-2" out/voice-sample/me.wav
python tts.py chatterbox out/voice out/voice-sample/me.wav
```

Generated speech occasionally drops or repeats words. Check every line before recording, and regenerate the ones it
flags (`TTS_ONLY=init,limit python tts.py …`):

```bash
pip install faster-whisper
NARRATION=narration/intro.json python check_narration.py out/voice
```

[PLAN.md](PLAN.md) outlines the next videos: a deep dive on routing and a how-to on the settings.

### Claude Code's header

`claude --model semantic-router` makes Claude Code's header say who picks the model (any name the router doesn't know
routes as `auto`), and `CLAUDE_CODE_MAX_CONTEXT_TOKENS` tells it the context window it can't look up for that name,
which otherwise produces an "isn't recognized" warning. #128 proposes doing this properly.
