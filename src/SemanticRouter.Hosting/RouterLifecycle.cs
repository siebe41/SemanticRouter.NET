using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Host;

/// <summary>
/// What the dashboard's Service page may do to the <c>semantic-router</c> tool's own process: change where it listens,
/// restart or stop it, and install or remove the background service. It outlives each run of the web application, so
/// <see cref="RouterHost.RunAsync"/> can stop the router, carry out what the dashboard asked once the port is free, and
/// start the router again in the same process.
/// </summary>
internal sealed class RouterLifecycle
{
    private readonly Lock _gate = new();
    private readonly Func<HostSettings, ServiceDefinition>? _definition;
    private readonly Func<string, string?> _environment;
    private readonly TimeProvider _time;
    private string? _pending;

    public RouterLifecycle(
        HostSettings settings, ServicePlatform? platform, Func<HostSettings, ServiceDefinition>? definition, Func<string, string?> environment,
        TimeProvider? time = null)
    {
        Settings = settings;
        Platform = platform;
        _definition = definition;
        _environment = environment;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>For the tool: this machine's service manager, and the tool itself as the service.</summary>
    public static RouterLifecycle ForThisTool(HostSettings settings) =>
        new(settings, ServicePlatform.ForThisMachine(settings), ServiceDefinition.ForThisTool, Environment.GetEnvironmentVariable);

    public HostSettings Settings { get; }

    public ServicePlatform? Platform { get; }

    /// <summary>How long an install waits for the service's router to listen before this one starts again.</summary>
    public TimeSpan StartTimeout { get; init; } = ServiceCommand.DefaultStartTimeout;

    /// <summary>The configuration's <c>urls</c> when the router last started: where it listens now.</summary>
    public string StartedWith { get; private set; } = HostSettings.DefaultUrl;

    /// <summary>How the last action that left this router running ended; shown on the Service page.</summary>
    public RouterServiceOutcome? LastOutcome { get; private set; }

    /// <summary>The action waiting for the router to stop, if any.</summary>
    public string? Pending
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    /// <summary>In a container the runtime decides the ports and restarts the router; there is no service to install.</summary>
    public bool InContainer => string.Equals(_environment("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The environment variable that sets the listen addresses, if one does (<c>ASPNETCORE_URLS</c>, …).</summary>
    public string? AddressVariable => HostListen.EnvironmentVariable(_environment);

    /// <summary><c>service</c>, <c>container</c> or <c>foreground</c>: see <see cref="RouterServiceDescription.RunsAs"/>.</summary>
    public string RunsAs => InContainer ? "container" : Platform?.RunsThisProcess(_environment) == true ? "service" : "foreground";

    /// <summary>Adds the dashboard's control to the application being built.</summary>
    public void Register(WebApplicationBuilder builder) =>
        builder.Services.AddSingleton<IRouterServiceControl>(services => new RouterServiceControl(
            this, services.GetRequiredService<IHostApplicationLifetime>(), services.GetRequiredService<IConfiguration>()));

    /// <summary>Notes where the router listens, once it has started.</summary>
    public void Started(WebApplication app) => StartedWith = app.Configuration[WebHostDefaults.ServerUrlsKey] ?? HostSettings.DefaultUrl;

    /// <summary>A restart could not listen where the saved setting says, so the router went back to where it was.</summary>
    public void KeptListening(IReadOnlyList<string> previous) => LastOutcome = new RouterServiceOutcome
    {
        Action = RouterServiceActions.Restart,
        Succeeded = false,
        Message = $"Could not listen on {HostListen.NextUrls(this)}: the address is in use. The router listens where it did before ({string.Join(", ", previous)}); free the address or choose another port, then restart.",
        At = _time.GetUtcNow(),
    };

    /// <summary>Records <paramref name="action"/> to carry out once the router has stopped; false when one is already waiting.</summary>
    public bool TryRequest(string action)
    {
        lock (_gate)
        {
            if (_pending is not null)
            {
                return false;
            }

            _pending = action;
            LastOutcome = null;
            return true;
        }
    }

    /// <summary>The action waiting for the router to stop, which is no longer waiting.</summary>
    public string? TakePending()
    {
        lock (_gate)
        {
            string? pending = _pending;
            _pending = null;
            return pending;
        }
    }

    /// <summary>Why <paramref name="action"/> cannot be done now, or <see langword="null"/>.</summary>
    public string? Refusal(string action)
    {
        if (Pending is { } pending)
        {
            return $"The router is already {Doing(pending)}.";
        }

        if (action is RouterServiceActions.Restart or RouterServiceActions.Stop)
        {
            return null;
        }

        if (InContainer)
        {
            return "In a container, the container runtime runs and restarts the router.";
        }

        if (Platform is null)
        {
            return "This operating system has no service manager to run the router in the background.";
        }

        bool installed = Platform.IsInstalled();
        return action switch
        {
            RouterServiceActions.Install when _definition is null => "This program cannot install itself as the background service.",
            RouterServiceActions.Install when installed => "The background service is already installed.",
            RouterServiceActions.Reinstall when _definition is null => "This program cannot install itself as the background service.",
            RouterServiceActions.Reinstall or RouterServiceActions.Uninstall when !installed => "The background service is not installed.",
            RouterServiceActions.Reinstall when Platform is LaunchAgentService && RunsAs == "service" =>
                "launchd would stop this router before it could load the new definition. Run `semantic-router service install` in a terminal instead.",
            _ => null,
        };
    }

    /// <summary>
    /// Carries out <paramref name="action"/> once the router has stopped and let go of its port. Returns whether to start
    /// the router again in this process; when not, this process is done (stopped, or handed over to the service).
    /// </summary>
    public async Task<bool> CarryOutAsync(string action, TextWriter output)
    {
        switch (action)
        {
            case RouterServiceActions.Restart:
                await output.WriteLineAsync("Restarting, as asked on the dashboard.").ConfigureAwait(false);
                return Done(action, true, "Restarted.");

            case RouterServiceActions.Stop:
                await output.WriteLineAsync("Stopped, as asked on the dashboard.").ConfigureAwait(false);
                return false;

            case RouterServiceActions.Uninstall:
                return await UninstallAsync(output).ConfigureAwait(false);

            default:
                return await InstallAsync(action, output).ConfigureAwait(false);
        }
    }

    private async Task<bool> UninstallAsync(TextWriter output)
    {
        ServicePlatform platform = Platform!;
        if (RunsAs == "service")
        {
            // This router is the service's: stopping it through the service manager would end this process before the
            // definition is gone. It exits by itself instead, and a clean exit is not restarted.
            ServiceOutcome removed = await platform.UnregisterAsync().ConfigureAwait(false);
            if (!removed.Succeeded)
            {
                await output.WriteLineAsync($"Could not remove the {platform.Kind}: {removed.Detail}").ConfigureAwait(false);
                return Done(RouterServiceActions.Uninstall, false, $"Could not remove the {platform.Kind}: {removed.Detail} The router started again.");
            }

            await output.WriteLineAsync($"Removed the {platform.Kind}, as asked on the dashboard; the router stopped.").ConfigureAwait(false);
            return false;
        }

        ServiceOutcome outcome = await platform.UninstallAsync().ConfigureAwait(false);
        return outcome.Succeeded
            ? Done(RouterServiceActions.Uninstall, true, $"Removed the {platform.Kind}. This router keeps running until you stop it, and no longer starts when you sign in.")
            : Done(RouterServiceActions.Uninstall, false, $"Could not remove the {platform.Kind}: {outcome.Detail}");
    }

    private async Task<bool> InstallAsync(string action, TextWriter output)
    {
        ServicePlatform platform = Platform!;
        DateTimeOffset requested = _time.GetUtcNow();
        ServiceOutcome outcome = await platform.InstallAsync(Definition()).ConfigureAwait(false);
        if (!outcome.Succeeded)
        {
            await output.WriteLineAsync($"Could not install the {platform.Kind}: {outcome.Detail}").ConfigureAwait(false);
            return Done(action, false, $"Could not install the {platform.Kind}: {outcome.Detail} The router started again here.");
        }

        if (await RouterInstance.WaitForStartAsync(Settings.ConfigurationDirectory, requested, StartTimeout).ConfigureAwait(false) is { } instance)
        {
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"Handed over to the {platform.Kind} ({platform.Location}): its router (pid {instance.ProcessId}) listens on {string.Join(", ", instance.Urls)}.")).ConfigureAwait(false);
            return false;
        }

        await output.WriteLineAsync($"The {platform.Kind}'s router was not listening after {StartTimeout.TotalSeconds:0} seconds; starting again here.").ConfigureAwait(false);
        return Done(action, false,
            $"Installed the {platform.Kind}, but its router was not listening after {StartTimeout.TotalSeconds:0} seconds, so this one started again. See what it logged: {platform.LogHint}");
    }

    /// <summary>
    /// What the service runs: this program, with the host options it runs with. Once a listen address is saved from the
    /// dashboard, a <c>--urls</c> option is left out, so the saved one decides.
    /// </summary>
    internal ServiceDefinition Definition()
    {
        ServiceDefinition definition = _definition!(Settings);
        return HostListen.ReadSaved(Settings.ConfigurationDirectory) is null
            ? definition
            : definition with { HostArguments = HostListen.WithoutUrls(definition.HostArguments) };
    }

    private bool Done(string action, bool succeeded, string message)
    {
        LastOutcome = new RouterServiceOutcome { Action = action, Succeeded = succeeded, Message = message, At = _time.GetUtcNow() };
        return true;
    }

    private static string Doing(string action) => action switch
    {
        RouterServiceActions.Restart => "restarting",
        RouterServiceActions.Stop => "stopping",
        RouterServiceActions.Uninstall => "removing the background service",
        _ => "installing the background service",
    };
}

/// <summary>The dashboard's <see cref="IRouterServiceControl"/> for one run of the router.</summary>
internal sealed class RouterServiceControl(RouterLifecycle lifecycle, IHostApplicationLifetime lifetime, IConfiguration configuration) : IRouterServiceControl
{
    public async Task<RouterServiceDescription> DescribeAsync(CancellationToken cancellationToken = default)
    {
        string? saved = HostListen.ReadSaved(lifecycle.Settings.ConfigurationDirectory);
        string next = HostListen.NextUrls(lifecycle);
        (string scope, int port) = HostListen.Describe(next);
        string? pinnedBy = PinnedBy();
        var listen = new RouterListenDescription
        {
            Scope = scope,
            Port = port,
            Urls = next,
            Saved = saved,
            File = HostListen.FileIn(lifecycle.Settings.ConfigurationDirectory),
            PinnedBy = pinnedBy,
            Editable = Locked() is null,
            RestartNeeded = !HostListen.SameAddresses(next, lifecycle.StartedWith),
        };

        RouterBackgroundService? background = null;
        if (lifecycle.Platform is { } platform && !lifecycle.InContainer)
        {
            bool installed = platform.IsInstalled();
            background = new RouterBackgroundService
            {
                Kind = platform.Kind,
                Installed = installed,
                Status = installed ? await platform.StatusAsync().ConfigureAwait(false) : null,
                Location = platform.Location,
                LogHint = platform.LogHint,
                RestartsOnFailure = platform.RestartsOnFailure,
                Notes = [.. platform.InstallNotes],
            };
        }

        return new RouterServiceDescription
        {
            Listen = listen,
            Background = background,
            RunsAs = lifecycle.RunsAs,
            Actions = [.. RouterServiceActions.All.Where(a => lifecycle.Refusal(a) is null)],
            LastOutcome = lifecycle.LastOutcome,
        };
    }

