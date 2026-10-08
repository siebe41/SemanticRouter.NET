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
| **Overview → Trends** | Requests per interval and p50/p95 latency over 1 hour, 24 hours, 7 or 30 days, with a table view. |
| **Usage** | Tokens and cost by model, connector, route, client or day; "saved by routing" against a reference model; budgets with their state; the usage settings and budget editor. |
| **Activity** | The last 500 routed requests: prompt preview, protocol, tier, reason, route and similarity, the model that answered, attempt number, degraded flag, timing. Filter by failed, degraded or tool-bearing. **Label** says which tier a prompt should have had: it adds the prompt to the evaluation set and, optionally, as an utterance to a route. |
| **Tiers** | The capability ladder with names and descriptions, and a matrix of which routes require each tier and which models provide it. Shows at a glance when a tier has nothing that can serve it. |
| **Routes** | Intent clusters: name, tier, required capabilities, threshold, local-only, utterances. |
| **Evaluate** | Runs the evaluation set through the configuration: accuracy, too-low and too-high counts, accuracy per tier, a confusion matrix, the misrouted prompts with their scores, a threshold sweep with recommended values, route health, and a comparison with another embedding model. See [routing-quality.md](routing-quality.md). |
| **Models** | The catalog: connector, provider model id (with **Discover**, which asks the connector's `/v1/models`), tier, capabilities, context window, costs, priority, local flag, enabled. |
| **Connectors** | HTTP (OpenAI-compatible or Anthropic): base address, path, API key, headers, forwarded headers, timeout. CLI: preset, executable, extra arguments, tool calling, quota patterns, environment, concurrency. **List models** probes the connector. |
| **Routing rules** | Similarity threshold, fallback tier, ambiguity margin, scoring strategy, complexity short-circuit, complexity → tier rules, selection strategy and failover, cooldowns, complexity heuristic weights and keywords. |
| **Embeddings** | Provider, endpoint, model, batch size, timeout, warm-up, and a probe that embeds a sentence with the applied provider. |
| **Gateway** | Mode (Dispatch or Annotate), OpenAI and Anthropic path prefixes, model aliases, tool-turn pinning, system-reminder stripping, body limit, response headers, fail-open, message selection. |
| **Connect clients** | Ready-to-paste snippets for Claude Code, OpenAI SDKs, Codex CLI and curl, filled in with the router's address. |
| **JSON** | The whole document as text (the same shape as the `SemanticRouter` section of `appsettings.json`): edit, download, compare with the application baseline, reset. **History** lists every applied version (when, what applied it, who), diffs any of them against the draft and rolls back to it. |

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

Applied documents are kept in the configuration history: in memory by default, or on disk with `UseSqliteState` (see [architecture.md](architecture.md#durable-state)).

Secrets are never sent to the browser: the API masks them as `***`, and sending `***` back keeps the stored value.

## Keeping secrets out of the file

Three ways to give a connector its key, from safest to least safe:

1. **Leave the field empty** and keep the key in user-secrets or an environment variable (`SemanticRouter__Connectors__openai__ApiKey`). The saved document inherits it from the baseline.
2. **Write a reference** in the field: `${env:OPENAI_API_KEY}` reads an environment variable each time the configuration is applied; `${secret:connectors/openai/ApiKey}` reads the secret store. A reference can sit inside a value (`Bearer ${env:TOKEN}` in a header). References are shown as they are (they are not secrets), and an unresolved one is listed as a warning in the banner, in `GET api/status` (`warnings`) and in the log.
3. **Type the key.** With a secret store registered (`.UseSecretFile()`, or your own `IRouterSecretStore` through `.UseSecretStore<T>()`), **Apply & save** moves it into the store and the file keeps a `${secret:…}` reference. `UseSecretFile` encrypts values with ASP.NET Core Data Protection into `{config name}.secrets.json` next to the configuration file; the data-protection keys stay in your user profile (DPAPI-protected on Windows), so a copy of the configuration directory carries no usable key. Without a store, the key is written to the file in plain text and the save says so, with these alternatives.

References work in connector `ApiKey` and `Headers`, CLI `EnvironmentVariables`, `Embeddings:ApiKey`, `Middleware:GatewayKeys` and the dashboard tokens. Resolved values are redacted (`***`) from 503 details, relayed upstream errors, activity entries and log messages, so a backend that echoes a key back cannot leak it through the router.

## Admin API

All under the dashboard prefix, JSON in and out. Who may call it is described in [Access and security](#access-and-security).

| Method and path | Purpose |
|---|---|
| `GET api/status` | Runtime version and source, warm-up state, tiers, models with cooldowns, connectors, routes, gateway settings, activity counters. |
| `GET api/config` | The active document (masked) with version, source and store location. |
| `GET api/config/baseline` | The application's own configuration (masked). |
| `POST api/config/validate` | Validation errors for a document. |
| `PUT api/config?persist=true\|false` | Validate, apply, and save (default) or apply in memory. `400` with `errors` when invalid. |
| `POST api/config/reset` | Back to the baseline. |
| `GET api/config/history?limit=50` | Applied versions, newest first: `id`, `timestamp`, `source` (`startup`, `admin API`, `reset`, `rollback`), `actor`, `note`. `PUT api/config?note=...` sets the note. |
| `GET api/config/history/{id}` | That version's document (masked). |
| `POST api/config/history/{id}/rollback` | Apply and save that version; recorded as a `rollback`. |
| `GET api/activity?limit=100&after=<id>` | Recent routing activity. |
| `GET api/activity/series?window=1h\|24h\|7d\|30d` | Counts, mix and latency percentiles per bucket. |
| `GET api/activity/export?format=csv\|jsonl&prompts=false&since=&until=` | Download activity; prompt previews only with `prompts=true`. |
| `GET api/usage?window=day\|week\|month\|7d\|30d&by=model\|connector\|route\|client\|day` | Usage totals and groups, the reference model and every budget's state. |
| `POST api/classify` | `{ "prompt": "...", "model": "auto" }` → the routing plan, without calling a model. |
| `POST api/health/reset` | Clear every cooldown. |
| `GET/PUT api/evaluation/set`, `POST api/evaluation/cases`, `GET api/evaluation/set/export` | The evaluation set. See [routing-quality.md](routing-quality.md#api). |
| `POST api/evaluate?calibrate=true`, `POST api/evaluate/compare`, `GET api/routes/health` | Evaluate, calibrate, compare embedding models, check routes. |
| `POST api/routes/{name}/utterances` | Add an utterance (or create the route) and apply. |
| `POST api/embeddings/test` | Embed a sentence with the applied provider. |
| `POST api/connectors/{name}/discover` | Ask an HTTP connector for its model list (a `401` is reported as "requires authentication", naming the key to set). |
| `GET api/session` | `{ role, tokenRequired, tokensConfigured }` for the caller. |
| `POST api/session` | `{ "token": "..." }` → sets the session cookie. |
| `DELETE api/session` | Signs out. |

`PUT api/config`, `POST api/config/reset`, rollback, `POST api/health/reset`, changes to the evaluation set, adding utterances and `POST api/evaluate/compare` (which calls an endpoint you name) need the admin role; everything else is open to the read-only role.

The same document works with `PUT` from scripts, e.g. to promote a tested configuration:

```bash
curl -X PUT http://localhost:5080/router/api/config -H 'Content-Type: application/json' --data @semantic-router.json
```

## Access and security

The admin API can change where prompts go, so the default is strict and opening it up takes two explicit settings.

| Setup | Settings | What happens |
|---|---|---|
| **Loopback only** (default) | nothing | Requests from other machines get `403`. Loopback requests must carry a `Host` header naming this machine (`localhost`, `127.0.0.1`, `[::1]`, or a `PublicOrigins` entry), which stops a web page from reaching the dashboard through DNS rebinding. |
| **Token** | `RequireLocalClient: false`, `AdminToken` (and optionally `ReadOnlyToken`) | Remote callers send `Authorization: Bearer <token>`, or sign in on the dashboard, which sets an `HttpOnly`, `SameSite=Strict` session cookie derived from the token (never the token itself; changing the token signs everyone out). The read-only token can view status, activity and the masked configuration and use the prompt tester; it cannot apply, save or reset. Loopback clients still need no token. |
| **Reverse proxy that authenticates** | `RequireLocalClient: false`, `AllowUnauthenticatedRemoteClients: true` | Remote callers are trusted. Only for a proxy that authenticates every request; a warning is logged at start-up. |

```jsonc
"SemanticRouter": {
  "Dashboard": {
    "RequireLocalClient": false,
    "AdminToken": "${env:ROUTER_ADMIN_TOKEN}",       // 16+ characters, e.g. `openssl rand -hex 24`
    "ReadOnlyToken": "${env:ROUTER_VIEW_TOKEN}",
    "PublicOrigins": [ "https://router.home.lan" ]    // where people browse to it, if not localhost
  }
}
```

The same settings are on `RouterDashboardOptions` (`app.MapRouterDashboard("/router", o => ...)` runs after the bound section). With `RequireLocalClient: false` and no token, remote requests are still refused and the log says what to configure.

Whatever the setup:

- **Writes must come from the dashboard's own origin.** `PUT`, `POST` and `DELETE` with an `Origin` other than the request's host (or a `PublicOrigins` entry), or with `Sec-Fetch-Site: cross-site`, get `403`. Scripts that send no `Origin` (curl) are unaffected.
- **A reverse proxy on this machine makes every request look local.** Use ASP.NET Core's forwarded-headers middleware so the real client address is seen, or set `RequireTokenForLocalClients: true`.
- **Subscription CLI connectors stay loopback-only** (`AllowRemoteClients: false`) whatever the dashboard allows: a remote client's request falls through to an API-key or local model. Serving other people from your personal subscription is what [TERMS.md](TERMS.md) rules out.

### Gateway keys

The chat endpoints (`/v1/chat/completions`, `/v1/messages`) are separate from the dashboard. To tell clients of a shared router apart, give each one a key under `Middleware:GatewayKeys` (`"laptop": "${env:ROUTER_KEY_LAPTOP}"`). Remote requests without a valid key get `401` in their protocol's error format; the matched name appears in the Activity page (`client`). Clients send the key the way they send any API key: `ANTHROPIC_AUTH_TOKEN` or `ANTHROPIC_API_KEY` for Claude Code, `OPENAI_API_KEY` for OpenAI clients. Keys are compared in constant time and never logged or forwarded; backends always get the connector's own credentials. Loopback clients may omit the key unless `RequireGatewayKeyForLocalClients` is on. `GET /v1/models` and `count_tokens` are not covered.

## Themes

Themes are token blocks in `app.css`: every colour, font and radius the UI uses is a CSS variable set per `[data-theme]`, and the rest of the stylesheet only refers to the variables. The default, **Machine Dark**, is built on two colours: blue `#228fa7` for the working surfaces (primary actions, navigation, links, focus) and orange `#d08c34` for signals (the tier decision, warnings, cooldowns, the second chart), on near-black surfaces with Inter headings and Fira Sans text. Green and red are kept for status only. **Machine Light** uses the same two colours with darker ink variants for small text, and **Terminal** is a green-on-black alternative.

To add a theme: copy a `:root[data-theme="…"]` block in `app.css`, change the values (`--accent` and `--signal` are the two brand colours; `--accent-ink` is the variant used for small text), and add `{ id, name }` to `THEMES` at the top of `app.js`. The picker in the sidebar lists it; the choice is remembered per browser.
