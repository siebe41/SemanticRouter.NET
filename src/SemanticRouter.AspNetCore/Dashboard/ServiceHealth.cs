using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// The router itself, for the dashboard: the machine and process it runs in, the addresses clients point at, and a
/// state summing up whether it can answer them.
/// </summary>
internal static class ServiceHealth
{
    private static readonly TimeSpan NetworkRefresh = TimeSpan.FromMinutes(1);
    private static readonly Lock NetworkLock = new();
    private static (DateTimeOffset At, IReadOnlyList<string> Addresses) _network = (DateTimeOffset.MinValue, []);

    private static readonly DateTimeOffset ProcessStarted = StartTime();

    public static ServiceStatus Build(
        RouterRuntime runtime, IReadOnlyList<EndpointStatus> endpoints, IReadOnlyList<RoutingActivityEntry> recent,
        IEnumerable<string> listening, IEnumerable<string> publicOrigins)
    {
        string[] bound = [.. listening];
        IReadOnlyList<ClientAddress> addresses = Addresses(bound, publicOrigins, HostName(), bound.Any(IsWildcard) ? NetworkAddresses() : []);
        RoutingActivityEntry? last = recent.MaxBy(e => e.Timestamp);
        (string state, string? note, IReadOnlyList<string> problems) = State(
            runtime.WarmedUp, runtime.WarmUpError, runtime.Configuration.Embeddings.WarmUpOnStartup, runtime.Warnings, endpoints);

        return new ServiceStatus(
            Environment.MachineName,
            Environment.ProcessId,
            Environment.ProcessPath,
            ProcessStarted,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            bound,
            addresses,
            addresses.Any(a => !a.Local),
            runtime.Configuration.Middleware.GatewayKeys.Count > 0 || !runtime.Configuration.Middleware.AllowUnauthenticatedRemoteClients,
            runtime.Configuration.Middleware.GatewayKeys.Count > 0,
            state,
            note,
            problems,
            last?.Timestamp,
            last?.Outcome,
            recent.Where(e => e.Outcome == "served").Select(e => (DateTimeOffset?)e.Timestamp).Max());
    }

