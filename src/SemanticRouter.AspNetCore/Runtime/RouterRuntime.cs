using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SemanticRouter.AspNetCore;

/// <summary>Where the active configuration came from.</summary>
public enum RouterConfigurationSource
{
    /// <summary>The application's configuration (<c>appsettings.json</c>, environment, code).</summary>
    Configuration = 0,

    /// <summary>The configuration store (a document saved from the dashboard or API).</summary>
    Store = 1,

    /// <summary>Applied at runtime without being persisted.</summary>
    Memory = 2,
}

/// <summary>Marks the DI facades the router registers, so user registrations can be told apart from them.</summary>
internal interface IRuntimeFacade;

/// <summary>Everything built from one configuration document. Immutable; replaced as a whole on reload.</summary>
internal sealed class RouterRuntime : IDisposable
{
    /// <summary>The document as applied: secrets may be references (<c>${env:…}</c>) and are never resolved here.</summary>
    public required RouterConfiguration Configuration { get; init; }

    /// <summary>
    /// <see cref="Configuration"/> with every secret reference resolved. Connectors and the embedding provider are
    /// built from it; never send it to a client.
    /// </summary>
    public required RouterConfiguration Resolved { get; init; }

    /// <summary>References that could not be resolved, for the dashboard.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>What applying this document did besides swapping the runtime (secrets moved, plain-text secrets saved).</summary>
    public IReadOnlyList<string> ApplyNotes { get; init; } = [];

    /// <summary>Removes the resolved secret values from text that leaves the router (errors, logs, activity).</summary>
    public required SecretRedactor Redactor { get; init; }

    /// <summary>Resolved gateway keys: key value to client name.</summary>
    public required IReadOnlyDictionary<string, string> GatewayKeys { get; init; }

    public required int Version { get; init; }

    /// <summary>The configuration history entry recorded when this runtime was applied, when there is one.</summary>
    public long? HistoryVersion { get; set; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required RouterConfigurationSource Source { get; init; }

    public required IEmbeddingProvider EmbeddingProvider { get; init; }

    public required IPromptClassifier Classifier { get; init; }

    public required IPromptRouter Router { get; init; }

    public required IPromptExtractor Extractor { get; init; }

    public required ConnectorCatalog Connectors { get; init; }

    public required PathString[] OpenAIPrefixes { get; init; }

    public required PathString[] AnthropicPrefixes { get; init; }

    public required PathString[] ResponsesPrefixes { get; init; }

    public required IReadOnlyList<(string Pattern, string Target)> ModelAliases { get; init; }

    /// <summary>Whether the route index has been embedded (set by the warm-up).</summary>
    public volatile bool WarmedUp;

    /// <summary>The last warm-up failure, if any.</summary>
    public volatile string? WarmUpError;

    public void Dispose() => Connectors.Dispose();
}

/// <summary>
/// Owns the current <see cref="RouterRuntime"/>: builds it from the application's configuration (overlaid with the
/// configuration store when one is registered), validates and applies new documents atomically, and warms up each
/// new classifier in the background. Register as a singleton.
/// </summary>
internal sealed partial class RouterRuntimeProvider : IDisposable
{
    private readonly IServiceProvider _services;
    private readonly IRouterConfigurationStore? _store;
    private readonly IRouterSecretStore? _secrets;
    private readonly IRouterConfigurationHistory? _history;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private RouterRuntime? _current;
    private RouterConfiguration? _baseline;
    private int _version;

    public RouterRuntimeProvider(
        IServiceProvider services,
        ILogger<RouterRuntimeProvider> logger,
        IRouterConfigurationStore? store = null,
        IRouterSecretStore? secrets = null,
        IRouterConfigurationHistory? history = null)
    {
        _services = services;
        _store = store;
        _secrets = secrets;
        _history = history;
        _logger = logger;
    }

    /// <summary>Raised after a new runtime replaced the previous one.</summary>
    public event Action<RouterRuntime>? Changed;

    /// <summary>The store, when configured.</summary>
    public IRouterConfigurationStore? Store => _store;

    /// <summary>The secret store, when configured.</summary>
    public IRouterSecretStore? Secrets => _secrets;

