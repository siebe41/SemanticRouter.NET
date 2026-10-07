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
    public required RouterConfiguration Configuration { get; init; }

    public required int Version { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required RouterConfigurationSource Source { get; init; }

    public required IEmbeddingProvider EmbeddingProvider { get; init; }

    public required IPromptClassifier Classifier { get; init; }

    public required IPromptRouter Router { get; init; }

    public required IPromptExtractor Extractor { get; init; }

    public required ConnectorCatalog Connectors { get; init; }

    public required PathString[] OpenAIPrefixes { get; init; }

    public required PathString[] AnthropicPrefixes { get; init; }

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
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private RouterRuntime? _current;
    private RouterConfiguration? _baseline;
    private int _version;

    public RouterRuntimeProvider(IServiceProvider services, ILogger<RouterRuntimeProvider> logger, IRouterConfigurationStore? store = null)
    {
        _services = services;
        _store = store;
        _logger = logger;
    }

    /// <summary>Raised after a new runtime replaced the previous one.</summary>
    public event Action<RouterRuntime>? Changed;

    /// <summary>The store, when configured.</summary>
    public IRouterConfigurationStore? Store => _store;

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
                        _services.GetRequiredService<IOptions<PromptRouterMiddlewareOptions>>().Value);
                }
            }

            return _baseline;
        }
    }

    /// <summary>Validation errors for a document; empty when it could be applied.</summary>
    public static IReadOnlyList<string> Validate(RouterConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.GetDocumentValidationErrors();
    }

    /// <summary>
    /// Validates <paramref name="configuration"/>, builds a runtime from it, swaps it in and optionally saves it to the
    /// store. The previous runtime is disposed. Throws <see cref="InvalidOperationException"/> when invalid.
    /// </summary>
    public RouterRuntime Apply(RouterConfiguration configuration, bool persist)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        IReadOnlyList<string> errors = Validate(configuration);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid router configuration: " + string.Join("; ", errors));
        }

        RouterRuntime? previous;
        RouterRuntime runtime;
        lock (_gate)
        {
            RouterConfigurationSource source = persist && _store is not null ? RouterConfigurationSource.Store : RouterConfigurationSource.Memory;
            runtime = Build(configuration.Clone(), source);
            if (persist && _store is not null)
            {
                _store.Save(configuration);
            }

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
    public RouterRuntime ResetToBaseline()
    {
        RouterRuntime? previous;
        RouterRuntime runtime;
        lock (_gate)
        {
            runtime = Build(Baseline.Clone(), RouterConfigurationSource.Configuration);
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

    private RouterRuntime Build(RouterConfiguration configuration, RouterConfigurationSource source)
    {
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

        IEmbeddingProvider embeddings = ResolveCustom<IEmbeddingProvider>() ?? CreateEmbeddingProvider(configuration.Embeddings);
        IComplexityAnalyzer complexity = ResolveCustom<IComplexityAnalyzer>() ?? new HeuristicComplexityAnalyzer(configuration.Complexity);
        IRouteSelector routeSelector = ResolveCustom<IRouteSelector>() ?? new DefaultRouteSelector(configuration);
        IPromptClassifier classifier = ResolveCustom<IPromptClassifier>() ?? new SemanticPromptClassifier(embeddings, configuration, complexity, routeSelector);
        IModelSelector modelSelector = ResolveCustom<IModelSelector>() ?? new DefaultModelSelector(configuration.Selection);
        IModelHealth health = _services.GetRequiredService<IModelHealth>();
        IPromptRouter router = ResolveCustom<IPromptRouter>() ?? new PromptRouter(classifier, configuration, modelSelector, health);
        IPromptExtractor extractor = ResolveCustom<IPromptExtractor>() ?? new JsonPromptExtractor(configuration.Middleware.MessageSelection);

        var connectors = new ConnectorCatalog(
            new Dictionary<string, ConnectorOptions>(configuration.Connectors, StringComparer.OrdinalIgnoreCase),
            _services.GetServices<IChatConnector>(),
            _services.GetRequiredService<IHttpClientFactory>(),
            _services.GetRequiredService<ICliProcessRunner>(),
            _services.GetService<IToolCallAffinity>(),
            _services.GetRequiredService<ILoggerFactory>());

        return new RouterRuntime
        {
            Configuration = configuration,
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

    private IEmbeddingProvider CreateEmbeddingProvider(EmbeddingProviderOptions options) => options.Provider switch
    {
        EmbeddingProviderKind.Hashing => new HashingEmbeddingProvider(options.Dimensions),
        EmbeddingProviderKind.OpenAICompatible => new OpenAICompatibleEmbeddingProvider(
            () => _services.GetRequiredService<IHttpClientFactory>().CreateClient(SemanticRouterServiceCollectionExtensions.EmbeddingsClientName),
            options),
        _ => throw new InvalidOperationException($"Unknown embedding provider '{options.Provider}'."),
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
                LogWarmedUp(_logger, runtime.Version, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
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
