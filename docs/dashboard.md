# The dashboard

A single-page app, embedded in `SemanticRouter.AspNetCore`, that shows what the router is doing and lets you change every setting with hot reload. No build step, no extra package.

```csharp
app.MapRouterDashboard("/router");   // UI at /router, admin API under /router/api
```

```csharp
builder.Services
    .AddSemanticPromptRouting(builder.Configuration.GetSection("SemanticRouter"))
    .UseConfigurationFile("semantic-router.json");   // optional: make dashboard changes survive restarts
```

## Pages

| Page | What it does |
|---|---|
| **Overview** | Counters, every model with its tier and health (ready, cooling down, disabled), the runtime (configuration version and source, store, embeddings state, gateway paths), served-by and required-tier charts, and **Try a prompt**: classify without calling a model, or send it through the gateway for real. |
| **Activity** | The last 500 routed requests: prompt preview, protocol, tier, reason, route and similarity, the model that answered, attempt number, degraded flag, timing. Filter by failed, degraded or tool-bearing. |
| **Tiers** | The capability ladder with names and descriptions, and a matrix of which routes require each tier and which models provide it. Shows at a glance when a tier has nothing that can serve it. |
| **Routes** | Intent clusters: name, tier, required capabilities, threshold, local-only, utterances. |
| **Models** | The catalog: connector, provider model id (with **Discover**, which asks the connector's `/v1/models`), tier, capabilities, context window, costs, priority, local flag, enabled. |
| **Connectors** | HTTP (OpenAI-compatible or Anthropic): base address, path, API key, headers, forwarded headers, timeout. CLI: preset, executable, extra arguments, tool calling, quota patterns, environment, concurrency. **List models** probes the connector. |
| **Routing rules** | Similarity threshold, fallback tier, ambiguity margin, scoring strategy, complexity short-circuit, complexity → tier rules, selection strategy and failover, cooldowns, complexity heuristic weights and keywords. |
| **Embeddings** | Provider, endpoint, model, batch size, timeout, warm-up, and a probe that embeds a sentence with the applied provider. |
| **Gateway** | Mode (Dispatch or Annotate), OpenAI and Anthropic path prefixes, model aliases, tool-turn pinning, system-reminder stripping, body limit, response headers, fail-open, message selection. |
| **Connect clients** | Ready-to-paste snippets for Claude Code, OpenAI SDKs, Codex CLI and curl, filled in with the router's address. |
| **JSON** | The whole document as text (the same shape as the `SemanticRouter` section of `appsettings.json`): edit, download, compare with the application baseline, reset. |

## Apply, save, revert

Editing never touches the running router until you choose:

- **Validate** runs the same checks the router runs on start-up and lists problems.
- **Apply** builds a new runtime from the draft and swaps it in atomically. In-flight requests finish on the old runtime; new ones use the new one. Model cooldowns are kept.
- **Apply & save** also writes the document to the configuration store, when one is registered.
- **Discard** drops the draft.

**Reset** (JSON page) rebuilds from the application's own configuration for this process. The stored file is left in place; save again to overwrite it.

## Where the configuration comes from

1. The application's `SemanticRouter` section (`appsettings.json`, environment variables, user-secrets, code) is the **baseline**.
2. With `UseConfigurationFile(path)` (or a custom `IRouterConfigurationStore`), a saved document **replaces** the baseline on start-up. Secrets left empty in the document (connector `ApiKey`, header values, the embeddings key) are inherited from the baseline, so keys can stay in user-secrets or environment variables.
3. Without a store, changes apply in memory and are lost on restart; the UI says so.

Secrets are never sent to the browser: the API masks them as `***`, and sending `***` back keeps the stored value. Values you type are stored in the configuration file, so keep that file out of source control (the samples' `.gitignore` does).

## Admin API

All under the dashboard prefix, JSON in and out. By default only loopback clients are accepted (`RouterDashboardOptions.RequireLocalClient`); turn that off only behind your own authentication.

| Method and path | Purpose |
|---|---|
| `GET api/status` | Runtime version and source, warm-up state, tiers, models with cooldowns, connectors, routes, gateway settings, activity counters. |
| `GET api/config` | The active document (masked) with version, source and store location. |
| `GET api/config/baseline` | The application's own configuration (masked). |
| `POST api/config/validate` | Validation errors for a document. |
| `PUT api/config?persist=true\|false` | Validate, apply, and save (default) or apply in memory. `400` with `errors` when invalid. |
| `POST api/config/reset` | Back to the baseline. |
| `GET api/activity?limit=100&after=<id>` | Recent routing activity. |
| `POST api/classify` | `{ "prompt": "...", "model": "auto" }` → the routing plan, without calling a model. |
| `POST api/health/reset` | Clear every cooldown. |
| `POST api/embeddings/test` | Embed a sentence with the applied provider. |
| `POST api/connectors/{name}/discover` | Ask an HTTP connector for its model list. |

The same document works with `PUT` from scripts, e.g. to promote a tested configuration:

```bash
curl -X PUT http://localhost:5080/router/api/config -H 'Content-Type: application/json' --data @semantic-router.json
```

## Themes

Themes are token blocks in `app.css`: every colour, font and radius the UI uses is a CSS variable set per `[data-theme]`, and the rest of the stylesheet only refers to the variables. The default, **Machine Dark**, uses the Siebert & The Machine palette (near-black surfaces, one yellow signal colour, green and red for status, Inter headings with Fira Sans text). **Machine Light** and **Terminal** are included.

To add a theme: copy a `:root[data-theme="…"]` block in `app.css`, change the values, and add `{ id, name }` to `THEMES` at the top of `app.js`. The picker in the sidebar lists it; the choice is remembered per browser.
