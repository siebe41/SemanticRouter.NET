# Roadmap

Where SemanticRouter.NET is headed, and why in this order. Each goal is a GitHub issue with scope and acceptance criteria; progress is tracked in [#19](https://github.com/siebe41/SemanticRouter.NET/issues/19). Add ideas as issues with the `roadmap` label.

## Where we are

Today the router is a library, a `semantic-router` .NET tool and container image (`init`, `doctor`, run), a desktop app with installers for Windows, macOS and Linux (first released as [v0.1.0](https://github.com/siebe41/SemanticRouter.NET/releases/tag/v0.1.0)), and two samples:

- It accepts OpenAI chat completions and the Anthropic Messages API, routes each turn to local models, subscription CLIs or paid APIs, and writes the reply in the client's protocol, including streaming and tool calls.
- Claude Code can be pointed at it with `ANTHROPIC_BASE_URL`; its model names map through aliases, its tool loops stay on one model, and a subscription CLI can answer its tool-bearing turns through prompted tool calling.
- A dashboard edits every setting with hot reload and shows live status and activity.
- Keys can stay out of the configuration file (`${env:…}` references, an encrypted secret store), and the dashboard and gateway can be opened to other machines with tokens and gateway keys.

- Cooldowns, tool-call affinity, activity and every applied configuration can survive restarts (SQLite; on by default in the tool), with diff and rollback on the dashboard.

- It records tokens and cost per request, shows usage and "saved by routing" on the dashboard, enforces budgets, honours limit reset times, and emits OpenTelemetry-ready traces and metrics.

- It has a Responses API front door for Codex. Thinking, structured output and serial tool calls cross protocols, and what a backend cannot carry is reported in the `X-Prompt-Route-Dropped` header.

- Routing can be measured and tuned on your own prompts: evaluation sets, threshold calibration, route health, labelling activity entries, and optional retry escalation. It routes by the conversation rather than the last message, honours explicit choices (`@opus`, `/model opus`), notices when a decision was not good enough, tunes its thresholds and utterances from that by itself, with every change shown and undoable, and stops sending a model the kind of work it keeps failing at.

- It routes with a real embedding model and no server: an ONNX model runs in-process and is downloaded on first use, and recent decisions are cached.

- It knows who is asking: routing profiles per client, project or tool set narrow the models a request may use, and a conversation that contains a credential can be kept on this machine.

- Prompted CLI turns stream their text and survive near-miss and broken tool calls, and Copilot is reachable through GitHub's official SDK with native tool calls.

- The dashboard is tested end to end in the browser on every change, meets WCAG AA in every theme, works with a keyboard and on a phone, and takes drop-in theme files.

- It runs in the background as a per-user service (`semantic-router service install`: systemd, launchd or a Windows sign-in entry), so no terminal has to stay open, or as a desktop app with a tray icon and the dashboard in its own window or the browser.

- It learns from how its answers land: an optional judge grades a sample of cheap answers, and how often it agrees with your hand labels scales how much tuning trusts it. Corrections are recognised in five languages and, for other wording, by meaning. A cheaper model is tried on a share of each route's turns and takes the route over when it does as well.

- It can choose for speed as well as cost: `Fastest` and `Balanced` (`auto:fastest`, `auto:balanced`) use each model's recent time to first byte, and every decision says why it picked the model.

- It reports only what needs a decision: a weekly digest and notices on the dashboard, in the tray and to webhooks (JSON, Slack-compatible, ntfy). Gateway keys can be rate limited per client.

- Protocol translation is fuzz-tested in CI, and the wording that marks a client's side requests ships as a data file that can be updated without a release.

- A provider's whole lineup can be discovered and tiered at once (dashboard and `semantic-router models`): Claude's four families on four rungs, other known families from a model-family table you can extend, the rest of the local models by size, with LM Studio's and Ollama's load state and on-demand loading taken into account.

## What done looks like

You install one tool, point Claude Code, Codex or any chat client at it, and keep working. The router decides per turn between local models, your subscriptions and paid APIs. It tells you what it spent and why it chose what it chose, survives restarts, can be shared safely, and gets better at routing from your own prompts.

## Status of client support

Claims in the docs should match what has been exercised.

| Client | Status |
|---|---|
| Claude Code 2.1.293 | Exercised end to end against a stub OpenAI-compatible backend (a 38-tool streaming turn, tool execution by the client, tool-result continuation) and against the real `claude` CLI as a prompted-tools backend. |
| Codex CLI 0.161.0 | Exercised end to end against a stub OpenAI-compatible backend: a streaming Responses session in which Codex ran a shell command and a namespaced tool, and got the answer back. Recorded Codex requests are replayed in the tests against OpenAI-compatible, Anthropic and prompted-CLI backends. |
| curl, all three protocols | Exercised. |
| OpenAI SDKs, Aider, Continue, Cline, Open WebUI, LibreChat | Standard OpenAI chat completions; not individually tested. |
| Desktop app | Exercised on Linux (X11, WebKitGTK): hosting, attaching to a running router, single-instance hand-over, the dashboard window. The installers (Setup.exe, .pkg, AppImage) install and update in CI on Windows, macOS and Linux; the tray and window are untried on Windows and macOS. |
| Windows and macOS | The whole suite runs on Linux, Windows and macOS in CI, including real child processes started from a stand-in CLI (argument quoting, UTF-8 stdin, process-tree kill). The real vendor CLIs have been exercised on Linux only. |

## Plan

The order follows dependencies: install and trust first, then visibility (which needs durable state), then routing quality, then reach.

### 1. Easy to run, safe to expose

| Issue | Goal | Why now |
|---|---|---|
| [#4](https://github.com/siebe41/SemanticRouter.NET/issues/4) | A ready-to-run host: `dotnet tool` and a container image | "Point Claude Code at it" currently starts with cloning a repo. |
| [#3](https://github.com/siebe41/SemanticRouter.NET/issues/3) | LM Studio servers with "Require Authentication" | The most common local server setup fails the PoC preflight today. |
| [#14](https://github.com/siebe41/SemanticRouter.NET/issues/14) | CI on Windows and macOS | The host should not ship before the platforms it claims are tested. |
| [#6](https://github.com/siebe41/SemanticRouter.NET/issues/6) | Secrets out of the saved configuration file | Dashboard-typed keys are stored in plain text. |
| [#5](https://github.com/siebe41/SemanticRouter.NET/issues/5) | Authentication and safe remote access | Needed before the host binds to anything but loopback. |

### 2. See it and keep it

| Issue | Goal | Depends on |
|---|---|---|
| [#7](https://github.com/siebe41/SemanticRouter.NET/issues/7) | Durable cooldowns, tool-call affinity, activity log, configuration history | none; unlocks the rest of this phase |
| [#8](https://github.com/siebe41/SemanticRouter.NET/issues/8) | Usage, cost and quota tracking; budgets steer selection | #7 for history |
| [#13](https://github.com/siebe41/SemanticRouter.NET/issues/13) | OpenTelemetry traces, richer metrics, exportable activity | #7 for time series |

### 3. Route better

| Issue | Goal | Depends on |
|---|---|---|
| [#9](https://github.com/siebe41/SemanticRouter.NET/issues/9) | Evaluation sets, threshold calibration, route suggestions from activity | #7 (activity to learn from) |
| [#18](https://github.com/siebe41/SemanticRouter.NET/issues/18) | In-process embeddings and a classification cache | #9 to judge the new model fairly |
| [#16](https://github.com/siebe41/SemanticRouter.NET/issues/16) | Routing profiles and policy beyond the prompt text | #5 for per-key profiles, #8 for budgets |

### 4. Reach further

| Issue | Goal | Notes |
|---|---|---|
| [#10](https://github.com/siebe41/SemanticRouter.NET/issues/10) | Responses API front door; verify Codex CLI | Includes correcting the Codex docs with what is true. |
| [#12](https://github.com/siebe41/SemanticRouter.NET/issues/12) | Extended thinking, prompt caching and structured output across protocols | Biggest quality gap for Claude Code on non-Anthropic backends. |
| [#11](https://github.com/siebe41/SemanticRouter.NET/issues/11) | Lower latency and quota cost of prompted CLI turns | Any session-reuse change must be checked against [TERMS.md](TERMS.md) first. |
| [#17](https://github.com/siebe41/SemanticRouter.NET/issues/17) | GitHub Copilot SDK connector | The vendor-supported path that the CLI preset only approximates. |

### 5. Always on

| Issue | Goal | Notes |
|---|---|---|
| — | Background service per user (`semantic-router service`) | Done: systemd user unit, launchd agent, Windows sign-in entry. |
| — | Desktop app: tray or menu-bar icon, the dashboard in a native window or the browser, start at sign-in | Done ([desktop.md](desktop.md)): wraps the existing dashboard in the platform web view (WebView2, WKWebView, WebKitGTK, WPE) and hosts the router in-process or uses the running service. The app can also own the background service (`SemanticRouter --headless`), offered on first start. Installers ([#50](https://github.com/siebe41/SemanticRouter.NET/issues/50)): a per-user Windows `Setup.exe`, a macOS `.pkg` and a Linux AppImage with built-in updates, install, update and uninstall exercised on Windows in CI. Still to do: sign the Windows installer and notarize the macOS app. |

### 6. Set it and forget it

Tracked in [#40](https://github.com/siebe41/SemanticRouter.NET/issues/40): point a client at the router once and keep getting good results without touching it.

| Issue | Goal | Notes |
|---|---|---|
| [#41](https://github.com/siebe41/SemanticRouter.NET/issues/41) | Route by the conversation, not only its latest message | Done. |
| [#42](https://github.com/siebe41/SemanticRouter.NET/issues/42) | Explicit model choices win, and only explicit ones | Done: inline callouts and `family:` aliases. |
| [#43](https://github.com/siebe41/SemanticRouter.NET/issues/43) | Record implicit outcome signals per routing decision | Done. |
| [#45](https://github.com/siebe41/SemanticRouter.NET/issues/45) | Tune thresholds and utterances automatically, within guardrails | Done: applied with every change shown and undoable. |
| [#44](https://github.com/siebe41/SemanticRouter.NET/issues/44) | Track model reliability per kind of work | Done: per route and for tool use, left out for a period, never an explicit choice. |
| [#46](https://github.com/siebe41/SemanticRouter.NET/issues/46) | Model-family priors: tiers and capabilities from a curated table | Done: built-in table, `model-families.json` override. |
| [#47](https://github.com/siebe41/SemanticRouter.NET/issues/47) | Default routes and evaluation prompts for agentic clients | Done: client side requests recognised, a continuation route, an 88-prompt evaluation set gated in CI. |
| [#48](https://github.com/siebe41/SemanticRouter.NET/issues/48) | Optional local judge that grades a sample of cheap answers | Done: off by default; sampled, capped per day, never a local-only request to a cloud judge; low grades are feedback. |
| [#49](https://github.com/siebe41/SemanticRouter.NET/issues/49) | Weekly digest; notify only when a decision is needed | Done: digest on the Overview and in the tray; notices for limits, unreliable models, connectors down and tuning suggestions; one switch. |

### 7. After set it and forget it

Tracked in [#62](https://github.com/siebe41/SemanticRouter.NET/issues/62).

| Issue | Goal | Notes |
|---|---|---|
| [#63](https://github.com/siebe41/SemanticRouter.NET/issues/63) | First release, with the installers exercised on macOS and Linux | Done: CI installs and updates on Windows, macOS and Linux; released as [v0.1.0](https://github.com/siebe41/SemanticRouter.NET/releases/tag/v0.1.0). |
| [#64](https://github.com/siebe41/SemanticRouter.NET/issues/64) | Sign the Windows installer and notarize the macOS app | Done: the release workflow signs when the secrets are set ([desktop.md](desktop.md#signing)). |
| [#65](https://github.com/siebe41/SemanticRouter.NET/issues/65) | Keep incident counts and the judge's daily count across restarts | Done with the SQLite state. |
| [#66](https://github.com/siebe41/SemanticRouter.NET/issues/66) | Linux autostart icon when the app runs from an AppImage | Done. |
| [#67](https://github.com/siebe41/SemanticRouter.NET/issues/67) | Recognise corrections in any language | Done: phrases in five languages, and a comparison by meaning for the rest ([routing-quality.md](routing-quality.md#corrections-in-other-words)). |
| [#68](https://github.com/siebe41/SemanticRouter.NET/issues/68) | Measure how often the judge agrees with hand labels | Done: judge agreement on the Evaluate page and `GET api/judge/agreement`; it scales the judge's weight in tuning ([routing-quality.md](routing-quality.md#the-judge)). |
| [#69](https://github.com/siebe41/SemanticRouter.NET/issues/69) | Learn the cheapest good-enough model per route | Done: trials of a cheaper model per route, kept or dropped on feedback ([routing-quality.md](routing-quality.md#cheaper-models-per-route)). |
| [#70](https://github.com/siebe41/SemanticRouter.NET/issues/70) | Latency as a model selection factor | Done: `Fastest` and `Balanced` strategies (`auto:fastest`, `auto:balanced`) from each model's recent time to first byte; the reason shows on the Activity page ([README](../README.md#step-2-choosing-a-model)). |
| [#71](https://github.com/siebe41/SemanticRouter.NET/issues/71) | Send the digest and notices to a webhook | Done: JSON, Slack-compatible and ntfy webhooks ([dashboard.md](dashboard.md#digest-and-notifications)). |
| [#72](https://github.com/siebe41/SemanticRouter.NET/issues/72) | Client side-request wording as an updatable data file | Done: `client-turns.json`, overridable next to the configuration ([clients.md](clients.md)). |
| [#73](https://github.com/siebe41/SemanticRouter.NET/issues/73) | Fuzz tests for protocol translation | Done: `ProtocolFuzzTests` in CI with a fixed seed; longer runs by hand ([CONTRIBUTING.md](../CONTRIBUTING.md#building-and-testing)). |
| [#74](https://github.com/siebe41/SemanticRouter.NET/issues/74) | Per-client rate limit for gateway keys | Done: `Middleware:RateLimits` ([dashboard.md](dashboard.md#rate-limits)). |

### 8. Keep prompt caches working

Tracked in [#92](https://github.com/siebe41/SemanticRouter.NET/issues/92). Caches are per model, so moving a conversation between models re-sends and re-writes its whole context; the router should weigh that before it moves one.

| Issue | Goal | Notes |
|---|---|---|
| [#93](https://github.com/siebe41/SemanticRouter.NET/issues/93) | Get the prompt-cache numbers right | Streamed and CLI cache counts, `prompt_cache_key`, "saved by routing", a hit rate on the dashboard. |
| [#94](https://github.com/siebe41/SemanticRouter.NET/issues/94) | Cache-aware stickiness | Done: a conversation stays on the model holding its prompt cache while moving costs more, and returns to it after a failover (`Middleware:Conversation:KeepWarmCaches`, [routing-quality.md](routing-quality.md#conversations)). |
| [#95](https://github.com/siebe41/SemanticRouter.NET/issues/95) | Cache markers on translated Anthropic requests | So OpenAI and Codex clients routed to Claude use its cache. |
| [#96](https://github.com/siebe41/SemanticRouter.NET/issues/96) | Cache-aware cost estimates | Ranking and per-route model trials count cache prices. |

### Continuous

| Issue | Goal |
|---|---|
| [#15](https://github.com/siebe41/SemanticRouter.NET/issues/15) | Dashboard end-to-end tests, accessibility, drop-in themes |

## Principles

These constrain every item above.

- **Local first, private by default.** Loopback binding, local-only routes that hold on failover, no prompt text in telemetry unless asked for.
- **Never touch credentials.** The router does not read, store or forward a client's or a vendor CLI's login. New features that need credentials use the vendor's own sign-in flow.
- **Respect limits.** A usage limit is a cooldown, never a retry loop, account rotation or a way around the limit.
- **Zero dependencies in Core.** Anything that needs a package (ONNX, Redis, SQLite, a vendor SDK) ships as a separate package.
- **Explainable.** Every decision stays inspectable: headers, activity, logs, the classify endpoint.
- **Claims match tests.** If a client or platform has not been exercised, the docs say so.

## Not planned

- Serving other people from a personal subscription login, or any hosted multi-user service built on one. Use API-key connectors for anything shared (see [TERMS.md](TERMS.md)).
- Relaying, caching or "refreshing" vendor OAuth tokens or session files.
- Reimplementing a vendor CLI's agent loop. The client runs its own tools; the router chooses the model.
- Training or fine-tuning models. Routing stays embedding-plus-heuristics, tunable from the dashboard.