    /// <summary>
    /// The base addresses clients use, from what the server listens on: a wildcard bind is reached as <c>localhost</c>
    /// here and by the machine's name and network addresses elsewhere. The dashboard's public origins come last.
    /// </summary>
    internal static IReadOnlyList<ClientAddress> Addresses(
        IEnumerable<string> listening, IEnumerable<string> publicOrigins, string hostName, IEnumerable<string> networkAddresses)
    {
        var result = new List<ClientAddress>();
        void Add(string scheme, string host, int port, bool local)
        {
            string authority = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[') ? $"[{host}]" : host;
            bool defaultPort = (scheme == Uri.UriSchemeHttp && port == 80) || (scheme == Uri.UriSchemeHttps && port == 443);
            string url = defaultPort ? $"{scheme}://{authority}" : $"{scheme}://{authority}:{port}";
            if (!result.Any(a => string.Equals(a.Url, url, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add(new ClientAddress(url, local, local || scheme == Uri.UriSchemeHttps));
            }
        }

        foreach (string bound in listening)
        {
            if (!Uri.TryCreate(bound.Replace("://*", "://wildcard", StringComparison.Ordinal).Replace("://+", "://wildcard", StringComparison.Ordinal), UriKind.Absolute, out Uri? uri))
            {
                continue;
            }

            if (IsWildcard(bound))
            {
                Add(uri.Scheme, "localhost", uri.Port, local: true);
                Add(uri.Scheme, hostName, uri.Port, local: false);
                foreach (string address in networkAddresses)
                {
                    Add(uri.Scheme, address, uri.Port, local: false);
                }
            }
            else
            {
                string host = uri.Host.Trim('[', ']');
                bool local = uri.IsLoopback || LocalHosts.IsLocalName(host);
                Add(uri.Scheme, local && IPAddress.TryParse(host, out _) ? "localhost" : host, uri.Port, local);
            }
        }

        foreach (string origin in publicOrigins)
        {
            if (Uri.TryCreate(origin.Trim(), UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                Add(uri.Scheme, uri.Host, uri.Port, local: false);
            }
        }

        return result;
    }

    /// <summary>
    /// Ok, starting (the route index is still being embedded), degraded (warm-up failed, or some connectors are
    /// failing or down) or failing (no enabled model, or every connector in use is failing or down).
    /// </summary>
    internal static (string State, string? Note, IReadOnlyList<string> Problems) State(
        bool warmedUp, string? warmUpError, bool warmUpOnStartup, IEnumerable<string> warnings, IReadOnlyList<EndpointStatus> endpoints)
    {
        var problems = new List<string>();
        EndpointStatus[] used = [.. endpoints.Where(e => e.State != "unused")];
        EndpointStatus[] broken = [.. used.Where(e => e.State is "failing" or "down")];

        if (warmUpError is { } warmUp)
        {
            problems.Add($"The route index could not be embedded: {warmUp}");
        }

        foreach (EndpointStatus e in broken)
        {
            problems.Add($"{e.Connector} is {e.State}{(e.StateNote is null ? "." : ": " + e.StateNote)}");
        }

        problems.AddRange(warnings);

        if (used.Length == 0)
        {
            return ("failing", "No model is enabled, so the router cannot answer requests yet. Set one up on the dashboard's Overview.", problems);
        }

        if (broken.Length == used.Length)
        {
            return ("failing", used.Length == 1 ? "Its only connector is failing or down." : $"All {used.Length} connectors in use are failing or down.", problems);
        }

        if (warmUpError is not null)
        {
            return ("degraded", "The route index could not be embedded.", problems);
        }

        if (broken.Length > 0)
        {
            return ("degraded", $"{broken.Length} of {used.Length} connectors are failing or down; requests fail over to the others.", problems);
        }

        if (!warmedUp && warmUpOnStartup)
        {
            return ("starting", "Embedding the routes; requests wait until it is done.", problems);
        }

        return ("ok", null, problems);
    }

    private static bool IsWildcard(string url) =>
        url.Contains("://*", StringComparison.Ordinal) || url.Contains("://+", StringComparison.Ordinal)
        || url.Contains("://0.0.0.0", StringComparison.Ordinal) || url.Contains("://[::]", StringComparison.Ordinal);

    private static string HostName()
    {
        try
        {
            return Dns.GetHostName();
        }
        catch (SocketException)
        {
            return Environment.MachineName;
        }
    }

    /// <summary>
    /// This machine's IPv4 addresses on interfaces that are up and have a gateway: the network other machines reach
    /// it on, without the virtual switches of WSL, Hyper-V or Docker. Looked up at most once a minute.
    /// </summary>
    private static IReadOnlyList<string> NetworkAddresses()
    {
        lock (NetworkLock)
        {
            if (DateTimeOffset.UtcNow - _network.At < NetworkRefresh)
            {
                return _network.Addresses;
            }

            IReadOnlyList<string> addresses;
            try
            {
                addresses = [.. NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                    .Select(n => n.GetIPProperties())
                    .Where(p => p.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)))
                    .SelectMany(p => p.UnicastAddresses)
                    .Select(u => u.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    .Select(a => a.ToString())
                    .Distinct()];
            }
            catch (NetworkInformationException)
            {
                addresses = [];
            }

            _network = (DateTimeOffset.UtcNow, addresses);
            return addresses;
        }
    }

    private static DateTimeOffset StartTime()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return DateTimeOffset.UtcNow;
        }
    }
}

/// <summary>The router itself: where it runs, the addresses clients use, and how it is doing.</summary>
/// <param name="Machine">The machine's name.</param>
/// <param name="ProcessId">The router's process id.</param>
/// <param name="Program">The executable the router runs as.</param>
/// <param name="StartedAt">When the process started.</param>
/// <param name="Runtime">The .NET runtime.</param>
/// <param name="OperatingSystem">The operating system.</param>
/// <param name="Listening">The addresses the server listens on, as bound (<c>http://[::]:5080</c>).</param>
/// <param name="Addresses">Base addresses clients point at: on this machine, then from other machines.</param>
/// <param name="Remote">Whether other machines can reach it.</param>
/// <param name="GatewayKeyRequired">Whether clients on other machines must send a gateway key (they are refused while none is configured).</param>
/// <param name="GatewayKeysConfigured">Whether any gateway key is configured.</param>
/// <param name="State"><c>ok</c>, <c>starting</c>, <c>degraded</c> or <c>failing</c>.</param>
/// <param name="StateNote">Why, when it is not ok.</param>
/// <param name="Problems">Everything that needs a look: warm-up, connectors failing or down, configuration warnings.</param>
/// <param name="LastRequest">The latest routed request.</param>
/// <param name="LastOutcome">How that one ended (<c>served</c>, <c>failed</c>, …).</param>
/// <param name="LastServed">The latest request a model answered.</param>
public sealed record ServiceStatus(
    string Machine, int ProcessId, string? Program, DateTimeOffset StartedAt, string Runtime, string OperatingSystem,
    IReadOnlyList<string> Listening, IReadOnlyList<ClientAddress> Addresses, bool Remote, bool GatewayKeyRequired, bool GatewayKeysConfigured,
    string State, string? StateNote, IReadOnlyList<string> Problems,
    DateTimeOffset? LastRequest, string? LastOutcome, DateTimeOffset? LastServed);

/// <summary>A base address clients point at.</summary>
/// <param name="Url">Scheme, host and port: Anthropic clients use it as is, OpenAI clients add <c>/v1</c>.</param>
/// <param name="Local">Only reachable from this machine.</param>
/// <param name="Encrypted">Loopback, or HTTPS.</param>
public sealed record ClientAddress(string Url, bool Local, bool Encrypted);
