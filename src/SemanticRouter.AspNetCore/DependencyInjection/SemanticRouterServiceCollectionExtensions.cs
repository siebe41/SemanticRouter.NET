using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
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
    /// the <c>Connectors</c>, <c>Embeddings</c>, <c>Middleware</c>, <c>Usage</c> and <c>Dashboard</c> child sections.
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
        services.AddOptions<Microsoft.AspNetCore.Builder.RouterDashboardOptions>().Bind(configuration.GetSection("Dashboard"));

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
        services.TryAddSingleton(sp => new BudgetGate(
            sp.GetRequiredService<IUsageLedger>(), sp.GetService<TimeProvider>() ?? TimeProvider.System,
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<BudgetGate>>()));
        services.TryAddSingleton<IToolCallAffinity, ToolCallAffinity>();
        services.TryAddSingleton<RoutingActivityLog>();
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
        services.TryAddSingleton<PromptDispatcher>();
        services.AddHostedService<SemanticRouterWarmupService>();

        // Connector clients time out per request (time to response headers), so streamed replies are never cut off.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<HttpClientFactoryOptions>, ConnectorHttpClientOptions>());
        services.AddHttpClient(EmbeddingsClientName)
            .ConfigureHttpClient((sp, client) =>
                client.Timeout = sp.GetRequiredService<RouterRuntimeProvider>().Current.Configuration.Embeddings.Timeout);

        return new SemanticRouterBuilder(services);
    }

    private sealed class ConnectorHttpClientOptions : IConfigureNamedOptions<HttpClientFactoryOptions>
    {
        public void Configure(string? name, HttpClientFactoryOptions options)
        {
            if (name?.StartsWith(OpenAICompatibleConnector.HttpClientNamePrefix, StringComparison.Ordinal) == true)
            {
                options.HttpClientActions.Add(client => client.Timeout = Timeout.InfiniteTimeSpan);
            }
        }

        public void Configure(HttpClientFactoryOptions options) => Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
    }
}
