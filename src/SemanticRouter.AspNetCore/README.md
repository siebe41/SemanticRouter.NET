# SemanticRouter.AspNetCore

ASP.NET Core integration for [SemanticRouter.NET](https://github.com/siebe41/SemanticRouter.NET): it routes each prompt to a local model, a subscription CLI or a paid API, by what the prompt needs. Registration from configuration, the routing middleware for the OpenAI chat-completions, OpenAI Responses and Anthropic Messages protocols, HTTP and CLI connectors, and the dashboard with its admin API.

```bash
dotnet add package SemanticRouter.AspNetCore
```

Dispatch mode, where your application is the gateway that clients such as Claude Code or the OpenAI SDKs point at:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSemanticPromptRouting(builder.Configuration.GetSection("SemanticRouter"));

var app = builder.Build();
app.UsePromptRouter();          // POST /v1/chat/completions, /v1/messages and /v1/responses
app.MapRouterModels();          // GET /v1/models
app.MapRouterDashboard();       // the dashboard and admin API at /router
app.Run();
```

The `SemanticRouter` section names the connectors, models, tiers and routes. To use the router without writing an application, install the [`semantic-router` tool](https://www.nuget.org/packages/SemanticRouter.Host).

- [Using it in your application](https://github.com/siebe41/SemanticRouter.NET#using-it-in-your-application) (dispatch and annotate modes)
- [Configuration reference](https://github.com/siebe41/SemanticRouter.NET#configuration-reference) and [Providers](https://github.com/siebe41/SemanticRouter.NET/blob/main/docs/providers.md)
- [Dashboard](https://github.com/siebe41/SemanticRouter.NET/blob/main/docs/dashboard.md)
- [Changelog](https://github.com/siebe41/SemanticRouter.NET/blob/main/CHANGELOG.md)

MIT licence; the dashboard embeds the Inter and Fira Sans fonts under the SIL Open Font License 1.1. The packages are versioned together and depend on each other exactly: update all the SemanticRouter packages an application uses at once.
