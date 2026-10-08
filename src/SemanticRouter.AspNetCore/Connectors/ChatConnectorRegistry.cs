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
        SecretRedactor? redactor = null)
    {
        foreach ((string name, ConnectorOptions connector) in options)
        {
            if (connector.Type == ConnectorType.Cli)
            {
                var cli = new CliChatConnector(name, connector, processRunner, loggerFactory.CreateLogger<CliChatConnector>(), affinity, redactor);
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