    /// <summary>The configuration history, when configured.</summary>
    public IRouterConfigurationHistory? History => _history;

    /// <summary>The active runtime, built on first access.</summary>
    public RouterRuntime Current
    {
        get
        {
            RouterRuntime? current = Volatile.Read(ref _current);
            if (current is not null)
            {
                return current;
            }

            lock (_gate)
            {
                if (_current is null)
                {
                    (RouterConfiguration document, RouterConfigurationSource source) = LoadInitial();
                    _current = Build(document, source);
                    RecordStartup(document, source);
                    ScheduleWarmUp(_current);
                }

                return _current;
            }
        }
    }

    /// <summary>The configuration as bound from the application (without store overrides). Secrets are not masked.</summary>
    public RouterConfiguration Baseline
    {
        get
        {
            if (_baseline is null)
            {
                lock (_gate)
                {
                    _baseline ??= RouterConfiguration.Compose(
                        _services.GetRequiredService<IOptions<SemanticRouterOptions>>().Value,
                        new Dictionary<string, ConnectorOptions>(_services.GetRequiredService<IOptions<SemanticRouterConnectorsOptions>>().Value.Connectors, StringComparer.OrdinalIgnoreCase),
                        _services.GetRequiredService<IOptions<EmbeddingProviderOptions>>().Value,
                        _services.GetRequiredService<IOptions<PromptRouterMiddlewareOptions>>().Value,
                        _services.GetService<IOptions<UsageOptions>>()?.Value ?? new UsageOptions(),
                        _services.GetService<IOptions<MemoryOptions>>()?.Value,
                        _services.GetService<IOptions<TuningOptions>>()?.Value,
                        _services.GetService<IOptions<NotificationOptions>>()?.Value,
                        _services.GetService<IOptions<StateOptions>>()?.Value);
                }
            }

            return _baseline;
        }
    }

    /// <summary>
    /// The rules every configuration is held to: the machine policy, with the user's offline mode
    /// (<see cref="NetworkOptions.Offline"/>); <see cref="MachinePolicy.None"/> without either.
    /// </summary>
    public MachinePolicy Policy => _policy ??= MachinePolicy.Effective(_services.GetService<MachinePolicy>(), NetworkOptions.Of(_services));

    private MachinePolicy? _policy;

    /// <summary>
    /// Validation errors for a document that would replace the running one: <see cref="Validate"/>, and what it would
    /// have to give up to keep within the machine policy. Empty when it could be applied.
    /// </summary>
    public IReadOnlyList<string> ValidateChange(RouterConfiguration configuration) =>
        [.. Validate(configuration), .. Policy.Violations(configuration).Select(v => "Not allowed: " + v)];

    /// <summary>Validation errors for a document; empty when it could be applied.</summary>
    public static IReadOnlyList<string> Validate(RouterConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return [.. configuration.GetDocumentValidationErrors(), .. configuration.MaskedSecretErrors()];
    }

    /// <summary>
    /// Validates <paramref name="configuration"/>, builds a runtime from it, swaps it in and optionally saves it to the
    /// store. The previous runtime is disposed. Throws <see cref="InvalidOperationException"/> when invalid.
    /// </summary>
    public RouterRuntime Apply(RouterConfiguration configuration, bool persist, string source = "admin API", string? actor = null, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IReadOnlyList<string> errors = ValidateChange(configuration);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid router configuration: " + string.Join("; ", errors));
        }

