using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// The router's own outbound connections, and who on this machine counts as local (the <c>Network</c> section). A host
/// setting: read at start-up, not edited on the dashboard. See <c>docs/network.md</c>.
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

    /// <summary>
    /// Offline mode: the router reaches only this machine and <see cref="AllowedHosts"/>. Connectors, the embeddings
    /// endpoint and webhooks elsewhere are turned off, CLI and Copilot connectors (which always reach their vendor) too,
    /// and the ONNX model is never downloaded: place it yourself. A machine policy can force it on. Default off.
    /// </summary>
    public bool Offline { get; set; }

    /// <summary>
    /// For offline mode: other hosts the router may reach, such as an internal model gateway (<c>llm.corp.example</c>,
    /// <c>*.corp.example</c>, <c>10.0.0.5:8443</c>).
    /// </summary>
    public IList<string> AllowedHosts { get; } = new List<string>();

    /// <summary>
    /// Treat a loopback connection from another account on this machine as local, as the router did before it looked:
    /// admin on the dashboard without a token, the gateway without a key, and the CLI connectors. Off by default, so that
    /// on a shared machine (a server with several users, a terminal server, a CI runner) another user is a remote client
    /// and needs a token or a gateway key. Turn it on for a router that runs as its own service account for the people
    /// who sign in to this machine. Linux, macOS and Windows say who opened a connection; root and LocalSystem count as
    /// the router's own account, since they can read its keys anyway.
    /// </summary>
    public bool TrustOtherLocalAccounts { get; set; }

    /// <summary>
    /// The settings as configured, without validating them: a <see cref="CaBundle"/> that cannot be loaded is reported
    /// where the bundle is used, and must not keep offline mode from applying. <see langword="null"/> when not registered.
    /// </summary>
    public static NetworkOptions? Of(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        try
        {
            return services.GetService<IOptions<NetworkOptions>>()?.Value;
        }
        catch (OptionsValidationException)
        {
            var options = new NetworkOptions();
            foreach (IConfigureOptions<NetworkOptions> configure in services.GetServices<IConfigureOptions<NetworkOptions>>())
            {
                configure.Configure(options);
            }

            foreach (IPostConfigureOptions<NetworkOptions> configure in services.GetServices<IPostConfigureOptions<NetworkOptions>>())
            {
                configure.PostConfigure(Options.DefaultName, options);
            }

            return options;
        }
    }
}
