using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SemanticRouter;
using SemanticRouter.AspNetCore;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers semantic prompt routing services.</summary>
public static class SemanticRouterServiceCollectionExtensions
{
    internal const string EmbeddingsClientName = "SemanticRouter.Embeddings";

    /// <summary>
    /// Registers prompt routing, binding <see cref="SemanticRouterOptions"/> from <paramref name="configuration"/>
    /// (typically <c>builder.Configuration.GetSection("SemanticRouter")</c>) and the <c>Embeddings</c>,
    /// <c>Middleware</c> and <c>Endpoints</c> child sections.
    /// </summary>
    public static SemanticRouterBuilder AddSemanticPromptRouting(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SemanticRouterOptions>().Bind(configuration);
        services.AddOptions<EmbeddingProviderOptions>().Bind(configuration.GetSection("Embeddings"));
        services.AddOptions<PromptRouterMiddlewareOptions>().Bind(configuration.GetSection("Middleware"));
        services.AddOptions<PromptDispatchOptions>().Bind(configuration.GetSection("Endpoints"));

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

        services.TryAddSingleton<PromptRouterMetrics>();
        services.TryAddSingleton<PromptDispatcher>();
        services.AddHostedService<SemanticRouterWarmupService>();

        services.AddHttpClient(PromptDispatcher.LocalClientName)
            .ConfigureHttpClient((sp, client) => client.Timeout = DispatchOptions(sp).Local.Timeout);
        services.AddHttpClient(PromptDispatcher.CloudClientName)
            .ConfigureHttpClient((sp, client) => client.Timeout = DispatchOptions(sp).Cloud.Timeout);
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

    private static PromptDispatchOptions DispatchOptions(IServiceProvider sp) =>
        sp.GetRequiredService<IOptions<PromptDispatchOptions>>().Value;
}
