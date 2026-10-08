# Architecture

How a request moves through the router, how configuration reloads without dropping requests, and where to plug in your own code. For setup and options see the [README](../README.md), [providers.md](providers.md) and [dashboard.md](dashboard.md).

## Projects

| Project | Depends on | What it holds |
|---|---|---|
| `SemanticRouter.Core` | nothing | Vector math, token estimator, complexity heuristics, the route index, `DefaultRouteSelector`, `DefaultModelSelector`, `ModelHealthTracker`, `PromptRouter`, and the option types (`SemanticRouterOptions`, `RouteDefinition`, `ModelDefinition`, `TierDefinition`). Zero package references, AOT-compatible. |
| `SemanticRouter.AspNetCore` | Core, ASP.NET Core | Everything that touches HTTP and processes: the middleware and dispatcher, both wire protocols, connectors, the runtime and configuration store, the dashboard and its embedded assets. |
| `SemanticRouter.Host` | AspNetCore | The `semantic-router` .NET tool and container: `run`, `init` (detects local servers and CLIs), `doctor` (`RouterDoctor`), the embedded neutral template and per-OS configuration directory. See [host.md](host.md). |
| `SemanticRouter.Storage.Sqlite` | AspNetCore, `Microsoft.Data.Sqlite` | Durable state in one SQLite file: cooldowns, tool-call affinity, activity, configuration history (`UseSqliteState`). |
| `SemanticRouter.Embeddings.Onnx` | AspNetCore, `Microsoft.ML.OnnxRuntime`, `Microsoft.ML.Tokenizers` | In-process sentence embeddings (`Embeddings:Provider = Onnx`): pinned, checksum-verified model downloads, and one loaded model shared across configuration reloads (`AddOnnxEmbeddings`, `UseOnnxEmbeddings`). |
| `SemanticRouter.Connectors.Copilot` | AspNetCore, `GitHub.Copilot.SDK` | The `Copilot` connector type on GitHub's Copilot SDK (`AddCopilotConnector`): one runtime process shared across configuration reloads, a fresh session per turn, native tool calls returned to the client. |
| `samples/RoutingApiSample` | AspNetCore | A host with an annotated `appsettings.json`: Annotate and Dispatch modes, dashboard, Anthropic endpoints. |
| `samples/LmStudioClaudePoc` | AspNetCore | The LM Studio plus Claude Code personal setup, with a preflight and a self-test. |
| `tests/SemanticRouter.Tests` | all | 450+ tests; no network, model or vendor CLI needed. Run on Linux, Windows and macOS in CI. |
| `tests/SemanticRouter.FakeCli` | nothing | A stand-in CLI (`fake-cli`) the process tests start, so they run on every OS. |

## One request, end to end

```mermaid
flowchart TD
    C1[Claude Code · Anthropic SDKs<br/>POST /v1/messages] --> M
    C2[OpenAI SDKs · Codex · chat UIs<br/>POST /v1/chat/completions] --> M
    M[PromptRoutingMiddleware<br/>path prefix picks the protocol] --> P[Parse into ChatCompletionRequest<br/>turns · tools · images · limits]
    P --> A[Map model name through ModelAliases<br/>auto · auto:* · catalog name]
    A --> Q{Tool results from a<br/>known tool call?}
    Q -- "yes" --> AF[Affinity: try the model that<br/>issued the call first]
    Q -- "no" --> CL
    AF --> SEL
    subgraph Core [SemanticRouter.Core]
        CL[Classify latest human message<br/>complexity · embedding · routes] --> SEL[Rank models<br/>tier · capabilities · local-only<br/>context window · cost · cooldown]
    end
    SEL --> MODE{Mode}
    MODE -- "Annotate" --> N[Next endpoint reads<br/>context.GetPromptRouting]
    MODE -- "Dispatch" --> D[PromptDispatcher<br/>try candidates in order]
    D --> H[HTTP connector<br/>OpenAI-compatible or Anthropic]
    D --> L[CLI connector<br/>Claude Code · Copilot · Codex · custom]
    H --> W[Response written in the client's protocol<br/>JSON or SSE · tool calls · usage]
    L --> W
```

