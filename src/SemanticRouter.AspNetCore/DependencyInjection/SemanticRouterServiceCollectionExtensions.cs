using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SemanticRouter;
using SemanticRouter.AspNetCore;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers semantic prompt routing services.</summary>
public static class SemanticRouterServiceCollectionExtensions
{
    internal const string EmbeddingsClientName = "SemanticRouter.Embeddings";

    /// <summary>
    /// Registers prompt routing, binding <see cref="SemanticRouterOptions"/> (routes, models, selection) from
    /// <paramref name="configuration"/> (typically <c>builder.Configuration.GetSection("SemanticRouter")</c>) along with
    /// the <c>Connectors</c>, <c>Embeddings</c>, <c>Middleware</c>, <c>Usage</c>, <c>Memory</c>, <c>Tuning</c>, <c>Notifications</c>, <c>Dashboard</c> and <c>Network</c> child sections.
    /// </summary>
    public static SemanticRouterBuilder AddSemanticPromptRouting(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SemanticRouterOptions>().Bind(configuration);
        services.AddOptions<EmbeddingProviderOptions>().Bind(configuration.GetSection("Embeddings"));
        services.AddOptions<PromptRouterMiddlewareOptions>().Bind(configuration.GetSection("Middleware"));
        services.AddOptions<SemanticRouterConnectorsOptions>().Bind(configuration);
        services.AddOptions<UsageOptions>().Bind(configuration.GetSection("Usage"));
        services.AddOptions<MemoryOptions>().Bind(configuration.GetSection("Memory"));
        services.AddOptions<TuningOptions>().Bind(configuration.GetSection("Tuning"));
        services.AddOptions<NotificationOptions>().Bind(configuration.GetSection("Notifications"));
        services.AddOptions<StateOptions>().Bind(configuration.GetSection("State"));
        services.AddOptions<Microsoft.AspNetCore.Builder.RouterDashboardOptions>().Bind(configuration.GetSection("Dashboard"));
        services.AddOptions<NetworkOptions>().Bind(configuration.GetSection("Network"));

        return AddCore(services);
    }

    /// <summary>Registers prompt routing configured in code.</summary>
    public static SemanticRouterBuilder AddSemanticPromptRouting(
        this IServiceCollection services,
        Action<SemanticRouterOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (configure is not null)
        {
            services.Configure(configure);
        }

        return AddCore(services);
    }

    private static SemanticRouterBuilder AddCore(IServiceCollection services)
    {
        services.AddOptions();
        services.AddMetrics();
        services.AddOptions<SemanticRouterOptions>().ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<SemanticRouterOptions>, SemanticRouterOptionsValidator>());

