using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SemanticRouter.AspNetCore;

/// <summary>Fluent customization returned by <c>AddSemanticPromptRouting()</c>.</summary>
public sealed class SemanticRouterBuilder
{
    internal SemanticRouterBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>The application's service collection.</summary>
    public IServiceCollection Services { get; }

    /// <summary>Uses a custom <see cref="IEmbeddingProvider"/> (registered as a singleton).</summary>
    public SemanticRouterBuilder UseEmbeddingProvider<TProvider>()
        where TProvider : class, IEmbeddingProvider
    {
        Services.Replace(ServiceDescriptor.Singleton<IEmbeddingProvider, TProvider>());
        return this;
    }

    /// <summary>Uses a custom <see cref="IEmbeddingProvider"/> created by <paramref name="factory"/>.</summary>
    public SemanticRouterBuilder UseEmbeddingProvider(Func<IServiceProvider, IEmbeddingProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        Services.Replace(ServiceDescriptor.Singleton(factory));
        return this;
    }

    /// <summary>Uses the in-process <see cref="HashingEmbeddingProvider"/> (development and tests only).</summary>
    public SemanticRouterBuilder UseHashingEmbeddings(int dimensions = HashingEmbeddingProvider.DefaultDimensions)
    {
        Services.Configure<EmbeddingProviderOptions>(o =>
        {
            o.Provider = EmbeddingProviderKind.Hashing;
            o.Dimensions = dimensions;
        });
        return UseEmbeddingProvider(SemanticRouterServiceCollectionExtensions.CreateEmbeddingProvider);
    }

    /// <summary>Uses an OpenAI-compatible <c>/v1/embeddings</c> endpoint such as Ollama or OpenAI.</summary>
    public SemanticRouterBuilder UseOpenAICompatibleEmbeddings(Action<EmbeddingProviderOptions>? configure = null)
    {
        Services.Configure<EmbeddingProviderOptions>(o =>
        {
            o.Provider = EmbeddingProviderKind.OpenAICompatible;
            configure?.Invoke(o);
        });
        return UseEmbeddingProvider(SemanticRouterServiceCollectionExtensions.CreateEmbeddingProvider);
    }

    /// <summary>Customizes the routing rules (applied after configuration binding).</summary>
    public SemanticRouterBuilder ConfigureRouting(Action<SemanticRouterOptions> configure)
    {
        Services.Configure(configure);
        return this;
    }

    /// <summary>Customizes the middleware (applied after configuration binding).</summary>
    public SemanticRouterBuilder ConfigureMiddleware(Action<PromptRouterMiddlewareOptions> configure)
    {
        Services.Configure(configure);
        return this;
    }

    /// <summary>Customizes the dispatch endpoints (applied after configuration binding).</summary>
    public SemanticRouterBuilder ConfigureEndpoints(Action<PromptDispatchOptions> configure)
    {
        Services.Configure(configure);
        return this;
    }
}