1. **Intercept.** `PromptRoutingMiddleware` handles `POST` requests with a JSON body on the configured path prefixes. `Middleware:PathPrefixes` carries OpenAI bodies, `Middleware:AnthropicPathPrefixes` (default `/v1/messages`) carries Anthropic bodies; `/v1/messages/count_tokens` is excluded and served by its own endpoint. The body is read once (up to `MaxRequestBodyBytes`) and handed on as a fresh stream.
2. **Parse.** `IPromptExtractor` finds the text to classify: the latest `user` message that is not only tool results, with injected `<system-reminder>` blocks removed. `ChatCompletionRequest` parses the same body into a protocol-neutral model: structured turns (text, image, tool call, tool result), tools, tool choice, sampling parameters, an input-size estimate, and whether the request continues a tool-calling turn.
3. **Map the model name.** Aliases are globs (`*haiku*` to `auto:cheapest`). A catalog model name or any `auto…` value is never remapped; a name that matches nothing routes as `auto`.
4. **Pin tool turns.** A request that carries tool results asks `IToolCallAffinity` which model issued those calls. If that model is enabled and not cooling down, it goes first (`RoutingReason.ConversationAffinity`), with the normally ranked models behind it as fallbacks.
5. **Classify.** `SemanticPromptClassifier`: complexity heuristics, then (unless the complexity short-circuit applies) one embedding call scored against the route index, then `DefaultRouteSelector` turns the scores into a required tier, capabilities and a local-only flag. Requirements only ever go up.
6. **Rank.** `DefaultModelSelector` filters by enabled, available, local (if required), capabilities (the request adds `tools` and `vision` itself), and context window, then orders by strategy. Models below the required tier follow when `AllowBelowRequiredTier` is on.
7. **Dispatch or annotate.** In Annotate mode the plan is attached to the request and the next endpoint decides. In Dispatch mode `PromptDispatcher` tries up to `MaxAttempts` candidates. A failure before any output cools the model down and moves on; once output has started the connector owns the response. An account-wide limit cools down every model on that connector.
8. **Write the reply in the client's protocol.** See the next section.
9. **Record.** A `RoutingActivityEntry` (decision, then outcome, timing, tokens and cost) goes into the activity log behind the dashboard's Activity page; the reported usage goes into the `IUsageLedger` that budgets read; spans, metrics and routing headers (including `X-Prompt-Route-Id`) are emitted. The finished entry is also pushed to the Live page over `api/events`. When memory sources are configured, the body was checked against the `MemoryIndex` while parsing, and the notes it carried are counted against every model in `SentTo` by the `MemoryExposureLog` ([memory.md](memory.md)). See [observability.md](observability.md).

## Two protocols, any backend

A connector either passes the client's body through or translates it.

| Client speaks | Backend speaks | What happens |
|---|---|---|
| OpenAI | OpenAI-compatible | Body forwarded with only `model` rewritten; response streamed back byte for byte. |
| Anthropic | Anthropic connector | Same, plus `x-api-key`, `anthropic-version` and the configured `ForwardHeaders`. |
| Anthropic | OpenAI-compatible | `OpenAIRequestBuilder` re-expresses the request (system prompt, `tool_use` to `tool_calls`, tool results to `tool` messages, images, stop sequences); `OpenAIResponseReader` reads the JSON or SSE reply and drives an `IChatResponseWriter` that writes Anthropic JSON or events. |
| OpenAI | Anthropic connector | The mirror image through `AnthropicRequestBuilder` and `AnthropicResponseReader`; consecutive same-role turns are merged because the Messages API requires alternation, and `max_tokens` gets a default. |
| either | CLI | `ChatTranscript` renders the conversation to text; the adapter builds the command line; the connector parses the CLI's output and drives the writer. With `ToolCalling: Prompted` the tools are described in the system prompt and `PromptedToolCalls` turns fenced `tool_call` blocks in the reply into structured calls. |

