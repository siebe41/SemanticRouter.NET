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
        return this;
    }

    /// <summary>Uses an OpenAI-compatible <c>/v1/embeddings</c> endpoint such as Ollama or OpenAI.</summary>
    public SemanticRouterBuilder UseOpenAICompatibleEmbeddings(Action<EmbeddingProviderOptions>? configure = null)
    {
        Services.Configure<EmbeddingProviderOptions>(o =>
        {
            o.Provider = EmbeddingProviderKind.OpenAICompatible;
            configure?.Invoke(o);
        });
        return this;
    }

    /// <summary>
    /// Keeps the configuration in a JSON file so changes made through the dashboard or the admin API survive restarts.
    /// When the file exists it replaces the application's <c>SemanticRouter</c> section (secrets left empty in the file
    /// are inherited from it). Relative paths resolve against the current directory.
    /// </summary>
    public SemanticRouterBuilder UseConfigurationFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Services.Replace(ServiceDescriptor.Singleton<IRouterConfigurationStore>(new JsonFileRouterConfigurationStore(path)));
        return this;
    }

    /// <summary>Uses a custom <see cref="IRouterConfigurationStore"/>.</summary>
    public SemanticRouterBuilder UseConfigurationStore<TStore>()
        where TStore : class, IRouterConfigurationStore
    {
        Services.Replace(ServiceDescriptor.Singleton<IRouterConfigurationStore, TStore>());
        return this;
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

    /// <summary>Adds or replaces a configured connector (applied after configuration binding).</summary>
    public SemanticRouterBuilder AddConnector(string name, Action<ConnectorOptions> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        Services.Configure<SemanticRouterConnectorsOptions>(o =>
        {
            var options = new ConnectorOptions();
            configure(options);
            o.Connectors[name] = options;
        });
        return this;
    }

    /// <summary>Registers a custom <see cref="IChatConnector"/> implementation (singleton), addressable by its <see cref="IChatConnector.Name"/>.</summary>
    public SemanticRouterBuilder AddConnector<TConnector>()
        where TConnector : class, IChatConnector
    {
        Services.AddSingleton<IChatConnector, TConnector>();
        return this;
    }

    /// <summary>Adds a model to the catalog (applied after configuration binding).</summary>
    public SemanticRouterBuilder AddModel(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Services.Configure<SemanticRouterOptions>(o => o.Models.Add(model));
        return this;
    }
}
