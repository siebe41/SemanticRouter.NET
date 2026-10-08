using Microsoft.Extensions.DependencyInjection.Extensions;
using SemanticRouter.AspNetCore;
using SemanticRouter.Connectors.Copilot;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the GitHub Copilot SDK connector with the router.</summary>
public static class CopilotConnectorBuilderExtensions
{
    /// <summary>
    /// Makes <see cref="ConnectorType.Copilot"/> connectors work: configure one with <c>"Type": "Copilot"</c> (in
    /// configuration, on the dashboard, or with <c>AddConnector</c>). It needs the <c>copilot</c> CLI installed and signed
    /// in; see docs/providers.md.
    /// </summary>
    public static SemanticRouterBuilder AddCopilotConnector(this SemanticRouterBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatConnectorFactory, CopilotConnectorFactory>());
        return builder;
    }
}
