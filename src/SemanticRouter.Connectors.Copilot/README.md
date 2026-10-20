# SemanticRouter.Connectors.Copilot

A GitHub Copilot connector for [SemanticRouter.NET](https://github.com/siebe41/SemanticRouter.NET), built on GitHub's official Copilot SDK: it routes prompts to your Copilot subscription with native streaming, tool calls and model discovery. It runs the `copilot` CLI installed and signed in on the machine.

```bash
dotnet add package SemanticRouter.Connectors.Copilot
```

```csharp
builder.Services
    .AddSemanticPromptRouting(builder.Configuration.GetSection("SemanticRouter"))
    .AddCopilotConnector();
```

Then configure a connector with `"Type": "Copilot"`, in configuration or on the dashboard.

- [GitHub Copilot SDK connector](https://github.com/siebe41/SemanticRouter.NET/blob/main/docs/providers.md#github-copilot-sdk)
- [Subscription CLI terms](https://github.com/siebe41/SemanticRouter.NET/blob/main/docs/TERMS.md): read before using a personal plan at work
- [Changelog](https://github.com/siebe41/SemanticRouter.NET/blob/main/CHANGELOG.md)

MIT licence. The packages are versioned together and depend on each other exactly: update all the SemanticRouter packages an application uses at once.
