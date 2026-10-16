# Proof of concept: LM Studio + Claude Code CLI

A router on `http://localhost:5090` that answers OpenAI-style chat requests:

| Prompt | Goes to |
|---|---|
| easy (chit-chat, summarize, rewrite, translate, quick facts) | **LM Studio** (free, local) |
| hard (design, debugging, analysis, step-by-step reasoning), or anything complex | **Claude Code CLI**, on your own Claude subscription |
| private (customer records, salaries, medical history) | **LM Studio only**. If LM Studio is down, the request fails rather than going to Claude. |

If Claude is unavailable (not signed in, or out of its session limit), hard prompts fall back to LM Studio and are flagged as *degraded*. If LM Studio is down, easy prompts go to Claude instead.

## Prerequisites

1. **.NET 10 SDK.** Check with `dotnet --version`.
2. **LM Studio** with a chat model loaded and the local server started (Developer tab, or `lms server start`). The default port is `1234`.
3. **Claude Code**, installed and signed in:
   ```bash
   claude          # then /login with your Claude account, then exit
   claude auth status
   ```
   On Windows, use the native installer, so that `claude.exe` is on `PATH`. The router never runs commands through `cmd.exe`, so an npm `claude.cmd` shim won't work; the preflight will tell you if that's what it found.

## Run the self-test

From the repository root:

```bash
dotnet run --project samples/LmStudioClaudePoc -- --demo
```

The demo does the following:

1. **Preflight.** No Claude usage. It:
   - finds LM Studio and picks its first loaded chat model, unless you configured one;
   - finds `claude`;
   - checks that it is signed in, and with what (subscription or API key).
2. **Calls each backend directly** with "Reply with exactly one word: pong". A broken Claude setup shows its real error here instead of silently falling back.
3. **Sends five prompts with `"model": "auto"`** and shows which backend answered each one.
4. **Streams a reply from Claude** and reports the time to the first token.

It exits with code `0` when everything works. It uses about **four short Claude requests** from your subscription.

The output looks like this; the replies and timings will be your models':

```text
Preflight
---------
[ OK ] LM Studio: http://localhost:1234, using 'qwen/qwen3-8b' (auto-selected from: qwen/qwen3-8b)
[ OK ] Claude CLI: 2.1.300 (Claude Code) at /usr/local/bin/claude, signed in with your Claude subscription

1. Each backend, called directly ("model": "lmstudio" / "claude")
[ OK ] lmstudio    0.4s  pong
[ OK ] claude      6.1s  pong

2. Automatic routing ("model": "auto")
kind     tier reason                answered by try   time  reply
easy     1    SemanticMatch         lmstudio    1     1.2s  I'm doing well, thanks for asking! …
easy     1    SemanticMatch         lmstudio    1     0.9s  The client meeting was moved to Friday …
hard     3    SemanticMatch         claude      1     9.8s  Event sourcing gives you a full audit …
hard     3    SemanticMatch         claude      1    11.2s  Calling .Result blocks the thread that …
private  1    SemanticMatch         lmstudio    1     0.7s  I don't have access to customer records …

3. Streaming from Claude ("stream": true)
[ OK ] first token after 4.9s, 23 chunks in 5.6s: 1 2 3 4 5 6 7 8 9 10

Proof of concept OK. Point any OpenAI-compatible client at http://localhost:5090/v1 with model "auto".
```

Claude is slower to answer than LM Studio, because every request starts the `claude` process.

## Use it

```bash
dotnet run --project samples/LmStudioClaudePoc
```

The dashboard is at <http://localhost:5090/router>: live activity, the models' state, and every setting editable with hot reload (saved to `semantic-router.json` next to the app).

### From Claude Code

Keep using Claude Code exactly as before; just point it here:

```bash
ANTHROPIC_BASE_URL=http://localhost:5090 ANTHROPIC_AUTH_TOKEN=semantic-router claude
```

Every turn is routed on your latest message: easy and private ones go to LM Studio, hard ones to Claude. The `claude-cli` connector has `ToolCalling: Prompted`, so Claude Code's tool-bearing turns can land on your subscription too: the router describes Claude Code's tools to the inner `claude -p` run, parses the tool call it answers with, and hands it back to your Claude Code, which runs the tool. If your LM Studio model supports function calling (e.g. `qwen3-coder`), add `"tools"` to the `lmstudio` model's capabilities and easy agentic turns stay local. See [docs/clients.md](../../docs/clients.md).

Then send requests (`poc.http` has more examples):

```bash
curl -s http://localhost:5090/v1/chat/completions -H "Content-Type: application/json" \
  -d '{"model":"auto","messages":[{"role":"user","content":"Compare event sourcing and CRUD for a billing system"}]}' -i
```

```powershell
Invoke-RestMethod http://localhost:5090/v1/chat/completions -Method Post -ContentType 'application/json' `
  -Body '{"model":"auto","messages":[{"role":"user","content":"hello!"}]}'
