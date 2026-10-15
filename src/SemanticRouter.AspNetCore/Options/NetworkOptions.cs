namespace SemanticRouter.AspNetCore;

/// <summary>
/// The router's own outbound connections (the <c>Network</c> section). A host setting: read at start-up, not edited on
/// the dashboard. See <c>docs/network.md</c>.
/// </summary>
public sealed class NetworkOptions
{
    /// <summary>
    /// A PEM file of CA certificates to trust for the router's own HTTPS connections (connectors, the embeddings
    /// endpoint, webhooks and the ONNX model download), in addition to the operating system's store: for a corporate
    /// proxy that inspects TLS when its root is not in that store, as in most containers. A relative path is resolved
    /// against the content root (the configuration directory for the <c>semantic-router</c> tool). Subscription CLIs
    /// are separate programs and do not use it.
    /// </summary>
    public string? CaBundle { get; set; }
}
