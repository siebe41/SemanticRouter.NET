using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Connectors.Copilot;

/// <summary>
/// Creates <see cref="CopilotConnector.Type"/> connectors. The Copilot runtime behind them is shared per executable, home
/// directory and environment, so applying a new configuration does not start another process.
/// </summary>
internal sealed class CopilotConnectorFactory : IChatConnectorFactory, ISecretAwareConnectorFactory, IAsyncDisposable
{
    /// <summary>
    /// The variables the Copilot runtime signs in with (besides its own stored login): they stay in its environment even
    /// when a router setting reads the same token, such as a GitHub Models connector's <c>${env:GITHUB_TOKEN}</c>.
    /// </summary>
    internal static readonly string[] SignInVariables = ["COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN"];

    private readonly ConcurrentDictionary<string, ICopilotBackend> _backends = new(StringComparer.Ordinal);
    private readonly ILoggerFactory _loggers;
    private readonly Func<ConnectorOptions, IReadOnlyDictionary<string, string>, ICopilotBackend>? _create;
    private readonly Func<Dictionary<string, string?>> _inherited;

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
        ICopilotBackend backend = _backends.GetOrAdd(
            $"{options.Executable}\n{options.HomeDirectory}\n{Fingerprint(environment)}",
            _ => _create?.Invoke(options, environment)
                 ?? new SdkCopilotBackend(options.Executable, options.HomeDirectory, environment, _loggers.CreateLogger("SemanticRouter.Connectors.Copilot")));
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
}
