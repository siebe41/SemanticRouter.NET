using Microsoft.Extensions.DependencyInjection.Extensions;
using SemanticRouter.AspNetCore;
using SemanticRouter.Connectors.Copilot;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Registers the GitHub Copilot SDK connector with the router.</summary>
public static class CopilotConnectorBuilderExtensions
{
    /// <summary>
    /// Makes <see cref="CopilotConnector.Type"/> connectors work: configure one with <c>"Type": "Copilot"</c> (in
    /// configuration, on the dashboard, or with <c>AddConnector</c>). It needs the <c>copilot</c> CLI installed and signed
    /// in; see docs/providers.md. The dashboard's setup then offers GitHub Copilot through the SDK instead of the CLI.
    /// </summary>
    public static SemanticRouterBuilder AddCopilotConnector(this SemanticRouterBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IChatConnectorFactory, CopilotConnectorFactory>());
        if (!builder.Services.Any(d => d.ServiceType == typeof(SetupApi.SetupPresetReplacement)
                                       && d.ImplementationInstance is SetupApi.SetupPresetReplacement { Preset.Connector.Type: CopilotConnector.Type }))
        {
            builder.Services.AddSingleton(new SetupApi.SetupPresetReplacement(new SetupApi.SetupPreset(
                "copilot", "GitHub Copilot", "Your GitHub Copilot plan through the Copilot SDK (install the copilot CLI and sign in first)", "cli", "copilot",
                new ConnectorOptions { Type = CopilotConnector.Type, MaxConcurrency = 2 }, Consent: SetupApi.Consent(CliPreset.GitHubCopilot))));
        }

        return builder;
    }
}
