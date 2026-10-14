# Demo video

Records a one-minute marketing video of the router at work: the real `semantic-router` host and its real dashboard,
driven by scripted requests, with captions and title cards drawn over the page.

| Time | Scene |
|---|---|
| 0:00 | Title: one endpoint for local models, your subscriptions and paid APIs |
| 0:05 | Small talk → `lmstudio-small` (Open WebUI, chat completions) |
| 0:10 | Everyday code → `lmstudio-coder` (Codex, Responses API) |
| 0:15 | Hard reasoning → `claude-code` (Claude Code, Messages API) |
| 0:20 | Agentic coding with tools → `claude-code-opus` |
| 0:25 | Private data → a local-only route |
| 0:30 | Note text in a prompt: the dashboard shows which note went to which model |
| 0:38 | A burst of 12 prompts from three clients |
| 0:45 | Claude hits its session limit → cooldown and failover to the local coder |
| 0:51 | The memory map |
| 0:57 | End card: install command and repository |

Nothing in it is mocked inside the router. The backends are stand-ins: `serve.js` runs a stub LM Studio on
`localhost:1234` (or the router's port + 1 when LM Studio is running), and Claude Code is `fake-claude/claude` behind
the real CLI connector, which answers in Claude Code's `stream-json` format and, once `record.js` switches it,
with Claude Code's session-limit message. Routing, failover, cooldowns, note detection and the dashboard are the real
ones, on [demo-router.json](demo-router.json) and the notes in [vault](vault).

## Recording

Needs the .NET 10 SDK, Node 22, Playwright with Chromium, and ffmpeg with libx264. Linux or macOS (the fake `claude`
is a Node script with a shebang).

```bash
dotnet build src/SemanticRouter.Host --configuration Release
cd tools/demo-video
node serve.js &                      # the router on http://127.0.0.1:5290, fresh each time
node record.js /tmp/demo-frames      # about a minute; captures the dashboard frame by frame
./build.sh /tmp/demo-frames out/semantic-router-demo.mp4
```

`build.sh` writes a 1920×1080, 30 fps H.264 MP4 and a 960-wide GIF next to it. Restart `serve.js` before each
take: the dashboard should start empty and Claude should not be cooling down. `out/` is ignored by git.

To change the story, edit the scenes in [record.js](record.js) (each one is a headline, a client, a prompt and how
to describe the router's answer) and the title and end cards in [overlay.js](overlay.js).
