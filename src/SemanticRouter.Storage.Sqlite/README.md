# SemanticRouter.Storage.Sqlite

Durable state for [SemanticRouter.NET](https://github.com/siebe41/SemanticRouter.NET) in one SQLite file: model cooldowns, tool-call affinity, the routing activity log, usage, the configuration history and the audit log survive restarts, and router instances on one machine can share them. A file that cannot be opened or written falls back to memory, with a warning.

```bash
dotnet add package SemanticRouter.Storage.Sqlite
```

```csharp
using SemanticRouter.AspNetCore;

builder.Services
    .AddSemanticPromptRouting(builder.Configuration.GetSection("SemanticRouter"))
    .UseSqliteState("semantic-router.db");
```

- [Using it in your application](https://github.com/siebe41/SemanticRouter.NET#using-it-in-your-application)
- [Changelog](https://github.com/siebe41/SemanticRouter.NET/blob/main/CHANGELOG.md)

MIT licence. The packages are versioned together and depend on each other exactly: update all the SemanticRouter packages an application uses at once.
