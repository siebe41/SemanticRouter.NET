# Video plan

Three videos for now, each recorded with this kit (real router, real dashboard, real clients; stand-in models) so
they can be re-recorded whenever the product changes.

| | Video | Length | For | Question it answers |
|---|---|---|---|---|
| 1 | **Intro** ([record.js](record.js)) | ~1:45 | anyone | What is it, how fast is setup, what does it do for me? |
| 2 | **Deep dive: how a prompt gets routed** | ~6 min | developers deciding whether to trust it | Why did *this* prompt go to *that* model, and what happens when things go wrong? |
| 3 | **How-to: the settings** | ~6 min | new users after `init` | How do I make it fit my models, my budget and my privacy rules? |

Later candidates, each 1–2 minutes: per-client setup clips (Codex CLI, GitHub Copilot, Open WebUI, the OpenAI SDK);
the memory map and privacy; a vertical cut of the intro for social media.

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

## 2. Deep dive: how a prompt gets routed (~6 min)

Setup: the `init` configuration, Claude Code and an OpenAI client, the dashboard full screen most of the time, with
zoomed callouts. Chapter cards between sections.

| # | Chapter | On screen | Narration (draft) |
|---|---|---|---|
| 1 | Two questions | Animated diagram: prompt → *what does it need?* → *who should answer?* | "Every request gets two questions. First: what does this prompt actually need? Then: which of your models is the cheapest one that can do it?" |
| 2 | Routes and embeddings | **Routes** page, then **Overview → Try a prompt** with three prompts; the route scores bar | "Routes are example prompts grouped by intent, each with a tier. The router embeds your prompt and finds the closest examples. Here, 'find the memory leak' lands on complex code, at tier four." |
| 3 | Complexity | Try a prompt: a one-liner, then the same ask with a pasted stack trace and "step by step" | "Similarity isn't everything. Length, code, structure and reasoning words can raise the tier. The same question with a stack trace pasted in moves up a tier." |
| 4 | Tiers and the catalog | **Tiers** matrix; **Models** grouped by connector | "Tiers are your ladder. Every model sits on one. Local models at the bottom, frontier models at the top." |
| 5 | Choosing a model | **Routing rules** → strategy; `auto:cheapest` vs `auto:best` vs `auto:fastest` with curl and `X-Prompt-Route-*` headers | "Cheapest capable is the default: the cheapest model on or above the tier. Free models, local or subscription, tie at zero, so the lowest tier that's good enough wins." |
| 6 | Agentic clients | Claude Code doing a tool loop; Activity shows `ConversationAffinity` on tool results; a title request marked `ClientHousekeeping` | "Coding agents send more than prompts. Tool results stay with the model that asked for them. Titles and summaries go to the smallest model." |
| 7 | Conversations | "ok, go ahead" mid-task stays at tier 4 (`ConversationFloor`); `@opus` and `@local` callouts | "'Go ahead' says nothing about difficulty, so mid-task it keeps the task's tier. And you can always ask: at-opus, at-local." |
| 8 | Privacy rails | A local-only route and a profile; a prompt with an API key in it stays local; **Profiles → What would each profile do?** | "Some things never leave your machine. Local-only routes and profiles hold even when a local model is down." |
| 9 | When things fail | Session limit → cooldown → failover, the Endpoints table, the cooldown clearing | "When a model is down or out of quota, it cools down and the next one takes over. Your tools never see an error." |
| 10 | Seeing why | **Activity** entry expanded; `X-Prompt-Route-*` headers; Evaluate's confusion matrix | "Every decision explains itself: the route, the scores, the tier, every model it tried. And the Evaluate page tells you how often it's right." |
| 11 | Close | End card; link to video 3 | "Next: how to tune all of this for your own models." |

Kit work needed: a diagram card (chapter 1), zoomed callouts on dashboard regions (CSS transform on the iframe plus
an outline box), a curl pane (a third terminal), clicking through dashboard pages from `record.js`
(`page.frameLocator('#dash')`), and replies in `replies.js` for the new prompts.

## 3. How-to: the settings (~6 min)

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

## Production notes

- Everything is generated from scripts, so a product change means re-running, not re-editing.
- Captions are burned in (for muted autoplay), and an `.srt` ships alongside for platforms that take subtitle files.
- Each video ends on the same end card with the install command, so they work as a series.
