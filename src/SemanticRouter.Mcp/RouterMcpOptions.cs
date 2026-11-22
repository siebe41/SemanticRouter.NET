namespace SemanticRouter.Mcp;

/// <summary>
/// Options for the router's MCP server (<c>AddRouterMcpServer</c>, <c>MapRouterMcp</c>). Bound from
/// <c>SemanticRouter:Mcp</c>; the <c>configure</c> callback of <c>AddRouterMcpServer</c> runs after. Read when the
/// endpoint is mapped, at start-up. See <c>docs/clients.md#the-routers-mcp-server</c>.
/// </summary>
public sealed class RouterMcpOptions
{
    /// <summary>
    /// Serve the MCP endpoint. Default <see langword="false"/>: <c>MapRouterMcp</c> maps nothing, and the path answers
    /// <c>404</c>. Callers are admitted as the dashboard admits them (loopback, or a dashboard token), with the same roles.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Offer the <c>propose_tuning</c> tool to admin callers: it replays a proposed threshold, utterance or route-model
    /// change over recent requests and records it as a suggestion on the Router learned page, where a person applies or
    /// dismisses it. It never applies anything itself. Default <see langword="false"/>: the server offers read-only tools.
    /// </summary>
    public bool AllowProposals { get; set; }
}
