using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Connectors.Copilot;

/// <summary>
/// Creates <see cref="CopilotConnector.Type"/> connectors. The Copilot runtime behind them is shared per executable, home
/// directory and environment, so applying a new configuration does not start another process.
/// </summary>
/// <remarks>
/// A runtime is stopped once nothing uses it. Each connector holds its runtime until the connector is disposed (another
/// configuration replaced it, or a dashboard draft's Discover is over), and each request or model listing holds it while
/// it runs, so a request that started before a configuration change finishes on the runtime it started on. A changed
/// environment (an edited variable, a new secret) therefore does not leave the previous runtime process running.
/// </remarks>
internal sealed partial class CopilotConnectorFactory : IChatConnectorFactory, ISecretAwareConnectorFactory, IAsyncDisposable
{
    /// <summary>
    /// The variables the Copilot runtime signs in with (besides its own stored login): they stay in its environment even
    /// when a router setting reads the same token, such as a GitHub Models connector's <c>${env:GITHUB_TOKEN}</c>.
    /// </summary>
    internal static readonly string[] SignInVariables = ["COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN"];

    private readonly Dictionary<string, SharedBackend> _backends = new(StringComparer.Ordinal);
    private readonly HashSet<Task> _stopping = [];
    private readonly Lock _lock = new();
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly Func<ConnectorOptions, IReadOnlyDictionary<string, string>, ICopilotBackend>? _create;
    private readonly Func<Dictionary<string, string?>> _inherited;
    private bool _disposed;

    public CopilotConnectorFactory(ILoggerFactory loggers)
        : this(loggers, null)
    {
    }

    /// <summary>
    /// For tests: <paramref name="create"/> supplies the backend (given the runtime's environment) instead of the SDK, and
    /// <paramref name="inherited"/> the router's environment instead of the process's.
    /// </summary>
    internal CopilotConnectorFactory(
        ILoggerFactory loggers,
        Func<ConnectorOptions, IReadOnlyDictionary<string, string>, ICopilotBackend>? create,
        Func<Dictionary<string, string?>>? inherited = null)
    {
        _loggers = loggers;
        _logger = loggers.CreateLogger("SemanticRouter.Connectors.Copilot");
        _create = create;
        _inherited = inherited ?? CliEnvironment.Inherited;
    }

    public string Type => CopilotConnector.Type;

    public IChatConnector Create(string name, ConnectorOptions options) => Create(name, options, SecretRedactor.None);

    public IChatConnector Create(string name, ConnectorOptions options, SecretRedactor secrets)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secrets);
        IReadOnlyDictionary<string, string> environment = RuntimeEnvironment(options, secrets.Values, _inherited());
        string key = $"{options.Executable}\n{options.HomeDirectory}\n{Fingerprint(environment)}";
        return new CopilotChatConnector(
            name,
            options,
            () => Acquire(key, () => _create?.Invoke(options, environment)
                ?? new SdkCopilotBackend(options.Executable, options.HomeDirectory, environment, _logger)),
            _loggers.CreateLogger<CopilotChatConnector>());
    }

    public async ValueTask DisposeAsync()
    {
        SharedBackend[] remaining;
        Task[] stopping;
        lock (_lock)
        {
            _disposed = true;
            remaining = [.. _backends.Values];
            _backends.Clear();
            foreach (SharedBackend shared in remaining)
            {
                shared.Stopped = true;
            }

            stopping = [.. _stopping];
        }

        foreach (SharedBackend shared in remaining)
        {
            if (shared.Backend is IAsyncDisposable disposable)
            {
                await disposable.DisposeAsync().ConfigureAwait(false);
            }
        }

        await Task.WhenAll(stopping).ConfigureAwait(false);
    }

    /// <summary>
    /// The environment the Copilot runtime is started with, in place of the router's own: <paramref name="inherited"/>
    /// filtered as for a CLI connector (<see cref="CliEnvironment.Apply"/>: the connector's <see cref="ConnectorOptions.RemoveEnvironmentVariables"/>,
    /// the router's settings, provider keys and variables holding one of its <paramref name="secrets"/>), then the
    /// connector's <see cref="ConnectorOptions.EnvironmentVariables"/>. <c>PATH</c>, <c>HOME</c>, proxy and certificate
    /// variables and the runtime's own sign-in variables (<see cref="SignInVariables"/>) pass on.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> RuntimeEnvironment(
        ConnectorOptions options, IEnumerable<string> secrets, Dictionary<string, string?> inherited)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(inherited);
        CliEnvironment.Apply(inherited, options.RemoveEnvironmentVariables ?? [], secrets, options.EnvironmentVariables, SignInVariables);
        return inherited
            .Where(v => v.Value is not null)
            .ToDictionary(v => v.Key, v => v.Value!, inherited.Comparer);
    }

    /// <summary>Identifies an environment without keeping its values (some are tokens) in the backend's key.</summary>
    private static string Fingerprint(IReadOnlyDictionary<string, string> environment)
    {
        var text = new StringBuilder();
        foreach ((string name, string value) in environment.OrderBy(v => v.Key, StringComparer.Ordinal))
        {
            text.Append(name).Append('=').Append(value).Append('\0');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// A hold on the runtime for <paramref name="key"/>, started with <paramref name="create"/> when nothing holds one.
    /// The runtime is stopped when the last hold on it is let go of.
    /// </summary>
    private BackendLease Acquire(string key, Func<ICopilotBackend> create)
    {
        lock (_lock)
        {
            if (!_backends.TryGetValue(key, out SharedBackend? shared))
            {
                shared = new SharedBackend(create());
                if (!_disposed)
                {
                    // Once the factory is disposed (the router is stopping), a late request gets a runtime of its own,
                    // stopped when it lets go of it.
                    _backends[key] = shared;
                }
            }

            shared.Users++;
            return new BackendLease(shared.Backend, () => Release(key, shared));
        }
    }

    private void Release(string key, SharedBackend shared)
    {
        lock (_lock)
        {
            if (--shared.Users > 0 || shared.Stopped)
            {
                return;
            }

            shared.Stopped = true;
            if (_backends.TryGetValue(key, out SharedBackend? current) && current == shared)
            {
                _backends.Remove(key);
            }

            if (shared.Backend is IAsyncDisposable disposable)
            {
                // Stopping a runtime can take seconds: whoever let go of it (a request, a configuration change) does not
                // wait for it, and DisposeAsync does.
                Task stop = StopAsync(disposable);
                if (!stop.IsCompleted)
                {
                    _stopping.Add(stop);
                    _ = stop.ContinueWith(
                        done =>
                        {
                            lock (_lock)
                            {
                                _stopping.Remove(done);
                            }
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
        }
    }

    private async Task StopAsync(IAsyncDisposable backend)
    {
        try
        {
            await Task.Yield(); // out of the caller's lock
            await backend.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogStopFailed(_logger, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A Copilot runtime that is no longer used could not be stopped: {Error}")]
    private static partial void LogStopFailed(ILogger logger, string error);

    /// <summary>A runtime, and how many connectors, requests and model listings hold it.</summary>
    private sealed class SharedBackend(ICopilotBackend backend)
    {
        public ICopilotBackend Backend { get; } = backend;

        public int Users { get; set; }

        public bool Stopped { get; set; }
    }
}

/// <summary>A hold on a Copilot runtime (<see cref="Backend"/>); disposing it lets go of the runtime, once.</summary>
internal sealed class BackendLease(ICopilotBackend backend, Action release) : IDisposable
{
    private Action? _release = release;

    public ICopilotBackend Backend { get; } = backend;

    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
}