    public Task<RouterServiceDescription> SetListenAsync(RouterListenChange change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Scope is not ("local" or "network"))
        {
            throw new ArgumentException($"'{change.Scope}' is not a scope: use local (this machine only) or network (other machines too).", nameof(change));
        }

        if (change.Port is < 1 or > 65535)
        {
            throw new ArgumentException($"{change.Port} is not a port: use 1 to 65535.", nameof(change));
        }

        if (Locked() is { } locked)
        {
            throw new InvalidOperationException(locked);
        }

        HostListen.Save(lifecycle.Settings.ConfigurationDirectory, HostListen.Urls(change.Scope, change.Port));
        return DescribeAsync(cancellationToken);
    }

    public string? Refusal(string action) => lifecycle.Refusal(action);

    public void Run(string action)
    {
        if (lifecycle.TryRequest(action))
        {
            lifetime.StopApplication();
        }
    }

    /// <summary>What decides where the router listens instead of the saved setting.</summary>
    private string? PinnedBy()
    {
        if (HostListen.CommandLineUrls(lifecycle.Settings.HostArguments) is { } urls)
        {
            return lifecycle.RunsAs == "service"
                ? $"the --urls {urls} option the background service runs with"
                : $"the --urls {urls} option on the command line";
        }

        return Locked();
    }

