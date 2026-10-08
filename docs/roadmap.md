# Roadmap

Where SemanticRouter.NET is headed, and why in this order. Each goal is a GitHub issue with scope and acceptance criteria; progress is tracked in [#19](https://github.com/siebe41/SemanticRouter.NET/issues/19). Add ideas as issues with the `roadmap` label.

## Where we are

Today the router is a library, a `semantic-router` .NET tool and container image (`init`, `doctor`, run), and two samples:

- It accepts OpenAI chat completions and the Anthropic Messages API, routes each turn to local models, subscription CLIs or paid APIs, and writes the reply in the client's protocol, including streaming and tool calls.
- Claude Code can be pointed at it with `ANTHROPIC_BASE_URL`; its model names map through aliases, its tool loops stay on one model, and a subscription CLI can answer its tool-bearing turns through prompted tool calling.
- A dashboard edits every setting with hot reload and shows live status and activity.
- Keys can stay out of the configuration file (`${env:…}` references, an encrypted secret store), and the dashboard and gateway can be opened to other machines with tokens and gateway keys.

What it is not yet: something that remembers anything across a restart, or something that tells you what it spent. Those gaps drive the plan.

## What done looks like

You install one tool, point Claude Code, Codex or any chat client at it, and keep working. The router decides per turn between local models, your subscriptions and paid APIs. It tells you what it spent and why it chose what it chose, survives restarts, can be shared safely, and gets better at routing from your own prompts.

## Status of client support

Claims in the docs should match what has been exercised.

| Client | Status |
|---|---|
| Claude Code 2.1.293 | Exercised end to end against a stub OpenAI-compatible backend (a 38-tool streaming turn, tool execution by the client, tool-result continuation) and against the real `claude` CLI as a prompted-tools backend. |
| curl, both protocols | Exercised. |
| OpenAI SDKs, Aider, Continue, Cline, Open WebUI, LibreChat | Standard OpenAI chat completions; not individually tested. |
| Codex CLI | Not tested. It may need the Responses API ([#10](https://github.com/siebe41/SemanticRouter.NET/issues/10)). |
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
