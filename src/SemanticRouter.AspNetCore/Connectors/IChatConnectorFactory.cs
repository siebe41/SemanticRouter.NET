using Microsoft.AspNetCore.Http;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Creates the connector for a <see cref="ConnectorType"/> that a separate package provides (for example
/// <see cref="ConnectorType.Copilot"/>). Register one in DI; the router asks it for a connector every time a
/// configuration is applied, so expensive resources (processes, clients) belong in the factory and are shared.
/// </summary>
public interface IChatConnectorFactory
{
    /// <summary>The connector type this factory creates.</summary>
    ConnectorType Type { get; }

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
