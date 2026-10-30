using Microsoft.AspNetCore.Http;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Creates the connectors of a type that a separate package provides (for example <c>Copilot</c>, from
/// <c>SemanticRouter.Connectors.Copilot</c>): configuration selects it with <c>"Type": "&lt;its name&gt;"</c>. Register
/// one in DI; the router asks it for a connector every time a configuration is applied, so expensive resources
/// (processes, clients) belong in the factory and are shared.
/// </summary>
/// <remarks>
/// The router treats a connector of such a type as one that runs a program, like a <see cref="ConnectorTypes.Cli"/>
/// connector, unless the factory declares <see cref="UsesBaseAddress"/>: an offline machine policy turns it off, and
/// unless <see cref="ConnectorOptions.AllowRemoteClients"/> is set the router does not send it requests from other
/// machines, so the connector need not check that itself. Limits on concurrent runs and the queue
/// (<see cref="ConnectorOptions.MaxConcurrency"/>, <see cref="ConnectorOptions.MaxQueue"/>) are the connector's to apply.
/// </remarks>
public interface IChatConnectorFactory
{
    /// <summary>
    /// The <see cref="ConnectorOptions.Type"/> this factory creates, such as <c>Copilot</c>. Matched case-insensitively;
    /// a factory for one of <see cref="ConnectorTypes.BuiltIn"/> replaces the built-in implementation.
    /// </summary>
    string Type { get; }

    /// <summary>
    /// Whether connectors of this type send every request to their <see cref="ConnectorOptions.BaseAddress"/> and nowhere
    /// else. Then the router treats them as HTTP connectors: the machine policy's endpoint rules, offline mode and local
    /// hosts check that address, the configuration must give one, and the default timeout is 100 seconds. Default
    /// <see langword="false"/>: they reach their vendor by their own means (a program, an SDK), like a
    /// <see cref="ConnectorTypes.Cli"/> connector. Offline mode turns them off, they never count as local under a policy's
    /// local hosts, they count as a subscription for prices, and the default timeout is 5 minutes.
    /// </summary>
    bool UsesBaseAddress => false;

    /// <summary>Creates the connector called <paramref name="name"/> with <paramref name="options"/>.</summary>
    IChatConnector Create(string name, ConnectorOptions options);
}

/// <summary>
/// Implemented by connectors that can list the provider's models, for the dashboard's <b>Discover</b> button.
/// </summary>
public interface IModelDiscovery
{
    /// <summary>The model ids the provider offers to this sign-in or key.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken);
}

/// <summary>A configured connector whose implementation is not installed: every request skips it, with the reason.</summary>
internal sealed class UnavailableConnector(string name, string reason) : IChatConnector
{
    public string Name => name;

    public Task<ConnectorResult> SendAsync(ConnectorRequest request, HttpContext context, CancellationToken cancellationToken) =>
        Task.FromResult(ConnectorResult.Skipped(reason));
}
