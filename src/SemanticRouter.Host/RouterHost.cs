using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration.Json;
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
        Directory.CreateDirectory(settings.ConfigurationDirectory);
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [.. settings.HostArguments],
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
        // directory the tool runs from; SEMANTIC_ROUTER_KEYS_DIR keeps them on a volume in containers.
        IDataProtectionBuilder dataProtection = builder.Services.AddDataProtection().SetApplicationName("semantic-router");
        if (Environment.GetEnvironmentVariable(HostSettings.KeysVariable) is { Length: > 0 } keys)
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keys));
        }

        builder.Services
            .AddSemanticPromptRouting(builder.Configuration.GetSection(SemanticRouterOptions.SectionName))
            .UseConfigurationFile(settings.ConfigurationFile)
            .UseSecretFile()
            .AddOnnxEmbeddings()
            .AddCopilotConnector()
            .UseSqliteState(Path.Combine(settings.ConfigurationDirectory, HostSettings.StateFileName));

        configure?.Invoke(builder);

        WebApplication app = builder.Build();
        app.UsePromptRouter();
        app.MapRouterModels();
        app.MapAnthropicTokenCount();
        app.MapPromptClassification();
        app.MapRouterDashboard("/router");
        return app;
    }

    public static async Task<int> RunAsync(HostSettings settings, TextWriter output)
    {
        if (!File.Exists(settings.ConfigurationFile))
        {
            RouterConfiguration template = RouterConfiguration.FromJson(Template());
            new JsonFileRouterConfigurationStore(settings.ConfigurationFile).Save(template);
            await output.WriteLineAsync($"Created {settings.ConfigurationFile} from the built-in template. Run `semantic-router init` to add the models on this machine.").ConfigureAwait(false);
        }

        WebApplication app = Build(settings);
        await app.StartAsync().ConfigureAwait(false);
        await WriteBannerAsync(app, settings, output).ConfigureAwait(false);
        await app.WaitForShutdownAsync().ConfigureAwait(false);
        return 0;
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
            .AppendLine(CultureInfo.InvariantCulture, $"  OpenAI clients {Set("OPENAI_BASE_URL", local + "/v1")}; model \"auto\"");

        if (urls.Any(u => !IsLoopback(u)))
        {
            banner
                .AppendLine()
                .AppendLine("  Listening beyond this machine:")
                .AppendLine("  - Subscription CLI connectors (claude, codex, copilot) still answer only requests from this machine (docs/TERMS.md).")
                .AppendLine("  - The dashboard needs SemanticRouter:Dashboard:AdminToken and RequireLocalClient=false for remote use.")
                .AppendLine("  - Set SemanticRouter:Middleware:GatewayKeys so remote clients identify themselves (docs/dashboard.md).");
        }

        await output.WriteLineAsync(banner.ToString()).ConfigureAwait(false);
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