        RouterRuntime? previous;
        RouterRuntime runtime;
        var notes = new List<string>();
        lock (_gate)
        {
            RouterConfigurationSource runtimeSource = persist && _store is not null ? RouterConfigurationSource.Store : RouterConfigurationSource.Memory;
            RouterConfiguration document = configuration.Clone();
            if (persist && _store is not null)
            {
                if (_secrets is not null)
                {
                    IReadOnlyList<string> moved = document.MoveSecretsToStore(_secrets);
                    if (moved.Count > 0)
                    {
                        notes.Add($"Moved {moved.Count} secret(s) to the secret store ({_secrets.Location}); the saved file refers to them: {string.Join(", ", moved)}.");
                    }
                }
                else if (document.LiteralSecretSettings() is { Count: > 0 } literal)
                {
                    LogLiteralSecretsSaved(_logger, _store.Location, string.Join(", ", literal));
                    notes.Add(
                        $"{string.Join(", ", literal)} {(literal.Count == 1 ? "is" : "are")} saved in plain text in {_store.Location}. " +
                        "Use ${env:VARIABLE} references, keep keys in user-secrets or environment variables (leave the field empty), " +
                        "or register a secret store (UseSecretFile) so typed keys are kept out of the file.");
                }
            }

            runtime = Build(document, runtimeSource, notes);
            if (persist && _store is not null)
            {
                _store.Save(document);
            }

            runtime.HistoryVersion = Record(document, source, actor, persist && _store is not null ? note : string.Join("; ", new[] { note, "not saved" }.Where(n => n is not null)));

            previous = _current;
            _current = runtime;
        }

        LogApplied(_logger, runtime.Version, runtime.Source, configuration.Routes.Count, configuration.Models.Count, configuration.Connectors.Count);
        ScheduleWarmUp(runtime);
        previous?.Dispose();
        Changed?.Invoke(runtime);
        return runtime;
    }

