using Microsoft.Extensions.Logging;

namespace SemanticRouter.AspNetCore;

/// <summary>The connectors built from one configuration snapshot: configured ones plus any <see cref="IChatConnector"/> registered in DI.</summary>
internal sealed class ConnectorCatalog : IDisposable
{
    private readonly Dictionary<string, IChatConnector> _connectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> _owned = [];

    public ConnectorCatalog(
        IReadOnlyDictionary<string, ConnectorOptions> options,
        IEnumerable<IChatConnector> registered,
        IHttpClientFactory httpClientFactory,
        ICliProcessRunner processRunner,
        IToolCallAffinity? affinity,
        ILoggerFactory loggerFactory,
        SecretRedactor? redactor = null,
        PromptRouterMetrics? metrics = null,
        IEnumerable<IChatConnectorFactory>? factories = null,
        MachinePolicy? policy = null,
        PromptStorage storePrompts = PromptStorage.Redacted)
    {
        IChatConnectorFactory[] extra = factories?.ToArray() ?? [];
        foreach ((string name, ConnectorOptions connector) in options)
        {
            if (policy?.Blocks(connector) is { } blocked)
            {
                // Its models are turned off already; this keeps the connector itself from ever starting.
                _connectors[name] = new UnavailableConnector(name, blocked);
            }
            else if (extra.FirstOrDefault(f => connector.IsType(f.Type)) is { } factory)
            {
                IChatConnector created = factory.Create(name, connector);
                if (created is IDisposable disposable)
                {
                    _owned.Add(disposable);
                }

                _connectors[name] = created;
            }
            else if (!ConnectorTypes.IsBuiltIn(connector.Type))
            {
                _connectors[name] = new UnavailableConnector(name, NotInstalled(connector.Type));
            }
            else if (connector.Type == ConnectorTypes.Cli)
            {
                var cli = new CliChatConnector(name, connector, processRunner, loggerFactory.CreateLogger<CliChatConnector>(), affinity, redactor, metrics, storePrompts);
                _owned.Add(cli);
                _connectors[name] = cli;
            }
            else
            {
                _connectors[name] = new OpenAICompatibleConnector(
                    name, connector, httpClientFactory, loggerFactory.CreateLogger<OpenAICompatibleConnector>(), affinity, redactor);
            }
        }

        foreach (IChatConnector connector in registered)
        {
            _connectors[connector.Name] = connector;
        }
    }

    public IReadOnlyCollection<string> Names => _connectors.Keys;

    /// <summary>Why a connector of <paramref name="type"/>, which no factory provides, cannot run.</summary>
    internal static string NotInstalled(string type) =>
        $"no installed connector package provides connector type '{type}' (built in: {string.Join(", ", ConnectorTypes.BuiltIn)})";

    public bool TryGet(string name, out IChatConnector connector) => _connectors.TryGetValue(name, out connector!);

    public void Dispose()
    {
        // Connectors registered in DI are owned (and disposed) by the container.
        foreach (IDisposable disposable in _owned)
        {
            disposable.Dispose();
        }
    }
}

/// <summary>Resolves connectors by name from the current runtime (survives configuration reloads).</summary>
internal sealed class ChatConnectorRegistry
{
    private readonly RouterRuntimeProvider _runtime;

    public ChatConnectorRegistry(RouterRuntimeProvider runtime)
    {
        _runtime = runtime;
    }

    public IReadOnlyCollection<string> Names => _runtime.Current.Connectors.Names;

    public bool TryGet(string name, out IChatConnector connector) => _runtime.Current.Connectors.TryGet(name, out connector);
}
