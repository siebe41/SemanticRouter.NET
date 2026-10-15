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
| **Overview** | Counters, every model with its tier and health (ready, cooling down, disabled), the runtime (configuration version and source, store, embeddings state with the route index's size and warm-up time, classification-cache hit rate, gateway paths), served-by and required-tier charts, and **Try a prompt**: classify without calling a model, or send it through the gateway for real. |
| **Live** | What is happening now, pushed over server-sent events: tiles for requests, the share answered locally, input tokens (reported by the backend, or estimated) to cloud and local models, and notes whose text went to the cloud; a **model router** graph from clients (named by gateway key, or by protocol) through the router to every model, with the edge that just carried a request animated; a feed of decisions with the notes each request carried; and a strip of the latest memory crossings. |
| **Overview → Service** | The router itself. **Point clients at**: the OpenAI-compatible base URL (`…/v1`) and the Anthropic base URL for this machine and, when it listens beyond loopback, for other machines (the machine's name and network addresses, then `PublicOrigins`), each with a Copy button, and whether other machines can use it (only this machine, gateway key required, other machines refused until a key is set, or open to the network). **Runs on**: the machine, process id and uptime, the program, version and .NET runtime, the addresses it listens on and the configuration file. **Health**: `running`, `starting` (embedding the routes), `degraded` (warm-up failed, or some connectors failing or down) or `failing` (no enabled model, or every connector in use failing or down), connectors answering, the embeddings index, the last request and the last answered one, and everything that needs a look. |
| **Overview → Needs a decision** | Shown only when something needs you; see [Digest and notifications](#digest-and-notifications). |
| **Overview → This week** | The [digest](#digest-and-notifications) of the last 7 days: prompts routed, the share served locally, cost and savings, decisions raised a tier, signs a decision fell short, and what the router learned. |
| **Overview → Endpoints** | Where each connector sends requests and how it is doing. **Host** is the address's host and port only (never its path, query or credentials), or for a CLI the program it runs and whom it sends prompts to; `local` marks one on this machine and `unencrypted` plain HTTP to another machine. **State** is `ok`, `degraded` (some of its models cooling down, or a quarter or more of recent requests failed), `failing` (every request since a moment has failed, or the last check failed), `down` (every model cooling down), `idle` (nothing sent yet) or `unused` (no enabled model), with since when, until when and the last error. Over the activity log: the share of requests answered (an attempt that failed and fell through to another model counts against its endpoint), time to the first byte and to the end of a reply (p50/p95), output tokens per second, and when it was last used. **Check** asks it now without calling a model: reachable and key accepted for HTTP, installed and signed in for a CLI, the runtime starts for Copilot (the checks `semantic-router doctor` runs). The top bar shows the host the latest request went to, with its state; the Connectors page shows each connector's state. |
| **Overview → Trends** | Requests per interval and p50/p95 latency over 1 hour, 24 hours, 7 or 30 days, with a table view. |
| **Usage** | Tokens and cost by model, connector, route, client, profile or day; "saved by routing" against a reference model; budgets with their state; the usage settings and budget editor. |
| **Activity** | The last 500 routed requests: prompt preview, protocol, tier, reason, route and similarity, the model that answered, attempt number, degraded flag, timing. Each entry shows its routing profile, project, the signals that kept it local, what the connector noted (a repaired or malformed tool call) and the time to the first byte under the total. Filter by failed, degraded, tool-bearing or profile. **Label** says which tier a prompt should have had: it adds the prompt to the evaluation set and, optionally, as an utterance to a route. |
| **Memory** | The [memory map](memory.md): notes from an Obsidian vault, Claude Code's memory or the MCP memory server as a force-directed graph, coloured by where they have been sent (text to the cloud, mentioned to the cloud, local only, not sent) or by source or folder. Search, filter, select a note for its links and per-model counts; notes pulse as requests carry them. The memory sources are edited here too. |
| **Tiers** | The capability ladder with names and descriptions, and a matrix of which routes require each tier and which models provide it. Shows at a glance when a tier has nothing that can serve it. |
| **Routes** | Intent clusters: name, tier, required capabilities, threshold, local-only, utterances. |
| **Evaluate** | Runs the evaluation set through the configuration: accuracy, too-low and too-high counts, accuracy per tier, a confusion matrix, the misrouted prompts with their scores, a threshold sweep with recommended values, route health, and a comparison with another embedding model. See [routing-quality.md](routing-quality.md). |
| **Models** | The catalog, grouped by connector with each group's tiers at a glance: connector, provider model id (with **Discover**, which suggests the connector's model ids), tier, capabilities, context window, costs, priority, active hours, local flag, enabled. Next to a model's list price, its effective input price once its recent turns show how much it reads from the prompt cache (e.g. "$0.57 input per M with cache (90% cached)"): the price cost estimates and model trials use. `GET api/status` has it per model as `effectiveInputCostPerMillionTokens`, with the shares as `cacheShare`. **Discover models** on a connector lists everything it offers (load state, size, context, which models are already in the catalog) with a suggested name, tier and capabilities for each; tick the ones you want, adjust them, and **Add selected** puts them in the draft. See [Model lineups](providers.md#model-lineups-several-models-per-provider). |
| **Connectors** | HTTP (OpenAI-compatible or Anthropic): base address, path, API key, headers, forwarded headers, timeout. CLI: preset, executable, extra arguments, tool calling (with streaming and repair switches), quota patterns, environment, concurrency and queue limit. Copilot: the CLI the SDK runs, its home directory, concurrency, queue limit. **Discover models** lists what the connector offers, as on the Models page. |
| **Routing rules** | Similarity threshold, fallback tier, ambiguity margin, scoring strategy, complexity short-circuit, complexity → tier rules, selection strategy and failover, cooldowns, the time zone for model active hours, complexity heuristic weights and keywords. |
| **Profiles** | Routing profiles (allowed and denied models and connectors, tier floor and cap, strategy, local-only), how a request picks one (headers, client mappings, rules), virtual models, and the content signals that keep a conversation local. **What would each profile do?** routes one prompt under every profile side by side. See [routing-profiles.md](routing-profiles.md). |
| **Embeddings** | Provider (Hashing, Onnx in-process, or an OpenAI-compatible server), model, endpoint, batch size, timeout, warm-up, and a probe that embeds a sentence with the applied provider. For ONNX models it shows the suggested threshold and can apply it. |
| **Gateway** | Mode (Dispatch or Annotate), OpenAI and Anthropic path prefixes, model aliases, tool-turn pinning, system-reminder stripping, body limit, response headers, fail-open, message selection. |
| **Connect clients** | Ready-to-paste snippets for Claude Code, OpenAI SDKs, Codex CLI and curl, filled in with the router's address. |
| **JSON** | The whole document as text (the same shape as the `SemanticRouter` section of `appsettings.json`): edit, download, compare with the application baseline, reset. **History** lists every applied version (when, what applied it, who), diffs any of them against the draft and rolls back to it. |

![The Live page](images/live.png)

## Digest and notifications

The router handles what it can on its own and asks for attention only when a person needs to decide something.

**The digest** (Overview → **This week**, `GET api/digest`, and a line in the desktop app's tray menu) covers the last 7 days:

- prompts routed and requests served;
- the share served by local models;
- cost, and the savings against `Usage:ReferenceModel` when it is set;
- decisions raised a tier (complexity, an ambiguous match, a retry, the conversation's floor), degraded and failed requests;
- signs a decision fell short ([feedback](routing-quality.md#feedback)) by kind;
- the changes [automatic tuning](routing-quality.md#automatic-tuning) made, linked to the Router learned page, where each one can be undone.

Its numbers are the Usage page's for the same window.

**Notices** say what needs a decision. Each is listed under **Needs a decision** on the Overview, with a link to the page to decide on, and it clears on its own when the condition ends:

| Notice | When |
|---|---|
| A subscription keeps hitting its usage limit | A connector hit its usage limit on `Notifications:LimitDays` (3) of the last 7 days. |
| A budget keeps running out | A [budget](observability.md#budgets) was spent in 2 of its last 4 periods (days, weeks or months), the current one included. |
| A model is unreliable for most of its work | [Model reliability](routing-quality.md#model-reliability) left it out of more than half of the kinds of work it does (routes, requests with tools). |
| A connector is down | Every request to it has failed for `Notifications:ConnectorDownAfter` (1 hour). A usage limit does not count. |
| Tuning wants a change it may not make | [Automatic tuning](routing-quality.md#automatic-tuning) has suggestions waiting: `Tuning:Mode` is `Suggest`, or a change someone undid came back with stronger evidence. |

**Notifications:** when a notice appears, the [desktop app](desktop.md) shows a system notification and the service writes a warning to its log ("Needs a decision: …"). Each is announced once, and the log says when it clears. `Notifications:Enabled: false` is the one switch that silences them (and the webhooks below); the Overview still lists the notices.

**Webhooks:** to hear about notices away from the machine, add webhooks. Each gets a message when a notice appears and when it clears, and the digest of the past week once a week (the first week starts when the webhook is added):

```json
"Notifications": {
  "DashboardUrl": "http://router.lan:5080/router",
  "Webhooks": {
    "phone": { "Url": "https://ntfy.sh/my-router-topic", "Format": "Ntfy" },
    "team":  { "Url": "https://hooks.slack.com/services/T000/B000/XXXX", "Format": "Slack", "Digest": false },
    "ops":   { "Url": "https://example.com/hooks/router", "Notices": false }
  }
}
```

| Format | What is POSTed |
|---|---|
| `Json` (default) | `{"type":"notice","state":"appeared"\|"cleared","key","kind","title","detail","link","since"}`, or `{"type":"digest","summary","digest"}` with the same figures as `GET api/digest`. |
| `Slack` | `{"text": …}`, which Slack incoming webhooks, Mattermost and Discord's `/slack` endpoint accept. |
| `Ntfy` | The text as a plain body, with `Title`, `Tags`, `Priority` and (with a `DashboardUrl`) `Click` headers, for an [ntfy](https://ntfy.sh) topic URL. |

Webhook messages leave out text that can come from prompts or replies: a learned utterance reads as "utterance for code", and a connector's latest error (which for a CLI connector can quote the model's reply) is dropped. They keep the counts, model, connector and route names, kinds and links. `Notifications:IncludeDetails: true` puts the text back, for a webhook you trust with prompts.

`Notices` and `Digest` (both `true`) choose what a webhook gets. `DashboardUrl` turns the notice's page into a link people can open. A webhook URL usually carries a token, so it is treated as a secret: masked on the dashboard and moved to the secret file with `.UseSecretFile()`, like an API key, and it can be a `${secret:…}` or `${env:…}` reference. A delivery that fails is retried three times (after 2, 10 and 60 seconds) and then logged as a warning; it runs in the background and never holds up routing.

Connector failures and usage-limit hits are kept in the state database when there is one (`UseSqliteState`, as the `semantic-router` tool and the desktop app use), so they add up across restarts. Without it they are counted in memory, and a restart starts the count again.

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

Secrets are never sent to the browser: the API masks them as `***`, and sending `***` back keeps the stored value. Secrets are API keys, header values whose name looks like a credential (`key`, `token`, `secret`, `auth`, `cookie`, `password`, `credential`, `session`, `signature`), every CLI `EnvironmentVariables` value, gateway keys and webhook URLs. Credentials written into a connector or embeddings address are masked too: `https://***@host/v1?api-key=***` (other query parameters, such as `api-version`, stay readable). Sent back unchanged, the address keeps its credentials; an edited address with `***` in it is refused until they are typed again. A stored secret only goes where it was configured to go: when a connector's type, address or program changes (or the embeddings' provider or endpoint), `***` and any `${secret:…}` or `${env:…}` reference it carries are refused over the admin API, and the secret has to be entered again (or the configuration file changed). A `${secret:…}` reference may only name its own setting's stored secret, and a new connector cannot take one. The configuration history keeps no secrets either; a rollback takes them from the running configuration.

## Keeping secrets out of the file

Three ways to give a connector its key, from safest to least safe:

1. **Leave the field empty** and keep the key in user-secrets or an environment variable (`SemanticRouter__Connectors__openai__ApiKey`). The saved document inherits it from the baseline.
2. **Write a reference** in the field: `${env:OPENAI_API_KEY}` reads an environment variable each time the configuration is applied; `${secret:connectors/openai/ApiKey}` reads the secret store. A reference can sit inside a value (`Bearer ${env:TOKEN}` in a header). References are shown as they are (they are not secrets), and an unresolved one is listed as a warning in the banner, in `GET api/status` (`warnings`) and in the log.
3. **Type the key.** With a secret store registered (`.UseSecretFile()`, or your own `IRouterSecretStore` through `.UseSecretStore<T>()`), **Apply & save** moves it into the store and the file keeps a `${secret:…}` reference. `UseSecretFile` encrypts values with ASP.NET Core Data Protection into `{config name}.secrets.json` next to the configuration file; the data-protection keys stay in your user profile, so a copy of the configuration directory alone carries no usable key. On Windows the keys are encrypted with DPAPI, also in a `SEMANTIC_ROUTER_KEYS_DIR` folder. On macOS and Linux they are stored unencrypted in `~/.aspnet/DataProtection-Keys`, readable by your account only: a backup of your whole home directory holds both. In the container the keys are on the same `/data` volume as the secrets. Set `SemanticRouter:DataProtection:Certificate` to encrypt the keys to a certificate on any platform; `semantic-router doctor` says how they are protected. See [host.md](host.md#the-key-ring). Without a store, the key is written to the file in plain text and the save says so, with these alternatives.

References work in connector `ApiKey` and `Headers`, CLI `EnvironmentVariables`, `Embeddings:ApiKey`, `Middleware:GatewayKeys` and the dashboard tokens. Resolved values are redacted (`***`) from 503 details, relayed upstream errors, activity entries and log messages, so a backend that echoes a key back cannot leak it through the router.

## Admin API

All under the dashboard prefix, JSON in and out. Who may call it is described in [Access and security](#access-and-security).

| Method and path | Purpose |
|---|---|
| `GET api/status` | Runtime version and source, warm-up state, tiers, models with cooldowns, connectors, routes, gateway settings, activity counters, `machine` (the host name of the machine the router runs on), `service` (the router itself: `processId`, `program`, `startedAt`, `runtime`, `operatingSystem`, `listening`, `addresses` with `url`, `local` and `encrypted`, `remote`, `gatewayKeyRequired`, `state` and `stateNote`, `problems`, `lastRequest`, `lastOutcome`, `lastServed`), and `endpoints`: per connector its `host` (or `program` and `via`), `local`, `encrypted`, `state` and `stateNote`, `attempts`, `failures`, `successRate`, `lastUsed`, `lastSuccess`, `lastFailure` and `lastError`, `failingSince`, `modelsCoolingDown` and `cooldownUntil`, `limitDays`, `firstByteP50`/`P95`, `totalP50`/`P95`, `tokensPerSecond`, and the latest `check`. Addresses here and in check, discovery and doctor messages are shown without their user name, password, query or fragment, where a key may sit. |
| `GET api/config` | The active document (masked) with version, source and store location. |
| `GET api/config/baseline` | The application's own configuration (masked). |
| `POST api/config/validate` | Validation errors for a document. |
| `PUT api/config?persist=true\|false` | Validate, apply, and save (default) or apply in memory. `400` with `errors` when invalid. |
| `POST api/config/reset` | Back to the baseline. |
| `GET api/config/history?limit=50` | Applied versions, newest first: `id`, `timestamp`, `source` (`startup`, `admin API`, `reset`, `rollback`), `actor`, `note`. `PUT api/config?note=...` sets the note. |
| `GET api/config/history/{id}` | That version's document (masked). |
| `POST api/config/history/{id}/rollback` | Apply and save that version; recorded as a `rollback`. |
| `GET api/activity?limit=100&after=<id>` | Recent routing activity. Entries carry `sentTo` (the models a request was sent to, failed attempts included) and `memory` (notes found in it); `feedback` maps entry ids to the [feedback](routing-quality.md#feedback) they received. |
| `GET api/feedback?limit=200` | Recent feedback about routing decisions, newest first. |
| `GET api/feedback/summary?days=7` | Decisions and feedback per route and per model, with the share that received feedback. |
| `GET api/tuning`, `POST api/tuning/run`, `POST api/tuning/changes/{id}/undo` … | [Automatic tuning](routing-quality.md#automatic-tuning): what the router learned, why, and undo. |
| `GET api/reliability`, `POST api/reliability/reset` | [Model reliability](routing-quality.md#model-reliability) per route and with tools, and offering a left-out model again. |
| `GET api/activity/series?window=1h\|24h\|7d\|30d` | Counts, mix and latency percentiles per bucket. |
| `GET api/activity/export?format=csv\|jsonl&prompts=false&since=&until=` | Download activity; prompt previews only with `prompts=true`. |
| `GET api/usage?window=day\|week\|month\|7d\|30d&by=model\|connector\|route\|client\|profile\|location\|day` | Usage totals and groups (`location`: local or cloud), the reference model and every budget's state. |
| `GET api/digest?window=…` | The [digest](#digest-and-notifications) for the same windows as `api/usage` (default the last 7 days), with `notices` (what needs a decision) and `notify`. |
| `GET api/events` | Server-sent events for live views: `activity` when a request is routed and again when it finishes, and a keep-alive comment every 15 s. |
| `GET api/memory` | The memory map and sharing counts ([memory.md](memory.md#api)). |
| `POST api/memory/refresh` | Rescan the memory sources. |
| `POST api/memory/exposure/reset` | Forget which notes were sent where. |
| `POST api/classify` | `{ "prompt": "...", "model": "auto" }` → the routing plan, without calling a model. |
| `POST api/health/reset` | Clear every cooldown. |
| `GET/PUT api/evaluation/set`, `POST api/evaluation/cases`, `GET api/evaluation/set/export` | The evaluation set. See [routing-quality.md](routing-quality.md#api). |
| `POST api/evaluate?calibrate=true`, `POST api/evaluate/compare`, `GET api/routes/health` | Evaluate, calibrate, compare embedding models, check routes. |
| `GET api/themes` | Built-in themes and theme files, with missing tokens and contrast issues; the files are served at `{prefix}/themes/{id}.css`. |
| `POST api/routes/{name}/utterances` | Add an utterance (or create the route) and apply. |
| `POST api/embeddings/test` | Embed a sentence with the applied provider. |
| `POST api/connectors/{name}/check` | Checks the connector now without calling a model, as `semantic-router doctor` does for it: `status` (`Ok`, `Warning`, `Failed`), `milliseconds`, `messages`, `at`. The result is kept for the Endpoints card until the connector points somewhere else. A check of a connector that is already being checked joins it. |
| `POST api/connectors/{name}/discover` | What the connector offers: `models` (ids it serves), `server` (`LM Studio`, `Ollama`), `loading` (`OnDemand`, `LoadedOnly`, `Unknown`) with `loadingNote`, and `lineup`: per model its `kind`, `listed`, `loaded`, `contextLength`, `parametersBillions`, `capabilities`, `inCatalog`, `recommended`, `reason` and a `suggestion` in the configuration document's shape. HTTP connectors are asked (`/v1/models`, plus LM Studio's and Ollama's own APIs on local addresses), the Copilot SDK lists your account's models, and CLI presets return the names they accept. A `401` is reported as "requires authentication", naming the key to set. |
| `GET api/session` | `{ role, tokenRequired, tokensConfigured }` for the caller. |
| `POST api/session` | `{ "token": "..." }` → sets the session cookie, good for `SessionLifetime`. After five wrong tokens from one address in 15 minutes, `429` with `Retry-After` until the first is 15 minutes old. |
| `DELETE api/session` | Signs out. |

`PUT api/config`, `POST api/config/reset`, rollback, `POST api/health/reset`, `POST api/memory/exposure/reset`, changes to the evaluation set, adding utterances and every tuning action need the admin role. So does everything that spends embedding or API calls or scans the disk: `POST api/classify`, `POST api/evaluate` and `api/evaluate/compare`, `POST api/embeddings/test`, `POST api/connectors/{name}/check` and `/discover`, and `POST api/memory/refresh`. Everything else is open to the read-only role; the dashboard disables or hides those buttons for it.

The same document works with `PUT` from scripts, e.g. to promote a tested configuration:

```bash
curl -X PUT http://localhost:5080/router/api/config -H 'Content-Type: application/json' --data @semantic-router.json
```

## Access and security

The admin API can change where prompts go, so the default is strict and opening it up takes two explicit settings.

| Setup | Settings | What happens |
|---|---|---|
| **Loopback only** (default) | nothing | Requests from other machines get `403`. Loopback requests must carry a `Host` header naming this machine (`localhost`, `127.0.0.1`, `[::1]`, or a `PublicOrigins` entry), which stops a web page from reaching the dashboard through DNS rebinding. |
| **Token** | `RequireLocalClient: false`, `AdminToken` (and optionally `ReadOnlyToken`) | Remote callers send `Authorization: Bearer <token>`, or sign in on the dashboard, which sets an `HttpOnly`, `SameSite=Strict` session cookie: the time it was issued, signed with the token (never the token itself). It lapses after `SessionLifetime` (12 hours), and changing the token signs everyone out. Five wrong tokens from one address within 15 minutes, at sign-in or as a bearer token, make that address wait (`429` with `Retry-After`) until the first is 15 minutes old; a right token does not count. The read-only token can view status, activity and the masked configuration; it cannot apply, save or reset, nor classify, evaluate, test embeddings, check or discover connectors, or rescan memory, which spend embedding or API calls or read the disk. Loopback clients still need no token. |
| **Reverse proxy that authenticates** | `RequireLocalClient: false`, `AllowUnauthenticatedRemoteClients: true` | Remote callers are trusted. Only for a proxy that authenticates every request; a warning is logged at start-up. |

```jsonc
"SemanticRouter": {
  "Dashboard": {
    "RequireLocalClient": false,
    "AdminToken": "${env:ROUTER_ADMIN_TOKEN}",       // 16+ characters, e.g. `openssl rand -hex 24`
    "ReadOnlyToken": "${env:ROUTER_VIEW_TOKEN}",
    "SessionLifetime": "12:00:00",                   // how long a sign-in lasts
    "PublicOrigins": [ "https://router.home.lan" ]    // where people browse to it, if not localhost
  }
}
```

The same settings are on `RouterDashboardOptions` (`app.MapRouterDashboard("/router", o => ...)` runs after the bound section). With `RequireLocalClient: false` and no token, remote requests are still refused and the log says what to configure.

Whatever the setup:

- **Writes must come from the dashboard's own origin.** `PUT`, `POST` and `DELETE` with an `Origin` other than the request's host (or a `PublicOrigins` entry), or with `Sec-Fetch-Site: cross-site`, get `403`. Scripts that send no `Origin` (curl) are unaffected.
- **What a CLI runs is changed in the configuration file, not over the API.** Loopback clients are admin without a token, so the admin API refuses changes to a CLI or Copilot connector's `Executable`, `Arguments`, `EnvironmentVariables` and `HomeDirectory`; otherwise any program on the machine could make the router run anything it liked. Adding a preset started from `PATH` (Claude Code, Codex, Copilot) still works from the dashboard. The same goes for which files the router reads: a [memory source](memory.md) with a path no source of its type has now (a new folder, or a source pointed elsewhere) is refused; renaming, turning off or removing a source, and adding one in its default place (Claude Code's `~/.claude`), still work. `AllowProcessSettings: true` lifts both.
- **A reverse proxy on this machine makes every request look local.** Name it in `SemanticRouter:ForwardedHeaders:KnownProxies` so the real client address is seen (see [host.md](host.md#serving-over-https)), or set `RequireTokenForLocalClients: true`.
- **Subscription CLI connectors stay loopback-only** (`AllowRemoteClients: false`) whatever the dashboard allows: a remote client's request falls through to an API-key or local model. Serving other people from your personal subscription is what [TERMS.md](TERMS.md) rules out.

### Gateway keys

The chat endpoints (`/v1/chat/completions`, `/v1/messages`) are separate from the dashboard. To share the router with other machines, give each client a key under `Middleware:GatewayKeys` (`"laptop": "${env:ROUTER_KEY_LAPTOP}"`). Remote requests without a valid key get `401` in their protocol's error format; the matched name appears in the Activity page (`client`). Clients send the key the way they send any API key: `ANTHROPIC_AUTH_TOKEN` or `ANTHROPIC_API_KEY` for Claude Code, `OPENAI_API_KEY` for OpenAI clients. Keys are compared in constant time and never logged or forwarded; backends always get the connector's own credentials. Keys can also come from `appsettings.json` or environment variables (`SemanticRouter__Middleware__GatewayKeys__laptop`, or `ROUTER_GATEWAY_KEY` in the container); these apply alongside the configuration file's own keys at every start, so remove them there. Loopback clients may omit the key unless `RequireGatewayKeyForLocalClients` is on. With no keys configured, remote requests get `401` too, unless `AllowUnauthenticatedRemoteClients` is on (only behind a proxy that authenticates callers). `GET /v1/models`, `count_tokens` and `/semantic-router/classify` follow the same rule.

Web pages cannot use the gateway. A request without a valid key is refused (`403`) when it comes from this machine under a host name that is not this machine (`localhost`, a loopback address or a `PublicOrigins` entry), which is what a DNS-rebinding page looks like, or when it carries a browser `Origin` other than the router's own. SDKs and CLIs send no `Origin` and are unaffected.

### Rate limits

Budgets stop spending once a period's limit is reached, but an agent stuck in a loop can spend a day's budget in minutes, and one busy client can crowd out the others. `Middleware:RateLimits` caps each client over a sliding minute:

```json
"RateLimits": {
  "ci":  { "RequestsPerMinute": 30, "TokensPerMinute": 200000 },
  "*":   { "RequestsPerMinute": 120 }
}
```

An entry is named after a gateway client (a name from `GatewayKeys`). `*` covers every client without its own entry, including clients that send no key, which are counted per address. A request is charged its estimated input tokens when it is admitted and the rest once the reply's usage is known. It is admitted while the client is under both limits. A client over a limit gets `429` with `Retry-After` (the seconds until enough of its last minute has passed) in its protocol's error format: Anthropic `rate_limit_error`, OpenAI Responses `rate_limit_exceeded`, a problem document for chat completions. The request reaches no model and shows in Activity as **rate limited**, with the limit it hit. The **Gateway** page edits the limits and lists each client's requests and tokens in the last minute and how often it was turned away. Without entries, clients on other machines are still limited to `RemoteRequestsPerMinute` (120) each, shown as `remote default`; 0 turns that off. Clients on this machine are not limited unless an entry covers them. Counts are kept in memory, so a restart starts them again; changing a limit takes effect at once.

## Themes

Themes are token blocks in `app.css`: every colour, font and radius the UI uses is a CSS variable set per `[data-theme]`, and the rest of the stylesheet only refers to the variables. The default, **Machine Dark**, is built on two colours: blue `#228fa7` for the working surfaces (primary actions, navigation, links, focus) and orange `#d08c34` for signals (the tier decision, warnings, cooldowns, the second chart), on near-black surfaces with Inter headings and Fira Sans text. Green and red are kept for status only. **Machine Light** uses the same two colours with darker ink variants for small text, and **Terminal** is a green-on-black alternative.

Every built-in theme meets WCAG AA (4.5:1) for body and small text. That includes the status colours on their tinted badge fills and the ink on the blue and orange buttons. A test checks it on every build.

### Adding a theme without rebuilding

Drop a token file into the **themes directory**: `themes/` next to the configuration file (for the tool, next to `semantic-router.json`), or `Dashboard:ThemesDirectory`. After a reload the theme appears in the picker under **Theme files**. Nothing is rebuilt or restarted.

```css
/* themes/ocean.css: the file name is the theme id (lower-case letters, digits and dashes). */
:root[data-theme="ocean"] {
  color-scheme: dark;
  --bg: #0b1726;
  --text: #e6f0fa;
  /* … the rest of the tokens below … */
}
```

An optional manifest next to it gives the display name; without one, the picker shows the id in title case:

```json
{ "name": "Ocean" }
```

**Tokens.** A theme should declare every colour token:
- `color-scheme`
- surfaces and borders: `--bg`, `--bg-elevated`, `--bg-sunken`, `--bg-hover`, `--border`, `--border-strong`
- text: `--text`, `--text-muted`, `--text-faint`
- the working colour: `--accent`, `--accent-ink` (small text and links), `--accent-contrast` (text on accent fills), `--accent-soft`
- the signal colour: `--signal`, `--signal-ink`, `--signal-soft`, `--signal-contrast`
- status: `--ok`, `--warn`, `--danger` and `--info`, each with a `-soft` fill
- charts: `--series-1`, `--series-2`
- `--brand-mark-bg` and `--shadow`

Fonts (`--font-heading`, `--font-body`, `--font-mono`) and radii (`--radius`, `--radius-lg`) are optional. A token the file leaves out falls back to Machine Dark's value.

**Checks.** `GET api/themes` lists every theme with:
- the tokens it is missing;
- its text and surface pairs below 4.5:1, computed the way WCAG defines contrast, with badge fills blended over their surface;
- a problem when the file declares no tokens for its own id.

The picker marks such themes with ⚠ and lists the issues in the option's tooltip.

To add a built-in theme instead, add a token block to `app.css` and `{ id, name }` to `THEMES` in `js/core.js`. The contrast test then covers it too.

## Accessibility

- **Structure.** Landmarks: a navigation with the pages, a main region, and a skip link to it. The current page is marked `aria-current`, and every form control has a label (the end-to-end tests fail when one loses it).
- **Keyboard.** Everything works from the keyboard, and focus is always visible. Expandable items (routes, models, connectors, profiles, memory sources) are buttons that open with Enter or Space. After navigation, focus moves to the page content.
- **Small screens.** Below 720 px the navigation becomes a drawer behind a menu button. Escape closes it.
- **Motion.** `prefers-reduced-motion` turns off transitions, the router graph's flowing edges and the memory map's pulses.

## Testing the dashboard

`tests/dashboard-e2e` is a Playwright suite run in CI against the real `semantic-router` host and a stub model backend. It:
- visits every page, failing on any script or console error and on any control without an accessible name;
- edits and applies a route, adds a model, renames a connector (its models follow), and checks that validation errors appear and that discard restores the configuration;
- runs the prompt tester, switches themes and checks the choice is remembered, and picks up a theme file dropped in at run time;
- checks keyboard operation, the phone navigation and contrast;
- compares screenshots of the three built-in themes at desktop and phone widths.

```bash
dotnet build -c Release
cd tests/dashboard-e2e && npm ci && npx playwright install chromium
npm test                       # or: npm run update-screenshots after an intended visual change
```

The page code is plain JavaScript in native ES modules under `wwwroot/js/` (no build step, no dependencies). `app.js` is the entry point, `core.js` holds state and helpers, `shell.js` holds loading, chrome, themes and routing, and there is one module per group of pages.
