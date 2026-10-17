using Microsoft.Extensions.Options;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Host;

/// <summary>
/// <c>semantic-router doctor</c>: loads the configuration exactly as <c>semantic-router</c> would, then checks every
/// connector, model and the embeddings against this machine (<see cref="RouterDoctor"/>) without calling a model, and
/// lists every host the router will contact (<see cref="EgressList"/>). Exit code 1 when anything failed.
/// </summary>
internal static class DoctorCommand
{
    public static async Task<int> RunAsync(HostSettings settings, TextWriter output, Action<WebApplicationBuilder>? configure = null)
    {
        await output.WriteLineAsync($"Configuration: {settings.ConfigurationFile}{(File.Exists(settings.ConfigurationFile) ? string.Empty : " (not created yet; the built-in template applies)")}").ConfigureAwait(false);

        IReadOnlyList<RouterCheck> checks;
        WebApplication built;
        try
        {
            built = RouterHost.Build(settings, builder =>
            {
                // Keep the report readable: the checks report problems themselves.
                builder.Logging.SetMinimumLevel(LogLevel.Error);
                configure?.Invoke(builder);
            });
        }
        catch (InvalidOperationException ex)
        {
            // A host setting the router cannot start with, e.g. a data-protection certificate it cannot load.
            await output.WriteLineAsync($"[FAIL] configuration: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        await using (WebApplication app = built)
        {
            try
            {
                // The egress list first: the embeddings check may download the ONNX model it reports.
                IReadOnlyList<RouterCheck> egress = EgressList.For(app.Services, settings.ConfigurationDirectory);
                IReadOnlyList<RouterCheck> router = await RouterDoctor.CheckAsync(app.Services).ConfigureAwait(false);
                RouterCheck? models = ModelsCheck(app.Services.GetRequiredService<RouterRuntimeProvider>().Current.Configuration, settings);
                IStateStorageHealth? state = app.Services.GetService<IStateStorageHealth>();
                checks =
                [
                    .. HostChecks(app, settings), .. state is null ? [] : new[] { StateCheck(state) }, .. egress, .. models is null ? [] : new[] { models },
                    .. router.Where(c => (models is null || !IsNoModelWarning(c)) && !(c.Subject == "configuration" && c.Message == state?.Problem)),
                ];
            }
            catch (InvalidOperationException ex)
            {
                // The configuration does not validate: nothing else can be checked.
                await output.WriteLineAsync($"[FAIL] configuration: {ex.Message}").ConfigureAwait(false);
                return 1;
            }
        }

        foreach (RouterCheck check in checks)
        {
            string tag = check.Status switch
            {
                RouterCheckStatus.Ok => "[ OK ]",
                RouterCheckStatus.Warning => "[WARN]",
                _ => "[FAIL]",
            };
            await output.WriteLineAsync($"{tag} {check.Subject}: {check.Message}").ConfigureAwait(false);
        }

        int failed = checks.Count(c => c.Status == RouterCheckStatus.Failed);
        int warnings = checks.Count(c => c.Status == RouterCheckStatus.Warning);
        await output.WriteLineAsync(failed == 0
            ? $"All checks passed{(warnings > 0 ? $" ({warnings} warning(s))" : string.Empty)}."
            : $"{failed} check(s) failed. Each [FAIL] line says what to change.").ConfigureAwait(false);
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// A router that dispatches but has no enabled model refuses every request: that fails the check, with where to add
    /// one. Running <c>init</c> is suggested only while the configuration is still the template, since an <c>init</c>
    /// that already ran found nothing to add.
    /// </summary>
    internal static RouterCheck? ModelsCheck(RouterConfiguration configuration, HostSettings settings)
    {
        if (configuration.Middleware.Mode != PromptRouterMode.Dispatch || configuration.Models.Any(m => m.Enabled))
        {
            return null;
        }

        string setup = $"{RouterHost.LocalAddress(settings)}/router#/overview";
        bool template = !File.Exists(settings.ConfigurationFile) || FirstRun.IsUntouchedTemplate(settings.ConfigurationFile, exactly: true);
        string fix = template
            ? $"Run `semantic-router init` to add the models on this machine, or start the router (`semantic-router`) and set one up on its dashboard: {setup}"
            : configuration.Models.Count > 0
                ? $"Start the router (`semantic-router`) and enable one on its dashboard under Providers → Models, or use the setup on {setup}"
                : $"Start the router (`semantic-router`) and set one up on its dashboard: {setup}. For a local model, start LM Studio's server (Developer → Start server) or Ollama first.";
        return new RouterCheck("models", RouterCheckStatus.Failed,
            $"no model is enabled{(configuration.Models.Count > 0 ? $" ({configuration.Models.Count} configured, all disabled)" : string.Empty)}, so the router cannot answer any prompt. {fix}");
    }

    /// <summary>
    /// The state database: a router that cannot use it keeps budgets, usage and the audit log in memory only, so it fails
    /// the check (the runtime's warning about it is left out, as this says the same).
    /// </summary>
    internal static RouterCheck StateCheck(IStateStorageHealth state) =>
        state.Problem is { } problem ? new RouterCheck("state", RouterCheckStatus.Failed, problem)
        : LocalData.OnNetworkPath(state.Location) ? new RouterCheck("state", RouterCheckStatus.Warning,
            $"{state.Location} is on a network drive, where SQLite's write-ahead log is not safe when two processes (the tray and the background service) use it. " +
            $"Set {HostSettings.HomeVariable} to a folder on this machine.")
        : new RouterCheck("state", RouterCheckStatus.Ok, $"{state.Location}: budgets, usage, history and the audit log are kept.");

    /// <summary>The runtime's own warning about the same thing, which <see cref="ModelsCheck"/> replaces.</summary>
    private static bool IsNoModelWarning(RouterCheck check) =>
        check.Subject == "configuration" && check.Message.StartsWith("No model is enabled", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// On Windows, a warning when <paramref name="directory"/> lets <c>Users</c>, <c>Everyone</c> or <c>Authenticated
    /// Users</c> read what is in it (<paramref name="holds"/>): a custom folder the router did not create, or one made
    /// before it set an ACL of its own (<see cref="OwnerOnly.Directory(string)"/>). <see langword="null"/> otherwise,
    /// and on macOS and Linux, where the router keeps the folder at 0700 itself.
    /// </summary>
    internal static RouterCheck? FolderAccessCheck(string subject, string directory, string holds)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(directory) || OwnerOnly.BroadReaders(directory) is not { Count: > 0 } readers)
        {
            return null;
        }

        return new RouterCheck(subject, RouterCheckStatus.Warning,
            $"{directory} can be read by {string.Join(", ", readers)}, so other accounts on this machine can read {holds}. " +
            $"Limit it to your account, SYSTEM and Administrators (in cmd: icacls \"{directory}\" /inheritance:r /grant:r \"%USERNAME%:(OI)(CI)F\" *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F), " +
            "or use a folder in your profile.");
    }

    private static IEnumerable<RouterCheck> HostChecks(WebApplication app, HostSettings settings)
    {
        MachinePolicy policy = app.Services.GetRequiredService<RouterRuntimeProvider>().Policy;
        yield return new RouterCheck("policy", RouterCheckStatus.Ok, policy.IsEmpty
            ? "no machine-wide policy is set."
            : $"set by your organization in {policy.Source}: {System.Text.Json.Nodes.JsonNode.Parse(policy.ToJson())!.ToJsonString()}");
        KeyRing keyRing = app.Services.GetRequiredService<KeyRing>();
        foreach (RouterCheck check in keyRing.Checks())
        {
            yield return check;
        }

        if (FolderAccessCheck("configuration folder", settings.ConfigurationDirectory, "the configuration, the activity log with its prompt previews, and the logs") is { } home)
        {
            yield return home;
        }

        if (keyRing.CustomDirectory && FolderAccessCheck("key ring folder", keyRing.Directory!, "the data-protection keys (encrypted for your account, but they can be copied)") is { } keys)
        {
            yield return keys;
        }

        string urls = app.Configuration[WebHostDefaults.ServerUrlsKey] ?? HostSettings.DefaultUrl;
        RouterDashboardOptions dashboard = app.Services.GetRequiredService<IOptions<RouterDashboardOptions>>().Value;
        if (!string.IsNullOrWhiteSpace(dashboard.TrustedIdentityHeader))
        {
            IConfigurationSection forwarded = app.Configuration.GetSection(RouterHost.ForwardedHeadersSection);
            bool proxies = forwarded.GetSection("KnownProxies").GetChildren().Any() || forwarded.GetSection("KnownNetworks").GetChildren().Any();
            yield return proxies
                ? new RouterCheck("identity", RouterCheckStatus.Ok,
                    $"the dashboard takes the user from {dashboard.TrustedIdentityHeader} on requests from the known proxies; TrustedIdentityRoles names {dashboard.TrustedIdentityRoles.Count}.")
                : new RouterCheck("identity", RouterCheckStatus.Warning,
                    $"Dashboard:TrustedIdentityHeader is {dashboard.TrustedIdentityHeader}, but {RouterHost.ForwardedHeadersSection} names no proxy, so the header is never believed.");
        }

        bool remote = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(u => !RouterHost.IsLoopback(u));
        if (!remote)
        {
            yield return new RouterCheck("listen", RouterCheckStatus.Ok, $"{urls} (this machine only).");
            yield break;
        }

        yield return new RouterCheck("listen", RouterCheckStatus.Warning,
            $"{urls} accepts other machines. Subscription CLI connectors still answer only local requests.");
        PromptRouterMiddlewareOptions middleware = app.Services.GetRequiredService<RouterRuntimeProvider>().Current.Configuration.Middleware;
        yield return new RouterCheck("gateway", middleware.GatewayKeys.Count > 0 ? RouterCheckStatus.Ok : RouterCheckStatus.Warning, RouterHost.RemoteGatewayAccess(middleware));
        if (!dashboard.RequireLocalClient && string.IsNullOrEmpty(dashboard.AdminToken) && string.IsNullOrEmpty(dashboard.ReadOnlyToken)
            && string.IsNullOrEmpty(dashboard.MetricsToken)
            && !dashboard.AllowUnauthenticatedRemoteClients)
        {
            yield return new RouterCheck("dashboard", RouterCheckStatus.Warning,
                "RequireLocalClient is off but no token is set, so remote dashboard requests are refused. Set SemanticRouter:Dashboard:AdminToken.");
        }
    }
}
