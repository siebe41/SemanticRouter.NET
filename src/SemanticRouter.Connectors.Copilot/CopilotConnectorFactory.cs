using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Connectors.Copilot;

/// <summary>
/// Creates <see cref="CopilotConnector.Type"/> connectors. The Copilot runtime behind them is shared per executable and
/// home directory, so applying a new configuration does not start another process.
/// </summary>
internal sealed class CopilotConnectorFactory : IChatConnectorFactory, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ICopilotBackend> _backends = new(StringComparer.Ordinal);
    private readonly ILoggerFactory _loggers;
    private readonly Func<ConnectorOptions, ICopilotBackend>? _create;

    public CopilotConnectorFactory(ILoggerFactory loggers)
        : this(loggers, null)
    {
    }

    /// <summary>For tests: <paramref name="create"/> supplies the backend instead of the SDK.</summary>
    internal CopilotConnectorFactory(ILoggerFactory loggers, Func<ConnectorOptions, ICopilotBackend>? create)
    {
        _loggers = loggers;
        _create = create;
    }

    public string Type => CopilotConnector.Type;

    public IChatConnector Create(string name, ConnectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ICopilotBackend backend = _backends.GetOrAdd(
            $"{options.Executable}\n{options.HomeDirectory}",
            _ => _create?.Invoke(options) ?? new SdkCopilotBackend(options.Executable, options.HomeDirectory, _loggers.CreateLogger("SemanticRouter.Connectors.Copilot")));
        return new CopilotChatConnector(name, options, backend, _loggers.CreateLogger<CopilotChatConnector>());
    }

    public async ValueTask DisposeAsync()
    {
        foreach (ICopilotBackend backend in _backends.Values)
        {
            if (backend is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
        }

        _backends.Clear();
    }
}
