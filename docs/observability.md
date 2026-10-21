# Usage, budgets and observability

What the router spent, where, and why. All of this works without extra packages: usage and budgets live in the router (in memory, or on disk with `UseSqliteState`). Traces and metrics use the .NET `ActivitySource` and `Meter` APIs, which OpenTelemetry, `dotnet-counters` and Prometheus exporters read. No span, metric or log line carries prompt text. A failed CLI run's output, which can quote the model's reply, is in its log line and attempt span only as `State:StorePrompts` allows ([What the router stores](host.md#what-the-router-stores)).

## Usage and cost

Every served request records the tokens the backend reported (input, output, prompt-cache reads and writes) and their cost at the model's prices. The data shows up:

- in the activity entry (`inputTokens`, `outputTokens`, `cacheReadTokens`, `cacheCreationTokens`, `costUsd`);
- in the usage ledger, behind the dashboard's **Usage** page and `GET {dashboard}/api/usage`;
- in the `semantic_router.tokens` and `semantic_router.cost` metrics.

| Backend | Where the numbers come from |
|---|---|
| OpenAI-compatible, translated or passed through as JSON | `usage.prompt_tokens` (cached tokens split out of it), `completion_tokens` |
| OpenAI-compatible, streamed pass-through | The final chunk's `usage`, which servers send only when the client asks with `stream_options.include_usage` |
| Anthropic, JSON or SSE | `usage` on the message, or on `message_start` plus `message_delta` |
| OpenAI Responses, JSON or SSE | `usage` on the response, or on `response.completed` (`input_tokens_details.cached_tokens` split out) |
| Claude Code CLI | The `result` event's `usage`, cache reads and writes included |
| Codex CLI | `turn.completed`'s `usage` (`cached_input_tokens` split out of `input_tokens`) |
| GitHub Copilot | The SDK's usage events: input, output, cache reads and writes |

Prices come from the catalog: `InputCostPerMillionTokens` and `OutputCostPerMillionTokens`, plus optional `CacheReadCostPerMillionTokens` and `CacheWriteCostPerMillionTokens` (default: the input price). Local models and flat-rate subscriptions are priced at 0. The Usage page, the digest and budgets price the recorded tokens at the catalog's current prices, so correcting a model's price (or giving one to a model that had none) corrects its past usage too; a model removed from the catalog keeps the cost it was recorded at. The activity log and the `semantic_router.cost` metric keep the cost at the time of the request.

**Looking prices up.** Providers' APIs do not report prices, so the Models page looks them up in [OpenRouter's public model list](https://openrouter.ai/api/v1/models) (no key needed): **Look up prices** on a connector's group fills in its cloud models' input, output and prompt-cache prices (a subscription's API prices) in the draft, and **Discover models** does the same for the cloud models it suggests. A model is matched by its provider's own id (`claude-sonnet-5-5`, `gpt-6.1-sol`, `models/gemini-3.5-flash`, Claude Code's `opus`), at the price of the provider the connector reaches (Anthropic for an Anthropic connector, OpenAI for `api.openai.com`, the exact model on an OpenRouter connector). A connector on another host, such as a gateway, gets the model maker's price and the dashboard says to check it. Nothing is applied until you Apply & save, and local models are never looked up. The list is fetched only when you ask, kept for six hours, and not at all in [offline mode](network.md#offline-mode) unless `openrouter.ai` is allowed. `POST api/prices/lookup` is the endpoint behind it.

**Subscription value.** A subscription costs nothing per request, but each request uses up its quota. Give a subscription model the provider's pay-as-you-go prices, `ApiInputCostPerMillionTokens` and `ApiOutputCostPerMillionTokens` (plus optional `ApiCacheReadCostPerMillionTokens` and `ApiCacheWriteCostPerMillionTokens`, default: the API input price), and the Usage page shows its use at those prices: what the plan is worth to you. It is kept apart from spend: it never counts toward cost, budgets or routing. `semantic-router init` and the Models page's Discover fill these in for Claude's models (Claude Code and GitHub Copilot), from Anthropic's list prices. The Models page's **Look up prices** fills them in for any subscription model the price list knows (see **Looking prices up**, above).

```jsonc
{ "Name": "claude-opus", "Connector": "claude-cli", "Model": "opus", "Tier": 4,
  "ApiInputCostPerMillionTokens": 4, "ApiOutputCostPerMillionTokens": 20,
  "ApiCacheReadCostPerMillionTokens": 0.4, "ApiCacheWriteCostPerMillionTokens": 5 }
```

**Saved by routing.** Set `Usage:ReferenceModel` to a catalog model, which may be disabled. The dashboard then shows what each request would have cost at that model's prices, cache reads and writes at its cache prices, minus what it did cost. A cache discount is not counted as a saving: the reference model would have cached the conversation too.

**Prompt-cache hits.** The Usage page shows cache reads and writes separately and a hit rate: cache reads as a share of all prompt tokens (input, cache reads and cache writes), overall and per group. Each activity entry shows its cached tokens.

```jsonc
"Usage": {
  "ReferenceModel": "anthropic-opus",
  "TimeZone": "Europe/Amsterdam",          // budget periods start at midnight here; default: this machine's zone
  "WarnAtPercent": 80,
  "Budgets": [
    { "Name": "daily-openai", "Connector": "openai", "Period": "Day", "MaxCostUsd": 5 },
    { "Name": "opus-month",   "Model": "anthropic-opus", "Period": "Month", "MaxTokens": 20000000 }
  ]
}
```

## Budgets

A budget covers one model, every model on one connector, or every request under one [routing profile](routing-profiles.md#budgets-per-profile), per `Day`, `Week` (from Monday) or `Month`, in tokens, dollars or both.

- **At `WarnAtPercent`**, the dashboard shows a banner and the log gets one warning per budget and period.
- **Once spent**, the model is not tried until the period ends:
  - The request goes to the next candidate, with the header `X-Prompt-Route-Budget: <name>` and the activity field `budget`.
  - A request that only that model could serve gets a 503 that names the budget: `budget 'daily-openai' is spent ($5.02 of $5.00 this day)`.

Budgets read the usage ledger. With `UseSqliteState`, spend survives restarts. Otherwise a restart starts the period's count again.

A budget check does not add up the period's usage on every request. The ledger keeps each budget's totals for the period in memory and adds each request to them as it is recorded, so the check costs the same on the last day of the month as on the first. Routers that share one state file read the rows the others added at most every 2 seconds, so they agree on a budget within that time. The Usage page and the digest add up their window in the database.

## Usage limits that say when they reset

A provider that reports when its limit ends replaces the fixed `QuotaCooldown` (15 minutes). The model cools down until that time, capped at 8 days:

| Source | Example |
|---|---|
| HTTP `Retry-After` | `Retry-After: 3600`, or an HTTP date |
| Anthropic rate-limit headers | `anthropic-ratelimit-tokens-reset: 2026-10-08T15:00:00Z` |
| The error message (HTTP body or CLI output) | "You've hit your session limit · resets 3:45pm", "resets Oct 10, 3pm", "resets 9am (Europe/Amsterdam)", "try again in 2 hours 30 minutes", "Please try again in 4h12m" |

A time without a date means the next such time. A time without a zone uses this machine's zone. The dashboard's model table shows "cooling down until …".

## Traces

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t
        .AddSource(SemanticRouterDiagnostics.ActivitySourceName)   // "SemanticRouter"
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(m => m
        .AddMeter(SemanticRouterDiagnostics.MeterName)              // "SemanticRouter"
        .AddPrometheusExporter());
```

One routed request is one trace:

```text
semantic_router.route              protocol, client, request_id, tier, reason, route, planned_model, served_by, outcome
├─ semantic_router.parse
├─ semantic_router.classify        tier, reason, route, similarity, complexity   (the embedding call is its child)
├─ semantic_router.select          candidates, planned_model, local_only
├─ semantic_router.attempt         model, connector, attempt=1, outcome=Failed, failure.kind, failure.detail (redacted)
└─ semantic_router.attempt         model, connector, attempt=2, outcome=Completed, dropped
   └─ HTTP POST (backend)          traceparent is sent to the backend
```

Failed attempts are marked with error status. HTTP backends receive `traceparent`, so a backend that traces joins the same trace.

## Metrics

| Instrument | Unit | Tags |
|---|---|---|
| `semantic_router.decisions` | {decision} | `tier`, `reason`, `route` |
| `semantic_router.classification.duration` | s | `reason` |
| `semantic_router.classification.cache_hits` | {decision} | `reason` (decisions reused from the classification cache, with no embedding call) |
| `semantic_router.attempts` | {attempt} | `model`, `outcome` |
| `semantic_router.request.duration` | s | `model`, `connector`, `outcome` (`served`, `failed`, or `cancelled` when the client went away) |
| `semantic_router.time_to_first_byte` | s | `model`, `connector` (the serving one; the time counts from routing, so it includes attempts that failed before it) |
| `semantic_router.attempt.time_to_first_byte` | s | `model`, `connector` (from the start of the serving attempt only) |
| `semantic_router.tokens` | {token} | `model`, `connector`, `type` (`input`, `output`, `cache_read`, `cache_write`) |
| `semantic_router.cost` | USD | `model`, `connector` |
| `semantic_router.cooldowns` | {cooldown} | `model`, `kind` |
| `semantic_router.failovers` | {failover} | `model` (the one that failed), `kind` |
| `semantic_router.budget.skips` | {skip} | `budget`, `model` |
| `semantic_router.state.dropped_writes` | {write} | State writes the database refused or could not take in time (a locked or full disk): they are lost. The dashboard says so too. |
| `semantic_router.cli.active` | {process} | `connector` |
| `semantic_router.cli.queued` | {request} | `connector` (waiting for `MaxConcurrency`) |

The names are constants on `SemanticRouterDiagnostics`, except `semantic_router.state.dropped_writes`, which the SQLite state store (`SemanticRouter.Storage.Sqlite`) records on the same meter. A Prometheus scrape of the router (with `app.MapPrometheusScrapingEndpoint()` from `OpenTelemetry.Exporter.Prometheus.AspNetCore`):

```yaml
scrape_configs:
  - job_name: semantic-router
    metrics_path: /metrics
    static_configs:
      - targets: [ "localhost:5080" ]
```

Without OpenTelemetry: `dotnet-counters monitor -n semantic-router --counters SemanticRouter`.

## Request ids, logs and export

- **Dropped features.** When the serving backend could not carry part of the request (a hosted tool, a reasoning request, `logprobs`, …), the response has `X-Prompt-Route-Dropped`, the activity entry has `dropped`, and the attempt span has `semantic_router.dropped`. The list is in [What survives translation](providers.md#what-survives-translation).
- **Connector notes.** What a connector wants on the record about the attempt it served, such as `tool call repaired` or `malformed tool call: …` for prompted tool calling, goes to the activity field `notes` and the attempt span tag `semantic_router.notes`.
- **Prompted tool calls.** When a CLI connector read tool calls out of the model's text (`ToolCalling: Prompted`), the activity entry has `promptedToolCalls` (how many), the export has the column `prompted_tool_calls`, the attempt span has `semantic_router.prompted_tool_calls`, and the dashboard's Activity page can show only those requests (`prompted calls`). Native tool calls are not counted. The entry's notes say when calls in the text were kept as text (copied from a tool result, or with `PromptedToolParsing: Strict` not at the end of the reply or without the run's label) and, with `Lenient`, when strict parsing would have kept them as text ([Forged tool calls](providers.md#forged-tool-calls)).
- **Timings after a failover.** When a model fails and the next one answers, the activity entry's `timeToFirstByteMilliseconds` and `totalMilliseconds` are what the client waited, failed attempts included, and so is `semantic_router.time_to_first_byte`. The serving model's own times, from when it was asked, are `attemptTimeToFirstByteMilliseconds` and `attemptMilliseconds` (export columns `attempt_time_to_first_byte_ms` and `attempt_ms`) and `semantic_router.attempt.time_to_first_byte`. Per-model and per-connector figures use the serving model's own times: the typical time to first byte that `Fastest` and `Balanced` rank by, and the dashboard's Endpoints and latency figures, so a model that rescues a request is not charged for the one that failed first. Without a failover the two agree.
- **Request ids.** Every routed response carries `X-Prompt-Route-Id`, the id of its activity entry. The entry also stores the W3C `traceId`, so a request a user reports can be found in the dashboard, the export and the tracing backend.
- **Log scope.** Log lines written while a request is routed carry the scope `RouteRequestId`. Turn on scopes in your logging provider (e.g. `"Console": { "IncludeScopes": true }`).
- **Export.** `GET {dashboard}/api/activity/export?format=csv|jsonl&since=…&until=…` downloads activity (default: the last 7 days), with tokens, cost, timings and trace ids. Prompt previews are left out unless you add `prompts=true`.
- **Time series.** `GET {dashboard}/api/activity/series?window=1h|24h|7d|30d` returns requests, served and failed counts, tier and model mix, and p50/p95 latency per bucket, plus p50/p95/p99 for the window. The dashboard's **Trends** card draws it. Percentiles are nearest-rank over served requests' end-to-end time, so they match what you compute from the export.

Both read the activity store. With `UseSqliteState` they cover everything kept on disk (30 days by default); otherwise they cover the last 500 requests.
