# Documentation

These guides are also on the [documentation site](https://siebe41.github.io/SemanticRouter.NET/), with search and the demo videos ([how the site is built](../site/README.md)).

| Read this | When you want to |
|---|---|
| [Overview and configuration](../README.md) | Understand what the router does, run it, and look up every configuration key. |
| [Connect your tools](clients.md) | Point Claude Code, Codex, the OpenAI SDKs or curl at the router, and see what is tested. |
| [Install and run](host.md) | Install and run the `semantic-router` tool or container, and run it in the background as a per-user service. |
| [Desktop app](desktop.md) | Use the desktop app: the router in the tray, the dashboard in its own window or your browser, start at sign-in. |
| [Dashboard](dashboard.md) | Use or extend the dashboard and its admin API: its pages, access, roles and sessions, the audit log, gateway keys and rate limits, notices and webhooks, themes. |
| [Routing quality](routing-quality.md) | Measure and improve routing on your own prompts: the starting routes, evaluation sets, threshold calibration, route health, conversation routing, feedback, the judge, automatic tuning, model reliability, retry escalation. |
| [Routing profiles](routing-profiles.md) | Route by context, not only by prompt: profiles per project, client or tool set, virtual models, keeping secrets local, budgets per profile, active hours. |
| [Usage, budgets and observability](observability.md) | See what the router spent and why: usage and cost, budgets, limit reset times, traces, metrics, request ids and activity export. |
| [Memory map](memory.md) | Map an Obsidian vault, Claude Code's memory or an MCP memory graph, and see which notes reach local and cloud models. |
| [Provider setup](providers.md) | Configure a provider: LM Studio, Ollama, the subscription CLIs, OpenAI, Anthropic, Gemini, Azure, OpenRouter; tool calling per connector. |
| [Machine-wide policy](enterprise.md) | Set a machine-wide policy, as an administrator, that users' configuration cannot override: connector types, allowed endpoints, local hosts, the dashboard, updates, features. |
| [Verifying a release](releases.md) | Verify a download or the container image: provenance attestations, `SHA256SUMS`, the cosign signature, the SBOMs and third-party notices. |
| [Network traffic](network.md) | See every host the router contacts and when, how to turn each off, and what to put on a firewall allowlist. |
| [Architecture](architecture.md) | Follow a request through the code, understand hot reload, or find the right extension point. |
| [Public API](api-surface.md) | See every public type of the library packages and what it is for. |
| [Roadmap](roadmap.md) | See where the project is going and why in that order. |
| [Subscription CLI terms](TERMS.md) | Check why the subscription CLI connectors are built the way they are and where the lines are. |
