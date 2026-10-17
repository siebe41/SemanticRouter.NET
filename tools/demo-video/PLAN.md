# Video plan

Three videos for now, each recorded with this kit (real router, real dashboard, real clients; stand-in models) so
they can be re-recorded whenever the product changes.

| | Video | Length | For | Question it answers |
|---|---|---|---|---|
| 1 | **Intro** ([record.js](record.js)) | ~1:45 | anyone | What is it, how fast is setup, what does it do for me? |
| 2 | **Deep dive: how a prompt gets routed** ([videos/deep-dive.js](videos/deep-dive.js), recorded) | ~3 min | developers deciding whether to trust it | Why did *this* prompt go to *that* model, and what happens when things go wrong? |
| 3 | **How-to: the settings** ([videos/settings.js](videos/settings.js), recorded) | ~2:10 | new users after `init` | How do I make it fit my models, my budget and my privacy rules? |

Next, so the videos stop being only about Claude Code: GitHub Copilot CLI, VS Code (Copilot's chat and agent mode, and
the CLIs in its terminal), the GitHub Copilot app, and a short "every tool, one router" cut. Their narration is
written (sections 4–7 below); none is recorded yet. Later candidates, each 1–2 minutes: Codex CLI on its own, Open
WebUI, the OpenAI SDK. The memory map and privacy video and the vertical short are made: see the README.

## Voice

- **Narration style:** written to be spoken. Short sentences, contractions, one idea per sentence, and room to
  breathe between beats (`record.js` waits for each line, so pauses come from the script, not the TTS).
