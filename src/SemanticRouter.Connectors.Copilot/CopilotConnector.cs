namespace SemanticRouter.Connectors.Copilot;

/// <summary>The connector type this package adds (<c>AddCopilotConnector</c>).</summary>
public static class CopilotConnector
{
    /// <summary>
    /// The <see cref="SemanticRouter.AspNetCore.ConnectorOptions.Type"/> of a GitHub Copilot connector: GitHub Copilot through
    /// GitHub's official Copilot SDK, on your own Copilot sign-in, with native streaming, tool calls and model discovery.
    /// It needs the <c>copilot</c> CLI, which the SDK runs as its runtime.
    /// </summary>
    public const string Type = "Copilot";
}
