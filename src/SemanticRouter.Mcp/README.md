# SemanticRouter.Mcp

An MCP server for [SemanticRouter.NET](https://github.com/siebe41/SemanticRouter.NET): agents such as Claude Code, GitHub Copilot and Codex can ask where a prompt would go (`classify`), why a request went where it did (`explain_request`), read usage and cost (`usage`) and connector health (`health`), and, when you allow it, propose a tuning change that a person applies on the dashboard (`propose_tuning`). Callers sign in as they do on the dashboard, and each tool needs the role its admin API endpoint needs.

```bash
dotnet add package SemanticRouter.Mcp
```

```csharp
builder.Services
    .AddSemanticPromptRouting(builder.Configuration.GetSection("SemanticRouter"))
    .AddRouterMcpServer();

app.MapRouterDashboard("/router");
app.MapRouterMcp("/router/mcp"); // serves only with SemanticRouter:Mcp:Enabled = true
```

- [The router's MCP server](https://github.com/siebe41/SemanticRouter.NET/blob/main/docs/clients.md#the-routers-mcp-server): tools, settings and client setup
- [Changelog](https://github.com/siebe41/SemanticRouter.NET/blob/main/CHANGELOG.md)

MIT licence; it uses the official MCP C# SDK (Apache-2.0). The packages are versioned together and depend on each other exactly: update all the SemanticRouter packages an application uses at once.
