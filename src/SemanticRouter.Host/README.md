# SemanticRouter.Host: the `semantic-router` tool

A ready-to-run semantic prompt router from [SemanticRouter.NET](https://github.com/siebe41/SemanticRouter.NET). Point Claude Code, Codex, the OpenAI SDKs or any OpenAI- or Anthropic-compatible client at it, and it answers each turn with a local model, a flat-rate subscription CLI or a paid API, by what the prompt needs. It includes the dashboard, where you can watch every decision and edit every setting.

```bash
dotnet tool install -g SemanticRouter.Host
semantic-router init    # finds LM Studio, Ollama and the claude, codex and copilot CLIs on this machine
semantic-router         # starts the router; the dashboard is at http://localhost:5080/router
```

Then run Claude Code through it, with the line the router prints:

```bash
ANTHROPIC_BASE_URL=http://localhost:5080 ANTHROPIC_AUTH_TOKEN=semantic-router CLAUDE_CODE_AUTO_MODE_SERVER=0 claude --model semantic-router
```

- [Quick start](https://github.com/siebe41/SemanticRouter.NET#quick-start) and [Connect your tools](https://github.com/siebe41/SemanticRouter.NET/blob/main/docs/clients.md)
- [Install and run](https://github.com/siebe41/SemanticRouter.NET/blob/main/docs/host.md): the background service and the container
- [Desktop app](https://github.com/siebe41/SemanticRouter.NET/blob/main/docs/desktop.md), for a router in the tray without the .NET SDK
- [Changelog](https://github.com/siebe41/SemanticRouter.NET/blob/main/CHANGELOG.md)

MIT licence; THIRD-PARTY-NOTICES.txt in the package lists the licences of what it ships.
