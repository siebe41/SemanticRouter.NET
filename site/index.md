---
layout: home
title: SemanticRouter.NET
titleTemplate: Send every prompt to the cheapest model that can answer it

hero:
  name: SemanticRouter.NET
  text: The cheapest model that can answer
  tagline: A prompt router for Claude Code, Codex and any OpenAI or Anthropic client. Each prompt goes to a local model, your subscription CLI or a paid API, and fails over when a limit runs out.
  image:
    src: /logo.png
    alt: SemanticRouter.NET
  actions:
    - theme: brand
      text: Get started
      link: /host
    - theme: alt
      text: Watch the videos
      link: /videos
    - theme: alt
      text: View on GitHub
      link: https://github.com/siebe41/SemanticRouter.NET

features:
  - title: Three protocols in, every backend out
    details: OpenAI chat completions, the Responses API and Anthropic Messages, with streaming, images and tool calls. Your tools keep their own model names.
    link: /clients
    linkText: Connect your tools
  - title: Local, subscription or paid
    details: LM Studio, Ollama, the Claude Code, Copilot and Codex CLIs on your own login, OpenAI, Anthropic, Gemini, Azure and OpenRouter.
    link: /providers
    linkText: Providers
  - title: Failover and cooldowns
    details: A model that is down or out of quota is skipped for a while, and an account-wide limit cools down every model on that account at once.
    link: /overview#features
    linkText: Features
  - title: Privacy rails
    details: Local-only routes and profiles, secret detection that keeps a conversation on the machine, and a memory map of which notes reach the cloud.
    link: /routing-profiles
    linkText: Routing profiles
  - title: A dashboard with hot reload
    details: Live activity, every decision explained, a prompt tester, usage and budgets, and an editor for every setting that applies without a restart.
    link: /dashboard
    linkText: The dashboard
  - title: Measured routing
    details: Evaluation sets, threshold calibration, route health, feedback and automatic tuning, on your own prompts.
    link: /routing-quality
    linkText: Routing quality
---

## Try it

```bash
dotnet tool install -g SemanticRouter.Host
semantic-router init
semantic-router
```

Then point your tools at `http://localhost:5080`: [Connect your tools](../docs/clients.md) has the lines for Claude Code, Codex,
the OpenAI SDKs and curl. [Install and run](../docs/host.md) covers the tool, the container and the background service, and
the [desktop app](../docs/desktop.md) puts the router in your tray.

![The dashboard](../docs/images/dashboard.png)