    /// <summary>Discards the stored document and rebuilds from the application's configuration.</summary>
    public RouterRuntime ResetToBaseline(string? actor = null)
    {
        RouterRuntime? previous;
        RouterRuntime runtime;
        lock (_gate)
        {
            runtime = Build(Baseline.Clone(), RouterConfigurationSource.Configuration);
            Record(runtime.Configuration, "reset", actor, "the application's own configuration");
            previous = _current;
            _current = runtime;
        }

        ScheduleWarmUp(runtime);
        previous?.Dispose();
        Changed?.Invoke(runtime);
        return runtime;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _current?.Dispose();
            _current = null;
        }
    }

    private long? Record(RouterConfiguration document, string source, string? actor, string? note)
    {
        try
        {
            // Masked: the history is for reviewing and rolling back routing, not a second copy of the keys (a rollback takes
            // them from the running configuration).
            return _history?.Record(document.Masked(), source, actor, string.IsNullOrEmpty(note) ? null : note);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            LogHistoryFailed(_logger, ex);
            return null;
        }
    }

    /// <summary>Records the start-up document unless the history already ends with it (a plain restart).</summary>
    private void RecordStartup(RouterConfiguration document, RouterConfigurationSource source)
    {
        if (_history is null)
        {
            return;
        }

        IReadOnlyList<RouterConfigurationVersion> recent = _history.List(1);
        RouterConfigurationVersion? latest = recent.Count > 0 ? recent[0] : null;
        if (latest is null || _history.GetVersion(latest.Id)?.ToJson() != document.Masked().ToJson())
        {
            Record(document, "startup", null, source == RouterConfigurationSource.Store ? _store?.Location : "the application's configuration");
        }
    }

    private (RouterConfiguration Document, RouterConfigurationSource Source) LoadInitial()
    {
        RouterConfiguration baseline = Baseline;
        if (_store is not null)
        {
            try
            {
                if (_store.Load() is { } stored)
                {
                    RouterConfiguration.InheritSecrets(stored, baseline);
                    IReadOnlyList<string> errors = stored.GetDocumentValidationErrors();
                    if (errors.Count == 0)
                    {
                        LogLoadedStore(_logger, _store.Location);
                        return (stored, RouterConfigurationSource.Store);
                    }

                    LogStoreInvalid(_logger, _store.Location, string.Join("; ", errors));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                LogStoreUnreadable(_logger, _store.Location, ex);
            }
        }

        return (baseline.Clone(), RouterConfigurationSource.Configuration);
    }

    private RouterRuntime Build(RouterConfiguration configuration, RouterConfigurationSource source, IReadOnlyList<string>? notes = null)
    {
        // The machine policy wins over the configuration: what breaks it is turned off, and said so.
        IReadOnlyList<string> enforced = Policy.Enforce(configuration);
        IReadOnlyList<string> errors = configuration.GetDocumentValidationErrors();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid SemanticRouter configuration:" + Environment.NewLine + " - " + string.Join(Environment.NewLine + " - ", errors));
        }

        if (_services.GetService<IModelHealth>() is ModelHealthTracker tracker)
        {
            tracker.Configure(configuration.Selection);
        }

        IModelLatency? latency = _services.GetService<IModelLatency>();
        (latency as ModelLatencyTracker)?.Configure(configuration.Selection);
        IModelCacheShare? cache = _services.GetService<IModelCacheShare>();
        (cache as ModelCacheTracker)?.Configure(configuration.Selection);
        _services.GetService<IStateRetention>()?.Apply(configuration.State.Retention);

        var warnings = new List<string>(enforced);
        if (configuration.Middleware.Mode == PromptRouterMode.Dispatch && !configuration.Models.Any(m => m.Enabled))
        {
            // Allowed so a fresh install can start and be configured from the dashboard, but every request gets 503.
            warnings.Add("No model is enabled, so every request gets 503: add one on the dashboard's Models page or run `semantic-router init`.");
        }

        if (!configuration.Embeddings.KeepsPromptsLocal
            && (configuration.Routes.Any(r => r.LocalOnly) || configuration.Profiles.Values.Any(p => p.LocalOnly)))
        {
            warnings.Add(
                $"Embeddings go to {AddressDisplay.Show(configuration.Embeddings.Endpoint)}, which is not on this machine. Requests already known to be local-only " +
                "are classified without it, but local-only routes are found by embedding the prompt, so every prompt reaches that endpoint " +
                "first. Use Onnx or a local embedding server, or set Embeddings:IsLocal if the server is yours.");
        }

        if (configuration.Middleware.AllowUnauthenticatedRemoteClients && configuration.Middleware.GatewayKeys.Count == 0)
        {
            warnings.Add(
                "The gateway serves other machines without a key (Middleware:AllowUnauthenticatedRemoteClients), so anyone who can reach it " +
                "can use the connectors' keys. Make sure a reverse proxy authenticates every request, or set Middleware:GatewayKeys.");
        }

        foreach (RouteDefinition route in configuration.Routes.Where(r => r.PreferredModel is { Length: > 0 }))
        {
            if (!configuration.Models.Any(m => m.Enabled && string.Equals(m.Name, route.PreferredModel, StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add($"Route '{route.Name}' prefers model '{route.PreferredModel}', which is not an enabled catalog model: its tier chooses as usual.");
            }
        }

        RouterConfiguration resolved = configuration.ResolveSecrets(_secrets, warnings);
        foreach ((string name, string reason) in Policy.BlockedWebhooks(resolved.Notifications).ToArray())
        {
            // A webhook address held as a secret is only known once resolved.
            resolved.Notifications.Webhooks.Remove(name);
            warnings.Add($"Webhook '{name}' is not used: {reason}.");
        }

        foreach (string warning in warnings)
        {
            LogConfigurationWarning(_logger, warning);
        }

        IEmbeddingProvider embeddings = ResolveCustom<IEmbeddingProvider>() ?? CreateEmbeddingProvider(resolved.Embeddings);
        IComplexityAnalyzer complexity = ResolveCustom<IComplexityAnalyzer>() ?? new HeuristicComplexityAnalyzer(configuration.Complexity);
        IRouteSelector routeSelector = ResolveCustom<IRouteSelector>() ?? new DefaultRouteSelector(configuration);
        IPromptClassifier classifier = ResolveCustom<IPromptClassifier>() ?? new SemanticPromptClassifier(embeddings, configuration, complexity, routeSelector);
        IModelResidency? residency = _services.GetService<IModelResidency>();
        (residency as ModelResidencyTracker)?.Configure(resolved);
        IModelSelector modelSelector = ResolveCustom<IModelSelector>() ?? new DefaultModelSelector(configuration.Selection, latency, cache, residency);
        IModelHealth health = _services.GetRequiredService<IModelHealth>();
        IPromptRouter router = ResolveCustom<IPromptRouter>() ?? new PromptRouter(classifier, configuration, modelSelector, health, _services.GetService<TimeProvider>(), _services.GetService<IModelReliability>(), latency);
        IPromptExtractor extractor = ResolveCustom<IPromptExtractor>() ?? new JsonPromptExtractor(
            configuration.Middleware.MessageSelection, configuration.Middleware.StripSystemReminders, configuration.Middleware.InjectedTags);

        var redactor = new SecretRedactor(resolved.SecretValues());
        var connectors = new ConnectorCatalog(
            new Dictionary<string, ConnectorOptions>(resolved.Connectors, StringComparer.OrdinalIgnoreCase),
            _services.GetServices<IChatConnector>(),
            _services.GetRequiredService<IHttpClientFactory>(),
            _services.GetRequiredService<ICliProcessRunner>(),
            _services.GetService<IToolCallAffinity>(),
            _services.GetRequiredService<ILoggerFactory>(),
            redactor,
            _services.GetService<PromptRouterMetrics>(),
            _services.GetServices<IChatConnectorFactory>(),
            Policy,
            configuration.State.StorePrompts);

        return new RouterRuntime
        {
            Configuration = configuration,
            Resolved = resolved,
            Warnings = warnings,
            ApplyNotes = notes ?? [],
            Redactor = redactor,
            GatewayKeys = resolved.Middleware.GatewayKeys
                .Where(kv => !string.IsNullOrEmpty(kv.Value))
                .GroupBy(kv => kv.Value, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal),
            Version = ++_version,
            CreatedAt = DateTimeOffset.UtcNow,
            Source = source,
            EmbeddingProvider = embeddings,
            Classifier = classifier,
            Router = router,
            Extractor = extractor,
            Connectors = connectors,
            OpenAIPrefixes = ToPaths(configuration.Middleware.PathPrefixes),
            AnthropicPrefixes = ToPaths(configuration.Middleware.EffectiveAnthropicPathPrefixes),
            ResponsesPrefixes = ToPaths(configuration.Middleware.EffectiveResponsesPathPrefixes),
            ModelAliases = configuration.Middleware.ModelAliases.Select(kv => (kv.Key, kv.Value)).ToArray(),
        };
    }

    private static PathString[] ToPaths(IEnumerable<string> prefixes) => prefixes
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Select(p => p.StartsWith('/') ? p.TrimEnd('/') : "/" + p.TrimEnd('/'))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Select(p => new PathString(p))
        .ToArray();

    /// <summary>A user-registered implementation, or <see langword="null"/> when only the router's own facade is registered.</summary>
    private T? ResolveCustom<T>()
        where T : class
    {
        T? service = _services.GetService<T>();
        return service is null or IRuntimeFacade ? null : service;
    }

    internal IEmbeddingProvider CreateEmbeddingProvider(EmbeddingProviderOptions options) => options.Provider switch
    {
        EmbeddingProviderKind.Hashing => new HashingEmbeddingProvider(options.Dimensions),
        EmbeddingProviderKind.OpenAICompatible => new OpenAICompatibleEmbeddingProvider(
            () => _services.GetRequiredService<IHttpClientFactory>().CreateClient(SemanticRouterServiceCollectionExtensions.EmbeddingsClientName),
            options),
        _ => _services.GetServices<IEmbeddingProviderFactory>().FirstOrDefault(f => f.Kind == options.Provider)?.Create(options)
             ?? new UnavailableEmbeddingProvider(options.Provider == EmbeddingProviderKind.Onnx
                 ? "The Onnx embedding provider needs the SemanticRouter.Embeddings.Onnx package: add it and call builder.UseOnnxEmbeddings() (the semantic-router tool includes it)."
                 : $"No embedding provider is registered for '{options.Provider}'."),
    };

    private void ScheduleWarmUp(RouterRuntime runtime)
    {
        if (!runtime.Configuration.Embeddings.WarmUpOnStartup)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                await runtime.Classifier.WarmUpAsync(CancellationToken.None).ConfigureAwait(false);
                runtime.WarmedUp = true;
                double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                LogWarmedUp(_logger, runtime.Version, elapsed);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
            {
                // A model that cannot be downloaded or a server that does not answer: the message says what to do, and a
                // stack trace would only bury it.
                runtime.WarmUpError = ex.Message;
                LogWarmUpProblem(_logger, runtime.Version, ex.Message);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                runtime.WarmUpError = ex.Message;
                LogWarmUpFailed(_logger, runtime.Version, ex);
            }
        });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Router configuration v{Version} applied ({Source}): {Routes} routes, {Models} models, {Connectors} connectors.")]
    private static partial void LogApplied(ILogger logger, int version, RouterConfigurationSource source, int routes, int models, int connectors);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Router configuration: {Warning}")]
    private static partial void LogConfigurationWarning(ILogger logger, string warning);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Saving secrets in plain text to {Location}: {Settings}. Prefer ${{env:VARIABLE}} references, user-secrets or environment variables, or register a secret store with UseSecretFile().")]
    private static partial void LogLiteralSecretsSaved(ILogger logger, string location, string settings);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not record the configuration in the history.")]
    private static partial void LogHistoryFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Loaded router configuration from {Location}.")]
    private static partial void LogLoadedStore(ILogger logger, string location);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring stored router configuration at {Location}: {Errors}")]
    private static partial void LogStoreInvalid(ILogger logger, string location, string errors);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the stored router configuration at {Location}; using the application's configuration.")]
    private static partial void LogStoreUnreadable(ILogger logger, string location, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Semantic route index v{Version} ready in {ElapsedMs:0} ms.")]
    private static partial void LogWarmedUp(ILogger logger, int version, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Semantic route index v{Version} warm-up failed; it will be retried on the first request.")]
    private static partial void LogWarmUpFailed(ILogger logger, int version, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Semantic route index v{Version} warm-up failed, and will be retried on the first request: {Problem}")]
    private static partial void LogWarmUpProblem(ILogger logger, int version, string problem);
}

/// <summary>DI facades that forward to the current runtime, so consumers never hold a stale instance.</summary>
internal sealed class RuntimeClassifierFacade(RouterRuntimeProvider runtime) : IPromptClassifier, IRuntimeFacade
{
    public ValueTask<PromptClassificationResult> ClassifyAsync(string prompt, CancellationToken cancellationToken = default) =>
        runtime.Current.Classifier.ClassifyAsync(prompt, cancellationToken);

    public ValueTask WarmUpAsync(CancellationToken cancellationToken = default) => runtime.Current.Classifier.WarmUpAsync(cancellationToken);
}

internal sealed class RuntimeRouterFacade(RouterRuntimeProvider runtime) : IPromptRouter, IRuntimeFacade
{
    public IReadOnlyList<ModelDefinition> Models => runtime.Current.Router.Models;

    public IReadOnlyDictionary<string, RoutingProfile> Profiles => runtime.Current.Router.Profiles;

    public ValueTask<RoutingPlan> RouteAsync(PromptRoutingRequest request, CancellationToken cancellationToken = default) =>
        runtime.Current.Router.RouteAsync(request, cancellationToken);

    public RoutingPlan Plan(PromptClassificationResult classification, PromptRoutingRequest request) =>
        runtime.Current.Router.Plan(classification, request);
}

internal sealed class RuntimeEmbeddingProviderFacade(RouterRuntimeProvider runtime) : IEmbeddingProvider, IRuntimeFacade
{
    /// <summary>The provider currently in use.</summary>
    public IEmbeddingProvider Inner => runtime.Current.EmbeddingProvider;

    public ValueTask<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default) =>
        Inner.GenerateEmbeddingAsync(text, cancellationToken);

    public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
        Inner.GenerateEmbeddingsAsync(texts, cancellationToken);
}

internal sealed class RuntimeExtractorFacade(RouterRuntimeProvider runtime) : IPromptExtractor, IRuntimeFacade
{
    public string? ExtractPrompt(HttpContext context, System.Text.Json.JsonElement body) => runtime.Current.Extractor.ExtractPrompt(context, body);
}

/// <summary>Stands in for a provider that cannot be built, so the configuration still applies and the reason shows up on the first use (the warm-up).</summary>
internal sealed class UnavailableEmbeddingProvider(string reason) : IEmbeddingProvider
{
    public ValueTask<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(reason);

    public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(reason);
}