```

Any tool that lets you set an OpenAI base URL will work. Use base URL `http://localhost:5090/v1`, any API key, and one of these models:

| `model` | Effect |
|---|---|
| `auto` | the router decides |
| `lmstudio` | force LM Studio |
| `claude` | force Claude |
| `auto:best` | the most capable model available |
| `claude-sonnet-…`, `claude-opus-…` (what Claude Code sends) | mapped by `Middleware:ModelAliases`: `*haiku*` → `auto:cheapest`, every other Claude name → `auto` (Claude Code sends its default Opus name whether or not you chose Opus) |
| `opus-pinned` (`claude --model opus-pinned`) | `family:opus`: the catalog's Opus model, or else the most capable (here `claude`) |

Every response carries routing headers:

| Header | Meaning |
|---|---|
| `X-Prompt-Route` | the matched route (`local-easy`, `claude-hard`, `private`, or `none`) |
| `X-Prompt-Route-Tier` / `-Reason` | what the prompt needed, and why |
| `X-Prompt-Route-Model` / `-Attempt` | who answered, and on which try |
| `X-Prompt-Route-Degraded: true` | answered by a less capable model because the better one was unavailable |

The console logs each decision too. To see why a prompt would go where it goes without calling any model:

```bash
curl -s http://localhost:5090/semantic-router/classify -H "Content-Type: application/json" -d '{"prompt":"why does my async code deadlock?"}'
```

## Tune it

Everything is in `appsettings.json`:

- **LM Studio model.** Set `SemanticRouter:Models:0:Model` to an id from `curl http://localhost:1234/v1/models`, or leave it empty to auto-select. You can also pass `--SemanticRouter:Models:0:Model=<id>` on the command line.
- **Claude model.** Change `SemanticRouter:Models:1:Model` to `sonnet`, `opus`, `haiku` or a full model name.
- **What counts as easy or hard.** Add example prompts to the `local-easy` and `claude-hard` routes; more varied examples route better.
  - The proof of concept uses built-in *lexical* embeddings, so matching is driven by shared words.
  - For meaning-based matching, load an embedding model in LM Studio and set `Embeddings` to `{ "Provider": "OpenAICompatible", "Endpoint": "http://localhost:1234", "Model": "<embedding id>" }`. Raise `DefaultSimilarityThreshold` to around `0.6`, then check with `/semantic-router/classify`.
- **Complexity escalation.** `ComplexityTiers: [{ "MinScore": 0.6, "Tier": 3 }]` sends any sufficiently complex prompt to Claude, even if it matched an easy route. Raise `MinScore` to use Claude less, or lower it to use Claude more.
- **Unmatched prompts.** `FallbackTier: 1` sends prompts that match no route to LM Studio. Set it to `3` to send them to Claude.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `[FAIL] LM Studio: not reachable` | Start LM Studio's server. If you changed its port, update `Connectors:lmstudio:BaseAddress`. |
| `[FAIL] LM Studio: … requires authentication` | "Require Authentication" is on in LM Studio's server settings. Copy the token there and run `dotnet user-secrets set "SemanticRouter:Connectors:lmstudio:ApiKey" "<token>"` (or `export LM_API_TOKEN=<token>`). Set `SemanticRouter:Embeddings:ApiKey` too if LM Studio serves your embeddings. |
| `[FAIL] LM Studio: … rejected the API token` | The configured token does not match LM Studio's. Copy it again from the server settings. |
| `[FAIL] LM Studio: model '…' is not available` | Use one of the ids it lists, or clear `Models:0:Model`. |
| `[FAIL] Claude CLI: 'claude' not found` | Install Claude Code, or set `Connectors:claude-cli:Executable` to the full path (on Windows, `claude.exe`). |
| `[FAIL] Claude CLI: … not signed in` | Run `claude`, then `/login`. |
| `[WARN] … signed in with an API key` | Requests will be billed to that key. Run `/logout` and `/login` with your Claude account to use your subscription. |
| `[FAIL] claude … unknown option '…'` | Your Claude Code version doesn't accept a flag the preset uses (they were checked against 2.1.293). Update Claude Code (`claude update`), or tell us the version. |
| `[FAIL] claude … You've hit your session limit` | Expected when you're out of quota. Hard prompts fall back to LM Studio (marked `*`) for 15 minutes, then Claude is tried again. |
| Hard prompts show `lmstudio*` | Claude failed or is cooling down; the console log shows why. |

Use `--skip-preflight` to start without the checks.

## What it does with your Claude login

Nothing directly. The router runs your installed `claude` binary, which uses its own login. The router never reads, stores or forwards credentials. It also:
- removes `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN` and `ANTHROPIC_BASE_URL` from the CLI's environment, so your subscription is what gets used;
- runs every request in an empty temporary folder, with Claude Code's tools disabled;
- only listens on `localhost`, and only answers local callers through the CLI.

See [docs/TERMS.md](../../docs/TERMS.md) for why it is built this way and how that relates to Anthropic's terms.