- **Engine:** Chatterbox (Resemble AI, MIT-licensed, runs locally). It has more natural cadence than Kokoro and can
  clone a voice from a 10–20 second sample. With the owner's recording, every video can use the owner's own voice.
  Chatterbox watermarks its audio (Resemble's Perth watermark, inaudible), which is fine for this use.
- **Sample to record (for cloning):** 15–30 seconds, one take, quiet room, phone or headset mic at normal speaking
  distance, conversational tone (as if explaining the router to a colleague), WAV or M4A. No music, no other voices.
  Any text works; something like the intro's first lines is ideal because it matches the delivery wanted.

## Before re-recording the intro

- Release the current `main` to nuget.org (0.2.1 there is older, .NET 9).
- Fix #122 (Claude Code's Opus default bypasses routing), #123 (title requests to the cloud), #127 (the "what can you
  help me with in this repo?" misroute) and #125 (Live page clipping), so the video shows no workarounds or
  rough edges.
- #128: if the router serves a `claude-semantic-router` entry for Claude Code's model discovery and the banner prints
  the Claude Code lines, the intro can show `/model` picking "auto (SemanticRouter.NET)" instead of
  `--model semantic-router`.

## 2. Deep dive: how a prompt gets routed (recorded, ~3 min)

Recorded as [videos/deep-dive.js](videos/deep-dive.js). Left out until siebe41/SemanticRouter.NET#130 is fixed:
chapter 7's conversation floor ("ok, go ahead" keeping the task's tier), which doesn't carry over between Claude Code
turns yet; and side requests going to the smallest model (#123). The outline it was built from:

Setup: the `init` configuration, Claude Code and an OpenAI client, the dashboard full screen most of the time, with
zoomed callouts. Chapter cards between sections.

| # | Chapter | On screen | Narration (draft) |
|---|---|---|---|
| 1 | Two questions | Animated diagram: prompt → *what does it need?* → *who should answer?* | "Every request gets two questions. First: what does this prompt actually need? Then: which of your models is the cheapest one that can do it?" |
| 2 | Routes and embeddings | **Routes** page, then **Overview → Try a prompt** with three prompts; the route scores bar | "Routes are example prompts grouped by intent, each with a tier. The router embeds your prompt and finds the closest examples. Here, 'find the memory leak' lands on complex code, at tier four." |
| 3 | Complexity | Try a prompt: a one-liner, then the same ask with a pasted stack trace and "step by step" | "Similarity isn't everything. Length, code, structure and reasoning words can raise the tier. The same question with a stack trace pasted in moves up a tier." |
| 4 | Tiers and the catalog | **Tiers** matrix; **Models** grouped by connector | "Tiers are your ladder. Every model sits on one. Local models at the bottom, frontier models at the top." |
| 5 | Choosing a model | **Routing rules** → strategy; `auto:cheapest` vs `auto:best` vs `auto:fastest` with curl and `X-Prompt-Route-*` headers | "Cheapest capable is the default. Local models go first, the most capable one that's already loaded; then subscriptions, lowest tier first, to save quota; paid APIs last." |
| 6 | Agentic clients | Claude Code doing a tool loop; Activity shows `ConversationAffinity` on tool results; a title request marked `ClientHousekeeping` | "Coding agents send more than prompts. Tool results stay with the model that asked for them. Titles and summaries go to the smallest model." |
| 7 | Conversations | "ok, go ahead" mid-task stays at tier 4 (`ConversationFloor`); `@opus` and `@local` callouts | "'Go ahead' says nothing about difficulty, so mid-task it keeps the task's tier. And you can always ask: at-opus, at-local." |
| 8 | Privacy rails | A local-only route and a profile; a prompt with an API key in it stays local; **Profiles → What would each profile do?** | "Some things never leave your machine. Local-only routes and profiles hold even when a local model is down." |
| 9 | When things fail | Session limit → cooldown → failover, the Endpoints table, the cooldown clearing | "When a model is down or out of quota, it cools down and the next one takes over. Your tools never see an error." |
| 10 | Seeing why | **Activity** entry expanded; `X-Prompt-Route-*` headers; Evaluate's confusion matrix | "Every decision explains itself: the route, the scores, the tier, every model it tried. And the Evaluate page tells you how often it's right." |
| 11 | Close | End card; link to video 3 | "Next: how to tune all of this for your own models." |

Kit work needed: a diagram card (chapter 1), zoomed callouts on dashboard regions (CSS transform on the iframe plus
an outline box), a curl pane (a third terminal), clicking through dashboard pages from `record.js`
(`page.frameLocator('#dash')`), and replies in `replies.js` for the new prompts.

## 3. How-to: the settings (recorded, ~2:10)

Recorded as [videos/settings.js](videos/settings.js), tighter than this outline: where settings live, Discover for a
model LM Studio just downloaded, a paid API (connector, key, Discover, prices), Evaluate's "Add to route…" fixing a
misroute, a local-only profile picked by project, a budget, and `doctor`. Left out for a later clip: embeddings,
gateway keys and rate limits, active hours. The outline it was built from:

Setup: a fresh `init` configuration, then each change made on the dashboard and applied live. Ends with `semantic-router
doctor`. Each section opens with the question a user has.

| # | Section | On screen | Narration (draft) |
|---|---|---|---|
| 1 | Where settings live | `~/.config/semantic-router/semantic-router.json`; the dashboard's **JSON** page; Validate / Apply / Apply & save / Discard | "Everything is in one file, and every page of the dashboard edits it. Changes are drafts until you apply them. Apply swaps them in live, no restart." |
| 2 | "I added a model to LM Studio" | **Models → Discover models** on the lmstudio connector, add one, set its tier, Apply | "Discover lists everything a connector offers, with a suggested tier. Add it, check the tier, apply." |
| 3 | "I want to use an API key too" | **Connectors** → add OpenAI-compatible (OpenRouter), the key stored encrypted, not in the file; Discover; set costs | "Paid models get a cost per million tokens, so cheapest capable knows when to reach for them. The key goes to the encrypted secrets file, never the config." |
| 4 | "Hard prompts go to the wrong place" | **Routes**: add an utterance; **Evaluate**: run, read the confusion matrix, apply the recommended threshold | "When something routes wrong, add an example to the right route, then run Evaluate to check you didn't break anything else." |
| 5 | "Keep work code private" | **Profiles**: a local-only profile picked by project; `LocalOnlyWhen` secrets | "Profiles are policies. This one keeps everything for the billing project on this machine." |
| 6 | "Don't burn my Opus quota" | **Routing rules**: strategy and failover; **Models**: active hours and priority; **Usage**: a budget | "Cap what you spend: budgets per day or month, and active hours for models you only want at night." |
| 7 | "Make it faster" | **Embeddings**: Hashing vs ONNX, the probe; `auto:fastest` | "The in-process ONNX model is the default for accuracy. Want it faster or offline? Switch provider here." |
| 8 | "Let my laptop use it" | **Gateway**: gateway keys, rate limits; `--urls`; dashboard token | "To share the router, give each client its own key. The router never forwards it." |
| 9 | Check everything | `semantic-router doctor` in a terminal | "When in doubt, doctor checks every connector without calling a model." |
| 10 | Close | End card with docs links | |

Kit work needed: typing into dashboard forms from `record.js` (Playwright `fill` and `click` in the iframe, slowed
down to human speed with a visible cursor), a cursor overlay on the stage, and an OpenRouter-like stub (an
OpenAI-compatible stub with a model list and prices).

## Re-recording 1–3, savings, privacy and the short on the new dashboard

The dashboard's sidebar became a section rail with page tabs, and `init`, the template and the admin API changed after
these were recorded. The scripts are updated, and each one was rehearsed end to end with stand-in narration
(`placeholder-voice.js`, `stills.js`; see the README) on 2026-10-17. All six run. What changed:

- **Kit:** `nav()` clicks the section in the rail, then the page's tab; scrolls and highlights keep clear of the taller
  sticky top bar; the off-camera `init` enables Claude with `--enable-cli claude` (without a terminal it now adds it
  disabled).
- **Intro:** `init` asks before enabling Claude; the script answers `y` on camera. Narration unchanged.
- **Deep dive:** the template has nine routes now. The caption says 9, and the `routes` line says "nine": **redo that
  one line** (`TTS_ONLY=routes`). "Overview → Endpoints" is "Connector endpoints". The tester still shows complex code
  at tier 4 and the regex at tier 2, as the narration says.
- **Settings:** Add connector is a menu of presets (the script picks Blank connector); budgets are added with "Add
  budget" and switched to the connector; Evaluate's set is 116 prompts (112 right before the fix), and the script now
  waits for one more right instead of "85 of 88". The save bar says "Apply only". Narration unchanged.
- **Savings:** narration unchanged, but the figures moved a lot in the rehearsal ($97.56 saved and $48.48 of
  subscription use, against $42.14 and $18.59 in the published take), most likely from usage now being priced at the
  catalog's current prices (0819d04). Check they are right before recording, then update `onScreen` from the take.
- **Privacy:** a memory source is a process setting now; the script starts the router with
  `SemanticRouter__Dashboard__AllowProcessSettings=true`. Narration unchanged.
- **Short:** narration unchanged.

So with the voice folders from the first recordings, only the deep dive's `routes` line needs new speech.

## 4. GitHub Copilot CLI (narration written, ~1:30)

Narration: [narration/copilot-cli.json](narration/copilot-cli.json). The intro's story, told with Copilot CLI instead
of Claude Code, on the same kit: two terminals, then the Live page beside Copilot. The router is installed and started
off camera (as the deep dive does), so it opens on the title card.

| Line | On screen | Caption |
|---|---|---|
| `title` | Title card: "SemanticRouter.NET + GitHub Copilot CLI" | |
| `connect` | Second terminal: the four variables typed, then `copilot` and its start screen | **Point Copilot CLI at it** · its own provider settings · "semantic-router" as the model |
| `hello` | Split: Copilot left, Live right. `hi! what can you do?` | from `k.describe(e)`: → local small model |
| `code` | `write a unit test for the InvoiceTotal function` | → local coder |
| `hard` | `the invoice job deadlocks under load, find the root cause` | → a subscription's frontier model (whichever the router picked: the caption reads it from Activity) |
| `local` | `@local why does the cache serve stale data after a deploy?`, then a follow-up without the tag | `@local` → local models for the rest of the conversation · "@local (earlier in the conversation)" |
| `live` | The Live page full screen, Copilot's turns next to a few OpenAI-client prompts (`k.send`) | **Every decision, live** |
| `end` | End card | |

What is typed in the second terminal (the values the router's banner prints):

```bash
export COPILOT_PROVIDER_BASE_URL=http://localhost:5080/v1
export COPILOT_PROVIDER_API_KEY=semantic-router
export COPILOT_MODEL=semantic-router
export COPILOT_PROVIDER_MAX_PROMPT_TOKENS=131072
copilot
```

Kit work: install the real `copilot` (`npm install -g @github/copilot`) into the demo user's prefix, and a `waitFor`
pattern for its start screen and for the end of each answer. The router's own `copilot-cli` connector stays disabled
(what `init` does), so no stand-in Copilot is needed; the hard turn goes to the stand-in Claude as in the intro.

## 5. VS Code: Copilot's chat, agent mode and the terminal (narration written, ~2:15)

Narration: [narration/vscode.json](narration/vscode.json). VS Code on the left two thirds of the stage, the Live page on
the right. The router runs off camera; the project is the intro's `~/billing`.

| Line | On screen | Caption |
|---|---|---|
| `title` | Title card | |
| `add` | Chat view → model picker → **Manage Language Models** → **Add Models** → **Custom Endpoint**; group name "Semantic Router", key `semantic-router`, API type **Chat Completions** | **Add the router as a custom endpoint** · Chat Completions, Responses or Messages: the router speaks all three |
| `models` | `chatLanguageModels.json` filled in (below), saved | `auto` · `auto:best` · tool calling on |
| `pick` | The model picker shows "Auto (router)" and "Best (router)"; pick Auto | inline completions stay with Copilot |
| `ask` | Ask mode, `InvoiceTotal.cs` open: "what does this function do?" | → local small model |
| `agent` | Agent mode: "add a currency parameter to InvoiceTotal and update its callers" — reads files, edits two, runs the tests | → tier 4 · tool results: `ConversationAffinity` |
| `utility` | Activity filtered to the last minutes: the chat's title request on tier 1 | `ClientHousekeeping` → tier 1 |
| `terminal` | The integrated terminal: the Claude Code line from the banner, `claude --model semantic-router`, one prompt; then `copilot` with the variables above | one router behind the chat and the terminal |
| `live` | Live page full width: rows from VS Code, Claude Code and Copilot CLI | **Every tool, every decision** |
| `end` | End card | |

The models file (typed by pasting; VS Code's auto-closing brackets would garble typed JSON):

```json
[
  {
    "name": "Semantic Router",
    "vendor": "customendpoint",
    "apiKey": "${input:semanticRouterKey}",
    "apiType": "chat-completions",
    "models": [
      { "id": "auto", "name": "Auto (router)", "url": "http://localhost:5080/v1/chat/completions", "toolCalling": true, "maxInputTokens": 114688, "maxOutputTokens": 16384 },
      { "id": "auto:best", "name": "Best (router)", "url": "http://localhost:5080/v1/chat/completions", "toolCalling": true, "maxInputTokens": 114688, "maxOutputTokens": 16384 }
    ]
  }
]
```

`maxInputTokens + maxOutputTokens` is the context window the banner prints for Claude Code (131072 here).
With no GitHub sign-in, VS Code's own small jobs need `chat.utilityModel` and `chat.utilitySmallModel` set to a BYOK
model: set both to the router's `auto` in the throw-away profile, so the `utility` line is true.

Kit work, the most of any video here:

- VS Code on the stage. First try `code serve-web` (the official VS Code server, in the stage's iframe like the
  dashboard, driven with `frameLocator`); if Copilot Chat or the Custom Endpoint provider doesn't work there, launch
  the desktop VS Code with Playwright's Electron support and draw its screenshots into the stage.
- A throw-away `--user-data-dir` and `--extensions-dir` under `DEMO_HOME`, with the Copilot Chat extension installed
  and no GitHub account signed in (BYOK models work without one), so no account shows on screen.
- `replies.js`: answers for the ask and agent prompts, and tool calls (read file, edit, run tests) in the
  OpenAI-compatible stub so agent mode has a real tool loop to show.

## 6. The GitHub Copilot app (narration written, ~1:00)

Narration: [narration/copilot-app.json](narration/copilot-app.json). The Copilot app (public preview) takes an
OpenAI-compatible provider under **Settings → Model providers → Add provider**: base URL `http://localhost:5080/v1`,
any key. The router already sets aside the blocks the app wraps around each message (`<system_reminder>`,
`<current_datetime>`, `<copilot_tauri_workspace>`), which is what the `context` line shows: an Activity entry whose
prompt is only what was typed.

| Line | On screen |
|---|---|
| `title` | Title card |
| `add` | The app's Settings → Model providers → Add provider → OpenAI-compatible, filled in, saved |
| `pick` | The session's model picker → `semantic-router` |
| `context` | Activity: the entry's prompt, and the request as sent with its injected blocks (zoomed callout) |
| `turns` | Two prompts in the app, the Live page beside it: local, then frontier |
| `end` | End card |

The kit can't drive it: it is a native app and needs a GitHub sign-in. Record it by hand with a demo GitHub account,
cut to the narration (`tts.py`, then the line lengths in `durations.json`), and take the dashboard shots with the kit.

## 7. Every tool, one router (narration written, ~0:50)

Narration: [narration/clients.json](narration/clients.json). A cut for the top of the README and social media: one
line per client, each showing its setup for a second and one turn landing on the Live page, then all four at once.

| Line | On screen |
|---|---|
| `hook` | Title card: the four tools' names around the router |
| `claude` | The intro's Claude Code terminal: the one-line setup, one turn |
| `copilot` | Copilot CLI: its variables, one turn |
| `codex` | Codex: `~/.codex/config.toml` from [docs/clients.md](../../docs/clients.md#openai-codex-cli), one turn |
| `vscode` | VS Code's chat with "Auto (router)" picked, one turn |
| `live` | Live page full width while all four send turns |
| `end` | End card |

Record it last, in one take with three terminals (`shells` of the kit) and the VS Code stage from video 5.

## Before recording 4–7

- Try each client against the router by hand first and look at Activity: the router has wording for Claude Code's and
  Codex's side requests (`client-turns.json`), not for Copilot CLI's or VS Code's. Any of their titles or summaries
  that land above tier 1 need an entry there before the `utility` line in video 5 is true.
- Copilot CLI can't send custom headers, so it has no `X-Router-Project`: the `local` line uses `@local`, which needs
  no configuration. A local-only profile for it is picked by model name instead (`COPILOT_MODEL=auto@private`).
- [docs/clients.md](../../docs/clients.md) has no Copilot CLI, VS Code or Copilot app section yet; the end cards say
  "everything you saw is in the docs", so add them (with the snippets above) before these go out.
- VS Code: check whether Chatterbox says "VS Code" as two letters; if not, spell it "V S Code" and teach
  `captions.js`'s `written()` to put it back, as it does for "C L I".

## Production notes

- Everything is generated from scripts, so a product change means re-running, not re-editing.
- Captions are burned in (for muted autoplay), and an `.srt` ships alongside for platforms that take subtitle files.
- Each video ends on the same end card with the install command, so they work as a series.
