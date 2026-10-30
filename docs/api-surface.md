# Public API surface

The public types of the five library packages, with one line on what each is for. This page says what is meant to be public; `src/<Project>/PublicAPI.Shipped.txt` and `PublicAPI.Unshipped.txt` list every member, and the build fails when they and the code disagree ([CONTRIBUTING.md](../CONTRIBUTING.md#public-api-and-package-validation)). A pull request that adds, removes or renames a public type changes this page too: `ApiSurfaceDocumentationTests` checks that every public type is here and nothing else is.

Setup extension classes are in `Microsoft.Extensions.DependencyInjection` and `Microsoft.AspNetCore.Builder`, everything else in the package's own namespace ([Namespaces](architecture.md#namespaces)). The `semantic-router` tool and the desktop app have no public API. For how the pieces fit together, see [Architecture](architecture.md); for the extension points by task, see [Extensibility](../README.md#extensibility).

## SemanticRouter.Core

Routing without ASP.NET Core or any package dependency: classify a prompt, derive what a model must be able to do, and rank the catalog. Namespace `SemanticRouter`.

### The routing pipeline

- `IPromptRouter`: routes a prompt to an ordered list of catalog models.
- `PromptRouter`: the default `IPromptRouter`: classifies, ranks the catalog, and skips models in cooldown or unreliable for the work.
- `PromptRouterDependencies`: the collaborators of a `PromptRouter` that have a default.
- `IPromptClassifier`: classifies a prompt into model requirements (tier, capabilities, locality).
- `SemanticPromptClassifier`: the default `IPromptClassifier`: complexity heuristics, then embedding similarity against the route index, then an `IRouteSelector`.
- `SemanticPromptClassifierDependencies`: the collaborators of a `SemanticPromptClassifier` that have a default.
- `IRouteSelector`: turns similarity scores and complexity signals into model requirements.
- `DefaultRouteSelector`: the default routing policy: the minimum tier and the capabilities a prompt needs.
- `IModelSelector`: orders the catalog's models for a request.
- `DefaultModelSelector`: the default `IModelSelector`.
- `DefaultModelSelectorDependencies`: the optional collaborators of a `DefaultModelSelector`: what it learns about the models while the router runs.
- `IComplexityAnalyzer`: scores how demanding a prompt is, independent of its intent.
- `HeuristicComplexityAnalyzer`: the default `IComplexityAnalyzer`: one pass over the prompt for length, code, structure and reasoning signals.
- `IEmbeddingProvider`: produces vector embeddings for text.
- `HashingEmbeddingProvider`: a deterministic in-memory `IEmbeddingProvider` (feature hashing): lexical overlap only, for tests and trying the router out, not production.
- `SemanticRouteIndex`: an immutable, pre-normalized embedding matrix for a set of routes.

### What the router knows about models

- `IModelHealth`: tracks which models are temporarily unavailable.
- `ModelHealthTracker`: `IModelHealth` with time-based cooldowns.
- `ModelHealthTrackerDependencies`: the collaborators of a `ModelHealthTracker` that have a default.
- `ICooldownStore`: keeps cooldowns where they outlive the process or are shared between instances.
- `IModelLatency`: how quickly models start answering lately, for the `Fastest` and `Balanced` strategies.
- `IModelReliability`: whether a model should be offered for a kind of work (a route, or work that needs tools).
- `IModelResidency`: which local models are in memory now, so a loaded one can be preferred.
- `IModelCacheShare`: how much of a model's input lately hits the provider's prompt cache, for cost estimates.
- `ModelFailureKind`: why a model attempt failed.

### Configuration

- `SemanticRouterOptions`: the routing configuration: routes, tiers, the model catalog and selection.
- `RouteDefinition`: a named set of example utterances and the tier a match requires.
- `TierDefinition`: a named rung on the capability ladder.
- `ModelDefinition`: a model the router may dispatch to: its connector, capabilities and prices.
- `ModelSelectionOptions`: model selection and failover settings.
- `SelectionStrategy`: how eligible models are ordered once the requirements are known.
- `RoutingProfile`: a named routing policy: which models a context may use, how aggressive routing is, whether prompts stay local.
- `TimeWindow`: a daily time window such as `08:00-18:00`, also across midnight. Positional and frozen.
- `ComplexityHeuristicsOptions`: tuning knobs for `HeuristicComplexityAnalyzer`.
- `ComplexityTierRule`: raises the required tier once a prompt's complexity score reaches a minimum.
- `ClassificationCacheOptions`: settings for the classification cache.
- `RouteScoringStrategy`: how a route's utterance similarities combine into one route score.
- `ThresholdTuningOptions`: limits for `ThresholdTuner`.

### Requests and results

- `PromptRoutingRequest`: a request to route: the prompt and facts about the surrounding request.
- `RoutingPlan`: the routing outcome: the requirements and the models to try, in order.
- `ModelCandidate`: a model eligible to serve a request, in the order it should be tried.
- `ModelRequirements`: what a model must satisfy to serve a request.
- `PromptClassificationResult`: the requirements a prompt needs, with the evidence behind them.
- `RouteDecision`: what an `IRouteSelector` decided a model must be able to do, and why.
- `RoutingReason`: how the required tier was decided.
- `RouteScore`: the similarity between a prompt and one route. Positional and frozen.
- `ComplexityAnalysis`: a prompt's structural and lexical signals and their combined score.
- `WarmCache`: a prompt cache a conversation has on a model: how many tokens, and until when.
- `PromptCacheShare`: the share of a model's input read from and written to the provider's prompt cache.
- `ClassificationCacheStatistics`: hits, misses and size of the classification cache.
- `RouteIndexStatistics`: the size of a `SemanticPromptClassifier`'s route index.

### Evaluation, calibration and tuning

- `EvaluationCase`: a labelled prompt: the tier (and optionally the route) it should get.
- `EvaluationSet`: reads and writes evaluation sets (a JSON array, `{"cases": [...]}`, or JSON Lines).
- `RoutingEvaluator`: runs labelled prompts through classification without calling a model, and sweeps thresholds.
- `PreparedEvaluation`: an evaluation set embedded once, to score under many thresholds.
- `EvaluationReport`: how well a configuration routes an evaluation set.
- `EvaluationResult`: what one labelled prompt was routed to.
- `TierAccuracy`: accuracy for the prompts labelled with one tier.
- `CalibrationReport`: the result of sweeping thresholds over an evaluation set.
- `CalibrationPoint`: one threshold tried during calibration.
- `RouteThresholdRecommendation`: a per-route threshold that calibration suggests.
- `ThresholdTuner`: moves thresholds toward calibration's recommendations in bounded steps, only when that is safe.
- `ThresholdTuning`: the steps the tuner found, and how routing compares before and after.
- `ThresholdStep`: one bounded threshold step the tuner would take.
- `TuningScore`: how one set of labelled prompts routes.
- `RouteHealthAnalyzer`: checks the routes' utterances for too few examples, duplicates, overlap and routes too close together.
- `RouteHealthReport`: the findings of `RouteHealthAnalyzer`.
- `RouteHealthFinding`: one problem found in the routes.
- `RouteHealthKind`: a kind of problem in a route's utterances.
- `RouteOverlap`: how close two routes are.

### Utilities

- `VectorMath`: allocation-free, SIMD-accelerated vector primitives.
- `TokenEstimator`: a fast, tokenizer-free estimate of a text's token count.
- `Glob`: case-insensitive `*` globs, as model aliases and profiles use them.
- `QuotaResetParser`: reads when a usage limit ends from a provider's or CLI's message, so the cooldown lasts until then.
- `RouterTelemetry`: the `ActivitySource` name for the router's spans, which never carry prompt text.

## SemanticRouter.AspNetCore

The router as ASP.NET Core middleware: connectors, the three chat protocols, the dashboard and its admin API, usage and budgets, and the durable-state abstractions. Namespace `SemanticRouter.AspNetCore`.

### Setup

- `SemanticRouterServiceCollectionExtensions`: `AddSemanticPromptRouting`, which registers the router (`Microsoft.Extensions.DependencyInjection`).
- `SemanticRouterBuilder`: the fluent customization `AddSemanticPromptRouting` returns: connectors, stores, embeddings.
- `PromptRouterApplicationBuilderExtensions`: `UsePromptRouter`, which adds routing to the request pipeline (`Microsoft.AspNetCore.Builder`).
- `RouterDashboardEndpoints`: `MapRouterDashboard`, which maps the dashboard and its admin API (`Microsoft.AspNetCore.Builder`).
- `RouterForwardedHeadersExtensions`: forwarded headers from known reverse proxies, noting which requests came through one (`Microsoft.AspNetCore.Builder`).

### Middleware

- `PromptRouterMiddlewareOptions`: options for `UsePromptRouter`.
- `PromptRouterMode`: what the middleware does once a prompt is classified: annotate the request, or dispatch it to a model.
- `PromptMessageSelection`: which chat messages make up the routed prompt.
- `IPromptRoutingFeature`: the request feature the middleware sets once a request is routed.
- `PromptRoutingHttpContextExtensions`: accessors for `IPromptRoutingFeature` on `HttpContext`.

### Extension points

- `IChatConnector`: reaches one provider; register an implementation with `AddConnector<T>`.
- `IChatConnectorFactory`: creates the connectors of a type another package provides, selected by `"Type"` in configuration.
- `IModelDiscovery`: lists a provider's models, for the dashboard's Discover button and `doctor`.
- `IEmbeddingProviderFactory`: builds an embedding provider for a `"Provider"` name another package provides.
- `EmbeddingModelChoice`: a model an `IEmbeddingProviderFactory` can provide by name.
- `IPromptExtractor`: pulls the text to classify out of an incoming JSON request.
- `IRouterConfigurationStore`: where the configuration document is kept between restarts.
- `IRouterConfigurationHistory`: every configuration document applied, for diffs and rollback.
- `IRouterSecretStore`: keeps secret values (API keys, header values, gateway keys) outside the configuration document.
- `IRoutingActivityStore`: keeps routing activity beyond the in-memory window and across restarts.
- `IRoutingFeedbackStore`: keeps feedback across restarts.
- `IToolCallAffinity`: remembers which model issued each tool call, so the tool results go back to it.
- `IWarmCacheStore`: remembers the prompt caches a conversation's turns left on models.
- `IUsageLedger`: records what every served request used, for the dashboard and budgets.
- `IAuditLog`: an append-only record of dashboard and admin API changes, sign-ins and sign-outs.
- `ITuningStore`: keeps learned changes and tuning state across restarts.
- `IStateValueStore`: small named values that should outlast a restart.
- `IEvaluationSetStore`: where the dashboard keeps its evaluation set.
- `IMemorySource`: a store of notes the dashboard maps and the router watches for in requests.
- `IRouterServiceControl`: the process the router runs in: where it listens, restarting, stopping, running as a service.
- `IRouterSetupDetector`: finds the model servers and CLIs on the machine, for the first-run setup.

### Default implementations

- `JsonFileRouterConfigurationStore`: keeps the configuration in a JSON file.
- `RoutingActivityLog`: a bounded in-memory log of recent routing decisions, optionally backed by an `IRoutingActivityStore`.
- `JsonPromptExtractor`: the default `IPromptExtractor`, for the common LLM request shapes.

### The configuration document

- `RouterConfiguration`: everything the router can be configured with, as one document.
- `RouterConfigurationSource`: where the active configuration came from.
- `RouterConfigurationVersion`: one applied configuration document.
- `SecretReference`: references to secrets (`${env:…}`, the secret store) inside configuration values.

### Connectors

- `SemanticRouterConnectorsOptions`: the connector catalog (`SemanticRouter:Connectors`).
- `ConnectorOptions`: one connector: a way of reaching one or more models.
- `ConnectorTypes`: the names of the connector types the router builds itself.
- `CliPreset`: ready-made settings for well-known CLIs.
- `CliPromptInput`: how a CLI receives the prompt.
- `CliToolCalling`: how a CLI connector handles requests that offer tools.
- `PromptedToolParsing`: how strictly a prompted-tool-calling reply is read for tool calls.
- `PromptCaching`: whether requests built for an Anthropic backend are marked for prompt caching.
- `ModelLoading`: how a model server makes its models available.
- `ConnectorRequest`: what a connector is asked to do: serve a chat request with a model.
- `ConnectorResult`: the result of `IChatConnector.SendAsync`.
- `ConnectorOutcome`: the outcome of a connector attempt.
- `ConnectorProbe`: checks an HTTP connector without calling a model (`GET /v1/models`).
- `ConnectorProbeResult`: the outcome of a probe.
- `ConnectorProbeFailure`: why a probe failed.
- `DiscoveredModel`: one model a connector's server reports, with the details it gives.
- `DiscoveredModelKind`: what kind of model a server reports.
- `CliExecutableLocator`: finds a CLI's executable on the absolute PATH entries.
- `CliExecutableLookup`: the result of `CliExecutableLocator.Find`.

### Embeddings

- `EmbeddingProviderOptions`: embedding provider configuration.
- `EmbeddingProviderKinds`: the names of the embedding providers the router builds itself.
- `EmbeddingPooling`: how token vectors become one sentence vector, for in-process models.
- `OpenAICompatibleEmbeddingProvider`: an `IEmbeddingProvider` for any server with the OpenAI `POST /v1/embeddings` contract.

### Routing options

- `ModelAliases`: model names clients can send to let the router choose.
- `VirtualModel`: a model name that stands for a profile and a strategy.
- `ProfileRule`: picks a profile from what the request looks like.
- `LocalOnlySignals`: content signals that force a request onto local models.
- `CalloutOptions`: inline tags (`@best`, `@local`, a model name) that override the router's choice for one message.
- `ConversationRoutingOptions`: conversation-aware routing.
- `RetryEscalationOptions`: a prompt repeated soon after its answer goes one tier up.
- `HousekeepingOptions`: a client's side requests: how they are recognised and the tier they get.
- `RateLimitOptions`: one client's rate limit over a sliding minute.
- `NetworkOptions`: the router's own outbound connections, and who on this machine counts as local.

### The chat protocol model

- `ChatCompletionRequest`: a chat request in a protocol-neutral shape, which connectors re-express for their backend.
- `ChatProtocol`: the wire format of a request or response: OpenAI chat completions, Anthropic Messages or OpenAI Responses.
- `ChatTurn`: one message of the conversation, with its structured content.
- `ChatMessage`: one chat message flattened to text.
- `ContentBlock`: one part of a chat message.
- `TextBlock`: plain text.
- `ImageBlock`: an image, inline (base64) or by URL.
- `ThinkingBlock`: the model's visible reasoning.
- `ToolUseBlock`: a tool call issued by the assistant.
- `ToolResultBlock`: the result of a tool call, sent by the client.
- `ToolDefinition`: a tool the client offers the model.
- `ToolKind`: what kind of tool a client offers.
- `ToolChoice`: the request's tool choice.
- `ToolChoiceMode`: how the model may use the offered tools.
- `ReasoningRequest`: a request for the model to reason before answering.
- `ResponseFormat`: structured output (`response_format`, or the Responses API's `text.format`).
- `ResponseFormatKind`: what shape the reply must have. It has no zero member, as its documentation explains.
- `ChatStopReason`: why a reply ended.
- `ChatUsage`: token usage reported by a backend.

### Usage, budgets and prices

- `UsageOptions`: usage accounting and budgets (`SemanticRouter:Usage`).
- `BudgetDefinition`: a spending limit for one model or one connector.
- `BudgetPeriod`: the period a budget covers.
- `UsageRecord`: what one served request used.
- `UsageTotals`: usage added up, including what the subscriptions' requests would have cost at API prices.
- `UsageGroup`: one group of a usage summary.
- `UsageGrouping`: how a summary groups usage.
- `UsageModelTotals`: one model's usage within one group, at the prices it was recorded at.
- `UsagePricing`: usage at the catalog's current prices.
- `ModelPrice`: a cloud model's list prices, in USD per million tokens.
- `ModelPriceList`: cloud providers' list prices, from OpenRouter's public model list.
- `ModelPriceMatch`: a price the list has for a catalog model.
- `ContextCostOptions`: context cost analysis and its thresholds (`SemanticRouter:Usage:Context`).
- `ContextBreakdown`: what one request's input is made of, in estimated tokens, and the signs that it carries more context than it needs.

### Activity, state and feedback

- `RoutingActivityEntry`: one routed request, for the dashboard's activity view.
- `ActivityPurgeResult`: what a purge of activity did, including whether it is gone from the store's files.
- `StateOptions`: what the router keeps about requests, and for how long (`SemanticRouter:State`).
- `StateRetentionOptions`: how long the state database keeps records.
- `PromptStorage`: how much of each prompt the activity log keeps.
- `AuditEntry`: one action taken through the dashboard or admin API.
- `RoutingFeedback`: one piece of feedback about a routing decision, with the tier it implies.
- `FeedbackKind`: a sign, noticed afterwards, that a routing decision was not good enough.
- `FeedbackOptions`: which feedback signs are recorded.
- `ReliabilityOptions`: when a model that draws far more feedback than its peers on some work stops being offered for it.

### The judge and automatic tuning

- `JudgeOptions`: the judge: a share of the answers is graded by another model afterwards.
- `JudgeAgreementReport`: how often the judge's grades agree with the evaluation set's hand labels.
- `JudgeAgreementPair`: a graded answer whose prompt is also labelled by hand.
- `TuningOptions`: automatic tuning of thresholds and utterances, in small, safe, undoable steps.
- `TuningMode`: what automatic tuning does with the changes it finds.
- `TuningChange`: one change automatic tuning made or suggests, with why and when.
- `TuningChangeKind`: what a learned change touches.
- `TuningChangeState`: where a learned change stands.
- `TuningRun`: what one tuning run did.
- `TuningState`: automatic tuning's own state: paused or not, and the last run.

### The memory map

- `MemoryOptions`: the memory sources the dashboard maps, and whether the router records which notes reach which model.
- `MemorySourceOptions`: one memory source.
- `MemorySourceType`: the built-in memory sources.
- `MemoryDocument`: a note as a source returns it.
- `MemoryReference`: a note found in a routed request.
- `MemoryShareKind`: how a note showed up in a request.

### Dashboard, notifications and the service

- `RouterDashboardOptions`: options for `MapRouterDashboard`.
- `RouterDashboardRole`: what a dashboard caller may do.
- `NotificationOptions`: notifications, sent only when a person needs to decide something.
- `NoticeKind`: something a person needs to decide.
- `RouterNotice`: one thing that needs a decision.
- `WebhookOptions`: one webhook.
- `WebhookFormat`: how a webhook message is written.
- `ClientAddress`: a base address clients point at.
- `SetupDetection`: something `IRouterSetupDetector` found that the router could use.
- `RouterServiceDescription`: how the router runs, for the dashboard's Service page.
- `RouterServiceActions`: what the dashboard can ask of `IRouterServiceControl`.
- `RouterServiceOutcome`: how a service action ended.
- `RouterListenDescription`: where the router listens.
- `RouterListenChange`: where the router should listen from its next start.
- `RouterBackgroundService`: the background service this machine's service manager runs.

### Machine policy

- `MachinePolicy`: settings an administrator sets for the whole machine, which the user's configuration cannot override.
- `MachinePolicyLoader`: reads the `MachinePolicy` from where only administrators can write it.
- `DashboardPolicy`: how the machine policy limits the dashboard.
- `FeaturePolicy`: features the machine policy can switch off.
- `UpdatePolicy`: desktop app updates.
- `UpdateMode`: where the desktop app's updates come from, if anywhere.
- `UpdateApply`: when the desktop app applies an update it found.

### Telemetry

- `SemanticRouterDiagnostics`: the meter and `ActivitySource` names; no instrument or span carries prompt text.

## SemanticRouter.Embeddings.Onnx

In-process sentence embeddings with ONNX Runtime, as the `Onnx` embedding provider. Namespace `SemanticRouter.Embeddings.Onnx`.

- `OnnxEmbeddingsBuilderExtensions`: `UseOnnxEmbeddings` and `AddOnnxEmbeddings` (`Microsoft.Extensions.DependencyInjection`).
- `OnnxEmbeddings`: the provider name this package adds, for `"Provider"`.
- `OnnxEmbeddingOptions`: options for `AddOnnxEmbeddings`.
- `OnnxEmbeddingProvider`: sentence embeddings from a BERT-style ONNX model, run in-process.
- `OnnxEmbeddingModels`: the models the package can download by name.
- `OnnxModelInfo`: a downloadable model, pinned to one revision and checksum.
- `OnnxModelDownloader`: downloads a known model's files once, checks their SHA-256 and writes them atomically.
- `OnnxRuntimeEnvironment`: ONNX Runtime's process-wide settings.

## SemanticRouter.Storage.Sqlite

Durable state in one SQLite file: cooldowns, affinity, warm caches, activity, history, usage and more. Namespace `SemanticRouter.Storage.Sqlite`. The stores themselves are internal; the package is used through its extension method.

- `SqliteStateBuilderExtensions`: `UseSqliteState`, which registers the SQLite-backed stores (`Microsoft.Extensions.DependencyInjection`).
- `SqliteStateOptions`: options for `UseSqliteState`.

## SemanticRouter.Connectors.Copilot

A connector for GitHub Copilot through its SDK, as the `Copilot` connector type. Namespace `SemanticRouter.Connectors.Copilot`. The connector itself is internal.

- `CopilotConnectorBuilderExtensions`: `AddCopilotConnector`, which registers the connector type (`Microsoft.Extensions.DependencyInjection`).
- `CopilotConnector`: the connector type name this package adds, for `"Type"`.