        // Long-lived state shared across configuration reloads.
        services.TryAddSingleton<IModelHealth>(sp => new ModelHealthTracker(
            sp.GetRequiredService<IOptions<SemanticRouterOptions>>().Value.Selection,
            sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetService<ICooldownStore>()));
        services.TryAddSingleton<IRouterConfigurationHistory>(_ => new InMemoryRouterConfigurationHistory());
        services.TryAddSingleton<IUsageLedger, InMemoryUsageLedger>();
        services.TryAddSingleton<IAuditLog>(_ => new InMemoryAuditLog());
        services.TryAddSingleton(sp => new BudgetGate(
            sp.GetRequiredService<IUsageLedger>(), sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BudgetGate>>()));
        services.TryAddSingleton<IToolCallAffinity, ToolCallAffinity>();
        services.TryAddSingleton<IWarmCacheStore>(sp => new WarmCacheStore(sp.GetService<TimeProvider>()));
        services.TryAddSingleton<RoutingActivityLog>();
        services.TryAddSingleton(sp => new GatewayGuard(
            sp.GetService<IOptions<Microsoft.AspNetCore.Builder.RouterDashboardOptions>>()?.Value.PublicOrigins ?? []));
        services.TryAddSingleton<RoutingFeedbackLog>();
        services.TryAddSingleton(sp => new ModelLatencyTracker(sp.GetRequiredService<RoutingActivityLog>(), sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IModelLatency>(sp => sp.GetRequiredService<ModelLatencyTracker>());
        services.TryAddSingleton(sp => new ModelCacheTracker(sp.GetRequiredService<RoutingActivityLog>(), sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IModelCacheShare>(sp => sp.GetRequiredService<ModelCacheTracker>());
        services.TryAddSingleton<ModelReliabilityTracker>();
        services.TryAddSingleton<IModelReliability>(sp => sp.GetRequiredService<ModelReliabilityTracker>());
        services.TryAddSingleton<IEvaluationSetStore>(sp =>
            sp.GetService<IRouterConfigurationStore>() is JsonFileRouterConfigurationStore store
                ? new JsonLinesEvaluationSetStore(EvaluationSetFileNextTo(store.Location))
                : new InMemoryEvaluationSetStore());
        services.TryAddSingleton<RetryTracker>();
        services.TryAddSingleton<ConversationTracker>();
        services.TryAddSingleton<ITuningStore, InMemoryTuningStore>();
        services.TryAddSingleton<ModelTrials>();
        services.TryAddSingleton<RouterTuner>();
        services.TryAddSingleton<MemoryExposureLog>();
        services.TryAddSingleton<MemoryIndex>();
        services.TryAddSingleton<PromptRouterMetrics>();
        services.TryAddSingleton<ICliProcessRunner, SystemCliProcessRunner>();

        // Everything derived from configuration lives in the current RouterRuntime; these facades forward to it.
        // Register your own implementation of any of these interfaces to replace the router's default.
        services.TryAddSingleton<RouterRuntimeProvider>();
        services.TryAddSingleton<IPromptClassifier, RuntimeClassifierFacade>();
        services.TryAddSingleton<IPromptRouter, RuntimeRouterFacade>();
        services.TryAddSingleton<IEmbeddingProvider, RuntimeEmbeddingProviderFacade>();
        services.TryAddSingleton<IPromptExtractor, RuntimeExtractorFacade>();
        services.TryAddSingleton<ChatConnectorRegistry>();
        services.TryAddSingleton<ConnectorIncidents>();
        services.TryAddSingleton(sp => new EndpointChecks(sp.GetService<TimeProvider>()));
        services.TryAddSingleton<PromptDispatcher>();
        services.TryAddSingleton<AnswerJudge>();
        services.TryAddSingleton<ClientTurnPatterns>();
        services.TryAddSingleton<CorrectionDetector>();
        services.TryAddSingleton(sp => new ClientRateLimiter(sp.GetService<TimeProvider>()));
        services.AddHostedService<SemanticRouterWarmupService>();
        services.AddHostedService<TuningService>();
        services.TryAddSingleton<RouterNotices>();
        services.TryAddSingleton<WebhookNotifier>();
        services.TryAddSingleton<NoticeService>();
        services.AddHostedService(sp => sp.GetRequiredService<NoticeService>());

        // The client factory logs every request of the router's own clients at Information, and since .NET 10 a failed one
        // with its exception message, which can quote a key a backend echoed back. The connectors log failures themselves,
        // redacted, so the factory's messages for these clients are left out.
        services.AddLogging(logging => logging.AddFilter("System.Net.Http.HttpClient.SemanticRouter", LogLevel.Warning));

        // Network:CaBundle: extra roots for the router's own HTTPS clients. A bundle that cannot be loaded fails start-up.
        services.AddOptions<NetworkOptions>().ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<NetworkOptions>, NetworkOptionsValidator>());
        services.TryAddSingleton(sp =>
        {
            NetworkOptions network;
            try
            {
                network = sp.GetRequiredService<IOptions<NetworkOptions>>().Value;
            }
            catch (OptionsValidationException ex)
            {
                // Before start-up validation (doctor, or a client created first): the same failure as a configuration error.
                throw new InvalidOperationException(ex.Message, ex);
            }

            return OutboundTrust.From(network, sp.GetService<IHostEnvironment>()?.ContentRootPath);
        });

        // Connector clients time out per request (time to response headers), so streamed replies are never cut off.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<HttpClientFactoryOptions>, ConnectorHttpClientOptions>());
        services.AddHttpClient(EmbeddingsClientName)
            .ConfigureHttpClient((sp, client) =>
                client.Timeout = sp.GetRequiredService<RouterRuntimeProvider>().Current.Configuration.Embeddings.Timeout);
        // A webhook URL carries its token in the path, which the client factory would log with every request.
        services.AddHttpClient(WebhookNotifier.ClientName).ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(10)).RemoveAllLoggers();

        return new SemanticRouterBuilder(services);
    }

    /// <summary>The evaluation set's file next to a configuration file: <c>{name}.evaluation.jsonl</c>.</summary>
    public static string EvaluationSetFileNextTo(string configurationFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationFile);
        string full = Path.GetFullPath(configurationFile);
        return Path.Combine(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full) + ".evaluation.jsonl");
    }

    /// <summary>The prefix of every client the router creates (connectors, embeddings, webhooks, ONNX model downloads).</summary>
    internal const string RouterClientPrefix = "SemanticRouter.";

    private sealed class ConnectorHttpClientOptions(IServiceProvider services) : IConfigureNamedOptions<HttpClientFactoryOptions>
    {
        public void Configure(string? name, HttpClientFactoryOptions options)
        {
            // The router's own clients also trust Network:CaBundle; the host's other clients are left alone.
            if (name?.StartsWith(RouterClientPrefix, StringComparison.Ordinal) == true
                && services.GetRequiredService<OutboundTrust>() is { Roots: not null } trust)
            {
                options.HttpMessageHandlerBuilderActions.Add(builder => trust.Apply(builder.PrimaryHandler));
            }

            bool connector = name?.StartsWith(OpenAICompatibleConnector.HttpClientNamePrefix, StringComparison.Ordinal) == true;
            if (connector)
            {
                options.HttpClientActions.Add(client => client.Timeout = Timeout.InfiniteTimeSpan);
            }

            // These requests carry keys, and a redirect drops only the Authorization header: x-api-key, api-key and other
            // key headers would go to whatever host the redirect names. No backend needs one, so none is followed.
            if (connector || name is EmbeddingsClientName or WebhookNotifier.ClientName)
            {
                options.HttpMessageHandlerBuilderActions.Add(builder =>
                {
                    switch (builder.PrimaryHandler)
                    {
                        case HttpClientHandler handler:
                            handler.AllowAutoRedirect = false;
                            break;
                        case SocketsHttpHandler sockets:
                            sockets.AllowAutoRedirect = false;
                            break;
                    }
                });
            }
        }

        public void Configure(HttpClientFactoryOptions options) => Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
    }
}
