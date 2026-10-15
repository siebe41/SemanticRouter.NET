using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

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
