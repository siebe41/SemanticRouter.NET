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

    /// <summary>
    /// Holds every configuration to <paramref name="policy"/>, which the user's configuration cannot override: what breaks
    /// it is turned off at start-up, and a change that would break it is refused (<see cref="MachinePolicy"/>).
    /// </summary>
    public SemanticRouterBuilder UseMachinePolicy(MachinePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Services.Replace(ServiceDescriptor.Singleton(policy));
        return this;
    }

    /// <summary>
    /// Reads this machine's policy (<see cref="MachinePolicyLoader.Load"/>) and holds every configuration to it. Throws
    /// <see cref="InvalidDataException"/> when a policy is set but cannot be read, so the router never runs without it.
    /// </summary>
    public SemanticRouterBuilder UseMachinePolicy() => UseMachinePolicy(MachinePolicyLoader.Load());

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

    /// <summary>
    /// Keeps secrets out of the configuration file: API keys, secret headers and gateway keys typed into the dashboard
    /// are encrypted (ASP.NET Core Data Protection) into a separate file and the configuration refers to them as
    /// <c>${secret:…}</c>. Without a <paramref name="path"/>, the file sits next to the configuration file as
    /// <c>{name}.secrets.json</c> (or <c>semantic-router.secrets.json</c> in the current directory).
    /// </summary>
    public SemanticRouterBuilder UseSecretFile(string? path = null)
    {
        Services.AddDataProtection();
        Services.Replace(ServiceDescriptor.Singleton<IRouterSecretStore>(sp =>
        {
            string file = path
                ?? (sp.GetService<IRouterConfigurationStore>() is JsonFileRouterConfigurationStore store
                    ? Path.Combine(Path.GetDirectoryName(store.Location)!, Path.GetFileNameWithoutExtension(store.Location) + ".secrets.json")
                    : "semantic-router.secrets.json");
            return new ProtectedFileSecretStore(file, sp.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>());
        }));
        return this;
    }

    /// <summary>
    /// Keeps the dashboard's evaluation set (labelled prompts) in a JSON Lines file. Without this, the set sits next to
    /// the configuration file as <c>{name}.evaluation.jsonl</c> when <see cref="UseConfigurationFile"/> is used, and in
    /// memory otherwise.
    /// </summary>
    public SemanticRouterBuilder UseEvaluationSetFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Services.Replace(ServiceDescriptor.Singleton<IEvaluationSetStore>(new JsonLinesEvaluationSetStore(path)));
        return this;
    }

    /// <summary>
    /// Reads the local model-family table (families and tiers discovery uses before the size rule) from
    /// <paramref name="path"/>. Without this, it is <c>model-families.json</c> next to the configuration file when
    /// <see cref="UseConfigurationFile"/> is used; only the built-in table otherwise.
    /// </summary>
    public SemanticRouterBuilder UseModelFamilyFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Services.Replace(ServiceDescriptor.Singleton(new ModelFamilyFile(Path.GetFullPath(path))));
        return this;
    }

    /// <summary>
    /// Reads the local client-turn patterns (the wording of agentic clients' own requests, which replaces or adds to the
    /// built-in patterns by id) from <paramref name="path"/>. Without this, it is <c>client-turns.json</c> next to the
    /// configuration file when <see cref="UseConfigurationFile"/> is used; only the built-in patterns otherwise.
    /// </summary>
    public SemanticRouterBuilder UseClientTurnFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Services.Replace(ServiceDescriptor.Singleton(new ClientTurnFile(Path.GetFullPath(path))));
        return this;
    }

    /// <summary>Uses a custom <see cref="IRouterSecretStore"/> (a vault, the OS keychain) for <c>${secret:…}</c> references.</summary>
    public SemanticRouterBuilder UseSecretStore<TStore>()
        where TStore : class, IRouterSecretStore
    {
        Services.Replace(ServiceDescriptor.Singleton<IRouterSecretStore, TStore>());
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

    /// <summary>
    /// Adds a memory source to <c>Memory:Sources</c> (applied after configuration binding), e.g. an Obsidian vault
    /// (<see cref="MemorySourceType.Markdown"/>), Claude Code's memory (<see cref="MemorySourceType.ClaudeCode"/>) or the
    /// MCP memory server's graph (<see cref="MemorySourceType.KnowledgeGraph"/>).
    /// </summary>
    public SemanticRouterBuilder AddMemorySource(string name, MemorySourceType type, string? path = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Services.Configure<MemoryOptions>(o => o.Sources.Add(new MemorySourceOptions { Name = name, Type = type, Path = path }));
        return this;
    }

    /// <summary>Registers a custom <see cref="IMemorySource"/> (singleton), mapped next to the configured sources.</summary>
    public SemanticRouterBuilder AddMemorySource<TSource>()
        where TSource : class, IMemorySource
    {
        Services.AddSingleton<IMemorySource, TSource>();
        return this;
    }

    /// <summary>Registers a custom <see cref="IMemorySource"/> instance, mapped next to the configured sources.</summary>
    public SemanticRouterBuilder AddMemorySource(IMemorySource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Services.AddSingleton(source);
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