    /// <summary>Why the dashboard may not change where the router listens, or <see langword="null"/> when it may.</summary>
    private string? Locked()
    {
        if (lifecycle.InContainer)
        {
            return "the container: its published ports decide who can reach the router";
        }

        if (configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
        {
            return "the Kestrel:Endpoints configuration";
        }

        return lifecycle.AddressVariable is { } variable ? $"the {variable} environment variable" : null;
    }
}

/// <summary>
/// Where the router listens, as the dashboard saves it: <c>Urls</c> in <c>semantic-router.host.json</c> in the
/// configuration directory, read right after <c>appsettings.json</c>, so environment variables and the command line still
/// win over it.
/// </summary>
internal static class HostListen
{
    public const string FileName = "semantic-router.host.json";

    /// <summary>The environment variables that set the addresses (ASP.NET Core reads them all).</summary>
    private static readonly string[] Variables =
        ["ASPNETCORE_URLS", "DOTNET_URLS", "URLS", "ASPNETCORE_HTTP_PORTS", "DOTNET_HTTP_PORTS", "HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS", "DOTNET_HTTPS_PORTS", "HTTPS_PORTS"];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FileIn(string configurationDirectory) => Path.Combine(configurationDirectory, FileName);

    /// <summary>The saved <c>Urls</c>, or <see langword="null"/> when there is none (or the file is unreadable).</summary>
    public static string? ReadSaved(string configurationDirectory)
    {
        try
        {
            JsonNode? root = JsonNode.Parse(File.ReadAllText(FileIn(configurationDirectory)), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return root is JsonObject o && o.FirstOrDefault(p => string.Equals(p.Key, "Urls", StringComparison.OrdinalIgnoreCase)).Value is JsonValue value
                && value.TryGetValue(out string? urls) && !string.IsNullOrWhiteSpace(urls)
                ? urls.Trim()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Saves <paramref name="urls"/> as <c>Urls</c>, keeping anything else in the file.</summary>
    public static void Save(string configurationDirectory, string urls)
    {
        string file = FileIn(configurationDirectory);
        JsonObject root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            root = [];
        }

        foreach (string key in root.Select(p => p.Key).Where(k => string.Equals(k, "Urls", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            root.Remove(key);
        }

        root["Urls"] = urls;
        Directory.CreateDirectory(configurationDirectory);
        File.WriteAllText(file, root.ToJsonString(Json) + Environment.NewLine);
        OwnerOnly.Restrict(file);
    }

    /// <summary>This machine only (<c>localhost</c>), or every network interface (<c>0.0.0.0</c>).</summary>
    public static string Urls(string scope, int port) =>
        string.Create(CultureInfo.InvariantCulture, $"http://{(scope == "network" ? "0.0.0.0" : "localhost")}:{port}");

    /// <summary>
    /// Where the router will listen at its next start: as now when the command line or something else decides, else the
    /// saved addresses.
    /// </summary>
    public static string NextUrls(RouterLifecycle lifecycle) =>
        CommandLineUrls(lifecycle.Settings.HostArguments) is null && ReadSaved(lifecycle.Settings.ConfigurationDirectory) is { } saved
            && !lifecycle.InContainer && lifecycle.AddressVariable is null
            ? saved
            : lifecycle.StartedWith;

    /// <summary>
    /// <c>local</c> when every address is this machine's loopback, <c>network</c> when every one is a wildcard, else
    /// <c>custom</c>; and the first port given.
    /// </summary>
    public static (string Scope, int Port) Describe(string urls)
    {
        (string Scheme, string Host, int Port)[] parsed = [.. Parse(urls)];
        string scope = parsed.Length > 0 && parsed.All(p => p.Host == "*") ? "network"
            : parsed.Length > 0 && parsed.All(p => p.Host == "localhost") ? "local"
            : "custom";
        return (scope, parsed.Length > 0 ? parsed[0].Port : 5080);
    }

    /// <summary>Whether two <c>urls</c> values listen on the same addresses (any wildcard and any loopback name being alike).</summary>
    public static bool SameAddresses(string a, string b) =>
        Parse(a).Select(p => (p.Host, p.Port, p.Scheme)).ToHashSet().SetEquals(Parse(b).Select(p => (p.Host, p.Port, p.Scheme)));

    /// <summary>The value of <c>--urls</c> (or <c>--http_ports</c>, <c>--https_ports</c>) on the command line.</summary>
    public static string? CommandLineUrls(IReadOnlyList<string> hostArguments)
    {
        string? found = null;
        for (int i = 0; i < hostArguments.Count; i++)
        {
            foreach (string option in (string[])["--urls", "--http_ports", "--https_ports"])
            {
                if (string.Equals(hostArguments[i], option, StringComparison.OrdinalIgnoreCase) && i + 1 < hostArguments.Count)
                {
                    found = hostArguments[i + 1];
                }
                else if (hostArguments[i].StartsWith(option + "=", StringComparison.OrdinalIgnoreCase))
                {
                    found = hostArguments[i][(option.Length + 1)..];
                }
            }
        }

        return found;
    }

    /// <summary><paramref name="hostArguments"/> without <c>--urls</c> and its value.</summary>
    public static IReadOnlyList<string> WithoutUrls(IReadOnlyList<string> hostArguments)
    {
        var kept = new List<string>();
        for (int i = 0; i < hostArguments.Count; i++)
        {
            if (string.Equals(hostArguments[i], "--urls", StringComparison.OrdinalIgnoreCase))
            {
                i++;
            }
            else if (!hostArguments[i].StartsWith("--urls=", StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(hostArguments[i]);
            }
        }

        return kept;
    }

    /// <summary>The first environment variable that sets the addresses, or <see langword="null"/>.</summary>
    public static string? EnvironmentVariable(Func<string, string?> environment) =>
        Variables.FirstOrDefault(name => !string.IsNullOrWhiteSpace(environment(name)));

    private static IEnumerable<(string Scheme, string Host, int Port)> Parse(string urls)
    {
        foreach (string url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string normal = url.Replace("://*", "://wildcard", StringComparison.Ordinal).Replace("://+", "://wildcard", StringComparison.Ordinal);
            if (!Uri.TryCreate(normal, UriKind.Absolute, out Uri? uri))
            {
                continue;
            }

            string host = uri.Host.Trim('[', ']').ToLowerInvariant();
            host = host is "wildcard" or "0.0.0.0" or "::" ? "*"
                : host is "localhost" or "::1" || host.StartsWith("127.", StringComparison.Ordinal) ? "localhost"
                : host;
            yield return (uri.Scheme.ToLowerInvariant(), host, uri.Port);
        }
    }
}
