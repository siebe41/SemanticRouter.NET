using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SemanticRouter.AspNetCore;

/// <summary>Resolves connectors by name: configured ones plus any <see cref="IChatConnector"/> registered in DI.</summary>
internal sealed class ChatConnectorRegistry : IDisposable
{
    private readonly Dictionary<string, IChatConnector> _connectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> _owned = [];

    public ChatConnectorRegistry(
        IOptions<SemanticRouterConnectorsOptions> options,
        IEnumerable<IChatConnector> registered,
        IHttpClientFactory httpClientFactory,
        ICliProcessRunner processRunner,
        ILoggerFactory loggerFactory)
    {
        foreach ((string name, ConnectorOptions connector) in options.Value.Connectors)
        {
            if (connector.Type == ConnectorType.Cli)
            {
                var cli = new CliChatConnector(name, connector, processRunner, loggerFactory.CreateLogger<CliChatConnector>());
                _owned.Add(cli);
                _connectors[name] = cli;
            }
            else
            {
                _connectors[name] = new OpenAICompatibleConnector(
                    name, connector, httpClientFactory, loggerFactory.CreateLogger<OpenAICompatibleConnector>());
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
