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
    /// the <c>Connectors</c>, <c>Embeddings</c> and <c>Middleware</c> child sections.
    /// </summary>
    public static SemanticRouterBuilder AddSemanticPromptRouting(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SemanticRouterOptions>().Bind(configuration);
        services.AddOptions<EmbeddingProviderOptions>().Bind(configuration.GetSection("Embeddings"));
        services.AddOptions<PromptRouterMiddlewareOptions>().Bind(configuration.GetSection("Middleware"));
        services.AddOptions<SemanticRouterConnectorsOptions>().Bind(configuration);

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

        services.TryAddSingleton<IComplexityAnalyzer>(sp =>
            new HeuristicComplexityAnalyzer(RouterOptions(sp).Complexity));
        services.TryAddSingleton<IRouteSelector>(sp => new DefaultRouteSelector(RouterOptions(sp)));
        services.TryAddSingleton<IEmbeddingProvider>(CreateEmbeddingProvider);
        services.TryAddSingleton<IPromptClassifier>(sp => new SemanticPromptClassifier(
            sp.GetRequiredService<IEmbeddingProvider>(),
            RouterOptions(sp),
            sp.GetRequiredService<IComplexityAnalyzer>(),
            sp.GetRequiredService<IRouteSelector>()));
        services.TryAddSingleton<IPromptExtractor>(sp => new JsonPromptExtractor(
            sp.GetRequiredService<IOptions<PromptRouterMiddlewareOptions>>().Value.MessageSelection));

        services.TryAddSingleton<IModelSelector>(sp => new DefaultModelSelector(RouterOptions(sp).Selection));
        services.TryAddSingleton<IModelHealth>(sp => new ModelHealthTracker(
            RouterOptions(sp).Selection, sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.TryAddSingleton<IPromptRouter>(sp => new PromptRouter(
            sp.GetRequiredService<IPromptClassifier>(),
            RouterOptions(sp),
            sp.GetRequiredService<IModelSelector>(),
            sp.GetRequiredService<IModelHealth>()));

        services.TryAddSingleton<PromptRouterMetrics>();
        services.TryAddSingleton<ICliProcessRunner, SystemCliProcessRunner>();
        services.TryAddSingleton<ChatConnectorRegistry>();
        services.TryAddSingleton<PromptDispatcher>();
        services.AddHostedService<SemanticRouterWarmupService>();

        // Connector clients time out per request (time to response headers), so streamed replies are never cut off.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IConfigureOptions<HttpClientFactoryOptions>, ConnectorHttpClientOptions>());
        services.AddHttpClient(EmbeddingsClientName)
            .ConfigureHttpClient((sp, client) =>
                client.Timeout = sp.GetRequiredService<IOptions<EmbeddingProviderOptions>>().Value.Timeout);

        return new SemanticRouterBuilder(services);
    }

    internal static IEmbeddingProvider CreateEmbeddingProvider(IServiceProvider services)
    {
        EmbeddingProviderOptions options = services.GetRequiredService<IOptions<EmbeddingProviderOptions>>().Value;
        return options.Provider switch
        {
            EmbeddingProviderKind.Hashing => new HashingEmbeddingProvider(options.Dimensions),
            EmbeddingProviderKind.OpenAICompatible => new OpenAICompatibleEmbeddingProvider(
                () => services.GetRequiredService<IHttpClientFactory>().CreateClient(EmbeddingsClientName),
                options),
            _ => throw new InvalidOperationException($"Unknown embedding provider '{options.Provider}'."),
        };
    }

    private static SemanticRouterOptions RouterOptions(IServiceProvider sp) =>
        sp.GetRequiredService<IOptions<SemanticRouterOptions>>().Value;

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
