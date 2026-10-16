using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Options;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Host;

/// <summary>Builds and runs the router web application.</summary>
internal static class RouterHost
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

        builder.Configuration.Sources.Insert(0, new JsonStreamConfigurationSource
        {
            Stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"SemanticRouter\":" + Template() + "}")),
        });

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
        return app;
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
    public static async Task<RunningRouter?> StartAsync(HostSettings settings, TextWriter output, Action<WebApplicationBuilder>? configure = null)
    {
        if (!File.Exists(settings.ConfigurationFile))
        {
            RouterConfiguration template = RouterConfiguration.FromJson(Template());
            new JsonFileRouterConfigurationStore(settings.ConfigurationFile).Save(template);
            await output.WriteLineAsync($"Created {settings.ConfigurationFile} from the built-in template. Run `semantic-router init` to add the models on this machine.").ConfigureAwait(false);
        }

        WebApplication app = Build(settings, configure);
        try
        {
            await app.StartAsync().ConfigureAwait(false);
        }
        catch (IOException ex) when (ex.InnerException is AddressInUseException)
        {
            await app.DisposeAsync().ConfigureAwait(false);
            await output.WriteLineAsync(
                $"{ex.Message} Another program is listening there, or the router already runs in the background " +
                "(`semantic-router service status`). Pick another address with --urls.").ConfigureAwait(false);
            return null;
        }

        var instance = new RouterInstance(Environment.ProcessId, [.. app.Urls], DateTimeOffset.UtcNow, Version);
        instance.Write(settings.ConfigurationDirectory);
        IDisposable? stopListener = OperatingSystem.IsWindows() ? RouterInstance.ListenForStop(app.Lifetime) : null;
        return new RunningRouter(app, instance, settings.ConfigurationDirectory, stopListener);
    }

    private static async Task WriteBannerAsync(WebApplication app, HostSettings settings, TextWriter output)
    {
        string[] urls = [.. app.Urls];
        string url = urls.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) ?? urls.FirstOrDefault() ?? HostSettings.DefaultUrl;
        string local = LocalAddress(url);
        bool windows = OperatingSystem.IsWindows();
        string Set(string name, string value) => windows ? $"$env:{name}=\"{value}\"" : $"export {name}={value}";

        var banner = new StringBuilder()
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"SemanticRouter {Version} is listening on {string.Join(", ", urls)}")
            .AppendLine(CultureInfo.InvariantCulture, $"  Configuration  {settings.ConfigurationFile}")
            .AppendLine(CultureInfo.InvariantCulture, $"  Dashboard      {local}/router")
            .AppendLine(CultureInfo.InvariantCulture, $"  Claude Code    {Set("ANTHROPIC_BASE_URL", local)}; {Set("ANTHROPIC_AUTH_TOKEN", "semantic-router")}")
            .AppendLine(CultureInfo.InvariantCulture, $"                 {ClaudeCodeStart(app.Services.GetRequiredService<RouterRuntimeProvider>().Current.Router.Models, windows)}")
            .AppendLine(CultureInfo.InvariantCulture, $"  OpenAI clients {Set("OPENAI_BASE_URL", local + "/v1")}; model \"auto\"");

        if (urls.Any(u => !IsLoopback(u)))
        {
            banner
                .AppendLine()
                .AppendLine("  Listening beyond this machine:")
                .AppendLine("  - Subscription CLI connectors (claude, codex, copilot) still answer only requests from this machine (docs/TERMS.md).")
                .AppendLine("  - The dashboard needs SemanticRouter:Dashboard:AdminToken and RequireLocalClient=false for remote use.")
                .AppendLine(CultureInfo.InvariantCulture, $"  - {RemoteGatewayAccess(app.Services.GetRequiredService<RouterRuntimeProvider>().Current.Configuration.Middleware)}");
        }

        await output.WriteLineAsync(banner.ToString()).ConfigureAwait(false);
    }

    /// <summary>
    /// How to start Claude Code against the router, for the banner and <c>init</c>: the router's own model name, and
    /// <c>CLAUDE_CODE_MAX_CONTEXT_TOKENS</c> set to <see cref="ClaudeCodeContextTokens"/> when it is known.
    /// </summary>
    internal static string ClaudeCodeStart(IEnumerable<ModelDefinition> models, bool windows)
    {
        string start = $"claude --model {ModelAliases.Router}";
        return ClaudeCodeContextTokens(models) is { } tokens
            ? (windows ? $"$env:CLAUDE_CODE_MAX_CONTEXT_TOKENS=\"{tokens}\"" : $"export CLAUDE_CODE_MAX_CONTEXT_TOKENS={tokens}") + "; " + start
            : start;
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
                : "Clients on other machines are refused until you set SemanticRouter:Middleware:GatewayKeys (docs/host.md).";

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