`IChatResponseWriter` is the single seam for output: `WriteTextAsync`, `StartToolCallAsync` / `WriteToolCallArgumentsAsync` / `EndToolCallAsync`, `CompleteAsync`. `OpenAIResponseWriter` and `AnthropicResponseWriter` implement the documented JSON and SSE shapes, so a new front door is a parser, a request builder and a writer.

What does not cross protocols is listed under *Known limitations* in the README (extended thinking, cache hints, structured-output parameters).

## Configuration and hot reload

```mermaid
flowchart LR
    AS[appsettings.json · env · user-secrets · code] --> BL[Baseline<br/>RouterConfiguration]
    FS[semantic-router.json<br/>IRouterConfigurationStore] --> LD{Load}
    BL --> LD
    LD -- "stored document valid, secrets inherited from baseline" --> RT
    LD -- "otherwise" --> RT
    RT[RouterRuntime snapshot<br/>classifier · router · extractor<br/>embedding provider · connectors] --> FA[DI facades<br/>IPromptRouter · IPromptClassifier<br/>IEmbeddingProvider · IPromptExtractor]
    UI[Dashboard · admin API<br/>validate · apply · save · reset] --> PV[RouterRuntimeProvider]
    PV --> RT
    PV --> FS
```

- **`RouterConfiguration`** is one document: the whole `SemanticRouter` section (routing rules, tiers, models, connectors, embeddings, middleware). Its JSON shape equals the `appsettings.json` section, so documents move between the two.
- **`RouterRuntime`** is an immutable snapshot built from a document: classifier, router, extractor, embedding provider, connector catalog, parsed path prefixes and alias list. `RouterRuntimeProvider.Apply` validates, builds a new runtime, swaps it in with one reference write, starts the route-index warm-up in the background, then disposes the old runtime. In-flight requests keep the runtime they started with.
- **DI facades** (`RuntimeRouterFacade` and friends) forward every call to the current runtime, so a service that injected `IPromptRouter` at start-up never holds a stale instance. Registering your own implementation of one of these interfaces replaces the facade and is used as-is across reloads.
- **State that survives a reload** is registered once: `IModelHealth` (cooldowns), `IToolCallAffinity`, `RoutingActivityLog`, `IRouterConfigurationHistory`, `MemoryIndex` and `MemoryExposureLog`, the metrics meter and the CLI process runner. See [Durable state](#durable-state) for what survives a restart.
- **Secrets** are masked as `***` on the way to the browser; sending the mask back keeps the stored value, and empty secrets in a stored document are filled from the baseline. Values may be references (`${env:…}`, `${secret:…}`): the runtime keeps the document as written (`Configuration`) and builds connectors and the embedding provider from a resolved copy (`Resolved`), never sent to the browser. A `SecretRedactor` built from the resolved values scrubs them from errors, relayed upstream bodies, activity and logs.

## The dashboard

`MapRouterDashboard` serves three embedded files (`index.html`, `app.css`, `app.js`) and the admin API under `{prefix}/api`. There is no build step and no dependency. The UI keeps one draft of the configuration in the browser, edits it through path-based bindings (`data-bind="Models.2.Tier"`), and talks to the same validation the router uses at start-up. Themes are token blocks keyed by `data-theme`; see [dashboard.md](dashboard.md#themes). The admin API answers loopback clients only unless that guard is turned off.

## Durable state

Out of the box everything is in memory. `SemanticRouter.Storage.Sqlite` keeps it in one SQLite file instead. The `semantic-router` tool uses it by default (`semantic-router.db` next to the configuration).

```csharp
builder.Services.AddSemanticPromptRouting(builder.Configuration.GetSection("SemanticRouter"))
    .UseConfigurationFile("semantic-router.json")
    .UseSqliteState("semantic-router.db", o => o.AffinityWindow = TimeSpan.FromHours(24));
```

| State | Stored as | Kept |
|---|---|---|
| Model cooldowns | model name plus absolute expiry, through `ICooldownStore` | Until they expire. A restart during a weekly limit keeps the model cooling down until the original time. |
| Tool-call affinity | tool-use id → model, with an in-memory cache in front | `AffinityWindow` (24 h), so a tool turn that spans a restart still goes back to the model that issued the call. |
| Activity | each entry once its request has finished; the log seeds itself on start | `ActivityMaxEntries` (10,000) and `ActivityMaxAge` (30 days). |
| Configuration history | every applied document, with source, actor and note | `HistoryMaxEntries` (200). The dashboard's JSON page diffs and rolls back; see `api/config/history`. |
| Usage | one row per served request: model, connector, route, client, tokens, cost, reference cost | `UsageRetention` (400 days), so monthly budgets survive restarts. |

History documents are stored as applied. They keep `${…}` references where the configuration uses them, and literal keys where the configuration file holds them too. With a secret store registered, typed keys never reach either.

**Failures never stop routing.** A file that cannot be opened (corrupt, wrong schema, no permission) leaves everything in memory, with one warning at start-up. An operation that fails later, for example a database locked longer than `BusyTimeout` by another process, is logged at most once a minute and the in-memory state carries on.

**Several instances.** Instances on one machine can share the file: SQLite in WAL mode serialises writers.
- Cooldowns are re-read every `ModelHealthTracker.RefreshInterval` (5 s), so an account limit one instance hits is honoured by the others within seconds.
- Affinity lookups fall through to the file, so a tool turn can continue on another instance.
- Activity ids are per instance, so a shared file mixes their entries; give each instance its own file if you want separate logs.

Do not put the file on a network share: SQLite's locking is unreliable there. For instances on different machines, implement `ICooldownStore` and `IToolCallAffinity` over a shared cache such as Redis.

## Extension points

| To… | Implement or call |
|---|---|
| Reach a provider with its own protocol | `IChatConnector`, then `.AddConnector<T>()` |
| Add a connector type that configuration can select | `IChatConnectorFactory` (and `IModelDiscovery` for the Discover button), as `SemanticRouter.Connectors.Copilot` does |
| Use another embedding backend | `IEmbeddingProvider`, then `.UseEmbeddingProvider<T>()` |
| Change how requirements are derived | `IRouteSelector`, `IComplexityAnalyzer`, or `IPromptClassifier` |
| Change how models are ranked | `IModelSelector` |
| Persist or share cooldowns | `ICooldownStore` (Core; `ModelHealthTracker` writes through to it), or replace `IModelHealth` |
| Persist or share tool-call affinity | `IToolCallAffinity` |
| Keep activity beyond the in-memory window | `IRoutingActivityStore` |
| Keep configuration history somewhere else | `IRouterConfigurationHistory` |
| Keep usage somewhere else (budgets read it) | `IUsageLedger` |
| Support another payload shape | `IPromptExtractor` |
| Keep configuration somewhere other than a file | `IRouterConfigurationStore`, then `.UseConfigurationStore<T>()` |
| Keep secrets in a vault or the OS keychain | `IRouterSecretStore`, then `.UseSecretStore<T>()` |

## Safety rules that shape the design

These are enforced in code and explained in [TERMS.md](TERMS.md): the router never reads or forwards credentials of a client or a CLI; CLI connectors answer loopback clients only and run in a fresh empty directory with their own tools off; subscription limits become cooldowns, never retries or account rotation; the admin API is loopback-only by default (remote access needs a token, and writes must come from the dashboard's own origin); and local-only routes can only be served by models marked local, including on failover.
