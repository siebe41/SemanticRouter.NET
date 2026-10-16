using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Options;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Host;

/// <summary>Builds and runs the router web application.</summary>
internal static partial class RouterHost
{
    private const string TemplateResource = "semantic-router.template.json";

    /// <summary>The embedded neutral configuration (the <c>SemanticRouter</c> section's content).</summary>
    public static string Template() => Resource(TemplateResource);

    /// <summary>Labelled prompts for the template's routes, written next to the configuration by <c>init</c>.</summary>
    public static string EvaluationTemplate() => Resource("semantic-router.evaluation.jsonl");

    private static string Resource(string name)
    {
        using Stream stream = typeof(RouterHost).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The embedded {name} is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string Version =>
        (typeof(RouterHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev").Split('+')[0];

    /// <summary>
    /// Creates the application: the template as the lowest-precedence configuration, then <c>appsettings.json</c> in
    /// the configuration directory, environment variables (<c>SemanticRouter__…</c>) and the command line; the saved
    /// configuration file on top, as the dashboard leaves it.
    /// </summary>
    public static WebApplication Build(HostSettings settings, Action<WebApplicationBuilder>? configure = null)
    {
        // Configuration, state with prompt previews, the evaluation set and logs: for this user only.
        OwnerOnly.Directory(settings.ConfigurationDirectory);
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // The router has no hosting-startup assemblies. Looking for one named after the application fails in the
            // desktop app, whose assembly is not called semantic-router; it is read while the builder is created.
            Args = [$"--{WebHostDefaults.PreventHostingStartupKey}=true", .. settings.HostArguments],
            ApplicationName = "semantic-router",
            ContentRootPath = settings.ConfigurationDirectory,
        });

        // Below everything else, so appsettings.json or Logging__LogLevel__… can turn any of it back up. Added before
        // the template: adding a source reloads the others, and the template's stream can be read only once.
        builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = QuietLogging });
        builder.Configuration.Sources.Insert(0, new JsonStreamConfigurationSource
        {
            Stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"SemanticRouter\":" + Template() + "}")),
        });

        var starting = new StartingQuietly();
        builder.Services.AddSingleton(starting);

        // While Kestrel binds, the generic host logs a failed start with its stack trace before StartAsync throws; the
        // start-up code below explains the one failure people meet (an address in use) itself.
        builder.Logging.AddFilter(HostCategory, level => level >= LogLevel.Warning && (!starting.On || level >= LogLevel.Critical));

        if (string.IsNullOrEmpty(builder.Configuration[WebHostDefaults.ServerUrlsKey])
            && string.IsNullOrEmpty(builder.Configuration[WebHostDefaults.HttpPortsKey])
            && string.IsNullOrEmpty(builder.Configuration[WebHostDefaults.HttpsPortsKey]))
        {
            builder.WebHost.UseUrls(HostSettings.DefaultUrl);
        }

        // A fixed application name keeps the data-protection keys (and so the encrypted secrets) readable whatever
        // directory the tool runs from; SEMANTIC_ROUTER_KEYS_DIR keeps them on a volume in containers. KeyRing decides
        // how they are encrypted at rest: DPAPI on Windows, or SemanticRouter:DataProtection:Certificate anywhere.
        IDataProtectionBuilder dataProtection = builder.Services.AddDataProtection().SetApplicationName("semantic-router");
        KeyRing keyRing = KeyRing.Configure(dataProtection, builder.Configuration, settings.ConfigurationDirectory, settings.KeysDirectory);

        builder.Services
            .AddSemanticPromptRouting(builder.Configuration.GetSection(SemanticRouterOptions.SectionName))
            .UseConfigurationFile(settings.ConfigurationFile)
            .UseMachinePolicy()
            .UseSecretFile()
            .AddOnnxEmbeddings()
            .AddCopilotConnector()
            .UseSqliteState(Path.Combine(settings.ConfigurationDirectory, HostSettings.StateFileName));

        builder.Services.AddSingleton<IRouterSetupDetector, SetupDetector>(); // the dashboard's first-run setup finds what init would
        bool behindProxy = ConfigureForwardedHeaders(builder);
        configure?.Invoke(builder);

        WebApplication app = builder.Build();
        bool replacedKey = keyRing.ReplaceUnencryptedDefaultKey(
            app.Services.GetRequiredService<IKeyManager>(),
            app.Services.GetRequiredService<IOptions<KeyManagementOptions>>().Value.NewKeyLifetime,
            DateTimeOffset.UtcNow);
        keyRing.LogAtStart(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SemanticRouter.Host.KeyRing"), replacedKey);
        if (behindProxy)
        {
            // Also notes which requests came straight from a known proxy: only those may name the dashboard user.
            app.UseRouterForwardedHeaders();
        }

        app.UsePromptRouter();
        app.MapRouterModels();
        app.MapAnthropicTokenCount();
        app.MapPromptClassification();
        app.MapRouterDashboard("/router");
        app.MapRouterDashboardRedirect("/router");
        return app;
    }

    /// <summary>The generic host's own log category: it reports a failed start, and a background service that failed.</summary>
    private const string HostCategory = "Microsoft.Extensions.Hosting.Internal.Host";

    /// <summary>
    /// Default log levels for the tool: ASP.NET Core's lines per request, Kestrel's binding notes and the hosting
    /// lifetime's "Application started" add nothing to the router's own line per request and the banner, and data
    /// protection's notes about where its keys live and that they are not encrypted are what <see cref="KeyRing"/>
    /// reports, once, in plain words. On Windows the host also writes to the Event Log, whose own Warning filter outranks
    /// a category level that names no provider, so data protection is quieted there by name too.
    /// </summary>
    private static readonly Dictionary<string, string?> QuietLogging = new()
    {
        ["Logging:LogLevel:Microsoft.AspNetCore"] = "Warning",
        ["Logging:LogLevel:Microsoft.Hosting.Lifetime"] = "Warning",
        ["Logging:LogLevel:Microsoft.AspNetCore.DataProtection"] = "Error",
        ["Logging:EventLog:LogLevel:Microsoft.AspNetCore.DataProtection"] = "Error",
        ["Logging:LogLevel:System.Net.Http.HttpClient"] = "Warning",
    };

    /// <summary>Set while the web host starts, to keep its failed-start report (a stack trace) off the console.</summary>
    private sealed class StartingQuietly
    {
        public volatile bool On;
    }

    /// <summary>The configuration section that names the reverse proxies whose forwarded headers are trusted.</summary>
    public const string ForwardedHeadersSection = "SemanticRouter:ForwardedHeaders";

    /// <summary>
    /// Behind a reverse proxy every request arrives from the proxy, so per-client rate limits, the activity log and the
    /// local-client checks would all see one address. With <c>SemanticRouter:ForwardedHeaders:KnownProxies</c> (addresses)
    /// or <c>KnownNetworks</c> (CIDR ranges) set, <c>X-Forwarded-For</c> and <c>X-Forwarded-Proto</c> from those proxies,
    /// and only those, give the real client address and scheme. <c>X-Forwarded-Host</c> is not taken: the Host header
    /// check against DNS rebinding keeps seeing what the browser sent. Returns whether any proxy is configured.
    /// </summary>
    internal static bool ConfigureForwardedHeaders(WebApplicationBuilder builder)
    {
        IConfigurationSection section = builder.Configuration.GetSection(ForwardedHeadersSection);
        string[] proxies = section.GetSection("KnownProxies").Get<string[]>() ?? [];
        string[] networks = section.GetSection("KnownNetworks").Get<string[]>() ?? [];
        if (proxies.Length == 0 && networks.Length == 0)
        {
            return false;
        }

        IPAddress[] proxyAddresses = [.. proxies.Select(p => IPAddress.TryParse(p.Trim(), out IPAddress? address)
            ? address
            : throw new InvalidOperationException($"{ForwardedHeadersSection}:KnownProxies: '{p}' is not an IP address."))];
        System.Net.IPNetwork[] proxyNetworks = [.. networks.Select(n => System.Net.IPNetwork.TryParse(n.Trim(), out System.Net.IPNetwork network)
            ? network
            : throw new InvalidOperationException($"{ForwardedHeadersSection}:KnownNetworks: '{n}' is not a CIDR range such as 10.0.0.0/8."))];
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (IPAddress address in proxyAddresses)
            {
                options.KnownProxies.Add(address);
            }

            foreach (System.Net.IPNetwork network in proxyNetworks)
            {
                options.KnownIPNetworks.Add(network);
            }
        });
        return true;
    }

    /// <summary>The exit code when the listen address is taken; a service manager does not restart on it.</summary>
    public const int AddressInUseExitCode = 3;

    public static async Task<int> RunAsync(HostSettings settings, TextWriter output, Action<WebApplicationBuilder>? configure = null)
    {
        await using RunningRouter? router = await StartAsync(settings, output, configure).ConfigureAwait(false);
        if (router is null)
        {
            return AddressInUseExitCode;
        }

        await WriteBannerAsync(router.App, settings, output).ConfigureAwait(false);
        await router.App.WaitForShutdownAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Starts the router and records it in <c>semantic-router.instance.json</c>. Returns <see langword="null"/> after
    /// explaining it on <paramref name="output"/> when the address is already taken. Disposing the result stops it.
    /// </summary>
    /// <param name="environment">Reads environment variables (the API keys a container seeds connectors from); the process's by default.</param>
    public static async Task<RunningRouter?> StartAsync(
        HostSettings settings, TextWriter output, Action<WebApplicationBuilder>? configure = null, Func<string, string?>? environment = null)
    {
        // Only the start that creates the file seeds connectors from the API keys: on a later start, a template without them
        // may be one someone deleted them from, and that must stick.
        if (!File.Exists(settings.ConfigurationFile))
        {
            RouterConfiguration fresh = RouterConfiguration.FromJson(Template());
            IReadOnlyList<string> seeded = FirstRun.SeedFromEnvironment(fresh, environment ?? Environment.GetEnvironmentVariable);
            new JsonFileRouterConfigurationStore(settings.ConfigurationFile).Save(fresh);
            await output.WriteLineAsync(FirstRun.Describe(settings.ConfigurationFile, seeded)).ConfigureAwait(false);
        }

        WebApplication app = Build(settings, configure);
        StartingQuietly starting = app.Services.GetRequiredService<StartingQuietly>();
        starting.On = true;
        try
        {
            await app.StartAsync().ConfigureAwait(false);
        }
        catch (IOException ex) when (ex.InnerException is AddressInUseException)
        {
            await app.DisposeAsync().ConfigureAwait(false);
            await output.WriteLineAsync(AddressInUse(ex.Message, RouterInstance.ReadRunning(settings.ConfigurationDirectory))).ConfigureAwait(false);
            return null;
        }
        finally
        {
            starting.On = false;
        }

        var instance = new RouterInstance(Environment.ProcessId, [.. app.Urls], DateTimeOffset.UtcNow, Version);
        instance.Write(settings.ConfigurationDirectory);
        IDisposable? stopListener = OperatingSystem.IsWindows() ? RouterInstance.ListenForStop(app.Lifetime) : null;
        return new RunningRouter(app, instance, settings.ConfigurationDirectory, stopListener);
    }

    /// <summary>
    /// Why the router could not listen, without a stack trace: when the router recorded for this configuration
    /// directory is the one listening on the address in use, where its dashboard is; otherwise that another program
    /// holds the address (the recorded router may listen elsewhere, while something else took this one).
    /// </summary>
    internal static string AddressInUse(string problem, RouterInstance? running)
    {
        if (running is null || InUse(problem) is not { } address || !running.Urls.Any(u => SameListenAddress(u, address)))
        {
            return $"{problem} Another program is listening there, or a router for another configuration directory " +
                   "(`semantic-router service status` shows the background one). Stop it, or pick another address with --urls, e.g. --urls http://localhost:5081.";
        }

        string url = LocalAddress(running.Urls.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? (running.Urls.Count > 0 ? running.Urls[0] : null) ?? HostSettings.DefaultUrl);
        return $"SemanticRouter is already running for this configuration (process {running.ProcessId}, version {running.Version}): " +
               $"its dashboard is {url}/router. Use that one, or stop it first (`semantic-router service stop` when it runs in the background).";
    }

    /// <summary>The address Kestrel says is taken (<c>Failed to bind to address http://127.0.0.1:5080: address already in use.</c>).</summary>
    private static string? InUse(string problem) => BoundAddress().Match(problem) is { Success: true } match ? match.Groups["url"].Value : null;

    /// <summary>
    /// Whether two listen addresses take the same port: the same port and the same host, or a wildcard or this machine's
    /// name on either side.
    /// </summary>
    internal static bool SameListenAddress(string a, string b)
    {
        if (ListenAddress().Match(a) is not { Success: true } first || ListenAddress().Match(b) is not { Success: true } second
            || Port(first) != Port(second))
        {
            return false;
        }

        string one = first.Groups["host"].Value, other = second.Groups["host"].Value;
        return string.Equals(one, other, StringComparison.OrdinalIgnoreCase) || IsWildcard(one) || IsWildcard(other) || (IsLoopback(one) && IsLoopback(other));

        static int Port(Match match) => match.Groups["port"].Success
            ? int.Parse(match.Groups["port"].Value, CultureInfo.InvariantCulture)
            : string.Equals(match.Groups["scheme"].Value, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;

        static bool IsWildcard(string host) => host is "*" or "+" or "0.0.0.0" or "[::]";

        static bool IsLoopback(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "[::1]" || host.StartsWith("127.", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"address (?<url>[A-Za-z]+://\S+?):?\s")]
    private static partial Regex BoundAddress();

    [GeneratedRegex(@"^(?<scheme>[A-Za-z]+)://(?<host>\[[^\]]*\]|[^:/\[]+)(?::(?<port>\d+))?")]
    private static partial Regex ListenAddress();

    private static async Task WriteBannerAsync(WebApplication app, HostSettings settings, TextWriter output)
    {
        RouterRuntime runtime = app.Services.GetRequiredService<RouterRuntimeProvider>().Current;
        await output.WriteLineAsync(Banner(Version, [.. app.Urls], settings.ConfigurationFile, runtime.Configuration, OperatingSystem.IsWindows())).ConfigureAwait(false);
    }

    /// <summary>
    /// What the router prints once it listens: where, the dashboard, and either how to try it (a request, where to watch
    /// it, the Claude Code command) or, while no model is enabled, where to set one up.
    /// </summary>
    internal static string Banner(string version, IReadOnlyList<string> urls, string configurationFile, RouterConfiguration configuration, bool windows)
    {
        string url = urls.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? (urls.Count > 0 ? urls[0] : null) ?? HostSettings.DefaultUrl;
        string local = LocalAddress(url);
        string dashboard = local + "/router";
        var banner = new StringBuilder()
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"SemanticRouter {version} is listening on {string.Join(", ", urls)}")
            .AppendLine(CultureInfo.InvariantCulture, $"  Configuration  {configurationFile}")
            .AppendLine(CultureInfo.InvariantCulture, $"  Dashboard      {dashboard}");

        bool dispatch = configuration.Middleware.Mode == PromptRouterMode.Dispatch;
        if (dispatch && !configuration.Models.Any(m => m.Enabled))
        {
            banner
                .AppendLine()
                .AppendLine("  No model is enabled yet, so the router cannot answer any prompt.")
                .AppendLine(CultureInfo.InvariantCulture, $"  Set one up on the dashboard: {dashboard}#/overview");
        }
        else if (dispatch)
        {
            banner
                .AppendLine()
                .AppendLine("  Try it")
                .AppendLine(CultureInfo.InvariantCulture, $"    {TryRequest(local, windows)}")
                .AppendLine(CultureInfo.InvariantCulture, $"    Watch it: {dashboard}#/live")
                .AppendLine("  Claude Code")
                .AppendLine(CultureInfo.InvariantCulture, $"    {ClaudeCodeCommand(local, configuration.Models, windows)}")
                .AppendLine(CultureInfo.InvariantCulture, $"  OpenAI clients: base URL {local}/v1, model \"auto\"");
        }

        if (urls.Any(u => !IsLoopback(u)))
        {
            banner
                .AppendLine()
                .AppendLine("  Listening beyond this machine:")
                .AppendLine(CultureInfo.InvariantCulture, $"  - Subscription CLI connectors (claude, codex, copilot) still answer only requests from this machine ({SiteLinks.Terms}).")
                .AppendLine(CultureInfo.InvariantCulture, $"  - The dashboard needs SemanticRouter:Dashboard:AdminToken and RequireLocalClient=false for remote use ({SiteLinks.DashboardAccess}).")
                .AppendLine(CultureInfo.InvariantCulture, $"  - {RemoteGatewayAccess(configuration.Middleware)}");
        }

        return banner.ToString();
    }

    /// <summary>A first request to the router, for the banner: PowerShell's own command on Windows, curl elsewhere.</summary>
    internal static string TryRequest(string local, bool windows)
    {
        const string Body = """{"model":"auto","messages":[{"role":"user","content":"Say hello in five words"}]}""";
        return windows
            ? $"Invoke-RestMethod {local}/v1/chat/completions -Method Post -ContentType application/json -Body '{Body}'"
            : $"curl {local}/v1/chat/completions -H 'Content-Type: application/json' -d '{Body}'";
    }

    /// <summary>
    /// How to start Claude Code against the router, for the banner and <c>init</c>, as docs/clients.md has it: the
    /// router's address and a placeholder token, <c>CLAUDE_CODE_MAX_CONTEXT_TOKENS</c> set to
    /// <see cref="ClaudeCodeContextTokens"/> when it is known, auto mode without Anthropic's server, and the router's own
    /// model name. Inline, so nothing changes in the shell; PowerShell has no inline form, so there it sets them for the
    /// session.
    /// </summary>
    internal static string ClaudeCodeCommand(string local, IEnumerable<ModelDefinition> models, bool windows)
    {
        var variables = new List<(string Name, string Value)> { ("ANTHROPIC_BASE_URL", local), ("ANTHROPIC_AUTH_TOKEN", "semantic-router") };
        if (ClaudeCodeContextTokens(models) is { } tokens)
        {
            variables.Add(("CLAUDE_CODE_MAX_CONTEXT_TOKENS", tokens.ToString(CultureInfo.InvariantCulture)));
        }

        variables.Add(("CLAUDE_CODE_AUTO_MODE_SERVER", "0"));
        string start = $"claude --model {ModelAliases.Router}";
        return windows
            ? string.Concat(variables.Select(v => $"$env:{v.Name}=\"{v.Value}\"; ")) + start
            : string.Concat(variables.Select(v => $"{v.Name}={v.Value} ")) + start;
    }

    /// <summary>
    /// The smallest context window among the enabled models that can carry tools (every Claude Code turn has them), so
    /// Claude Code compacts the conversation before it outgrows one of them. <see langword="null"/> when there is no
    /// such model, or when one of them does not declare its context window.
    /// </summary>
    internal static int? ClaudeCodeContextTokens(IEnumerable<ModelDefinition> models)
    {
        ModelDefinition[] agentic = models.Where(m => m.Enabled && m.HasCapabilities(["tools"])).ToArray();
        return agentic.Length == 0 || agentic.Any(m => m.ContextWindow <= 0) ? null : agentic.Min(m => m.ContextWindow);
    }

    /// <summary>What clients on other machines need to use the gateway, for the banner and <c>doctor</c>.</summary>
    internal static string RemoteGatewayAccess(PromptRouterMiddlewareOptions middleware) =>
        middleware.GatewayKeys.Count > 0
            ? "Clients on other machines must send a gateway key (SemanticRouter:Middleware:GatewayKeys)."
            : middleware.AllowUnauthenticatedRemoteClients
                ? "Clients on other machines can use the gateway without a key (AllowUnauthenticatedRemoteClients): only behind a proxy that authenticates them."
                : $"Clients on other machines are refused until you set SemanticRouter:Middleware:GatewayKeys ({SiteLinks.ListeningBeyond}).";

    /// <summary>
    /// The address the router will answer on for clients on this machine, from <c>--urls</c> on the command line, for
    /// commands that print it before the router runs (<c>init</c>, <c>doctor</c>).
    /// </summary>
    internal static string LocalAddress(HostSettings settings)
    {
        string? urls = null;
        for (int i = 0; i < settings.HostArguments.Count; i++)
        {
            string argument = settings.HostArguments[i];
            if (argument == "--urls" && i + 1 < settings.HostArguments.Count)
            {
                urls = settings.HostArguments[i + 1];
            }
            else if (argument.StartsWith("--urls=", StringComparison.Ordinal))
            {
                urls = argument["--urls=".Length..];
            }
        }

        string[] each = (urls ?? HostSettings.DefaultUrl).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return LocalAddress(each.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? each.FirstOrDefault() ?? HostSettings.DefaultUrl);
    }

    /// <summary>The address to print for clients on this machine: a wildcard bind is reached through localhost.</summary>
    internal static string LocalAddress(string url)
    {
        if (!Uri.TryCreate(url.Replace("://*", "://localhost", StringComparison.Ordinal).Replace("://+", "://localhost", StringComparison.Ordinal), UriKind.Absolute, out Uri? uri))
        {
            return url.TrimEnd('/');
        }

        string host = uri.Host is "0.0.0.0" or "[::]" or "::" ? "localhost" : uri.Host;
        return $"{uri.Scheme}://{(host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[') ? "[" + host + "]" : host)}:{uri.Port}";
    }

    internal static bool IsLoopback(string url)
    {
        if (!Uri.TryCreate(url.Replace("://*", "://wildcard", StringComparison.Ordinal).Replace("://+", "://wildcard", StringComparison.Ordinal), UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        return uri.IsLoopback || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out IPAddress? address) && IPAddress.IsLoopback(address));
    }
}

/// <summary>A started router: the web application and its instance record, removed when it is disposed.</summary>
internal sealed class RunningRouter(WebApplication app, RouterInstance instance, string configurationDirectory, IDisposable? stopListener) : IAsyncDisposable
{
    public WebApplication App => app;

    public RouterInstance Instance => instance;

    /// <summary>The address to give clients and the dashboard on this machine.</summary>
    public string LocalUrl => RouterHost.LocalAddress(instance.Urls.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? instance.Urls[0]);

    public async ValueTask DisposeAsync()
    {
        stopListener?.Dispose();
        try
        {
            await app.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            RouterInstance.Remove(configurationDirectory, instance.ProcessId);
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
