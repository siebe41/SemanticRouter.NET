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
        await using (WebApplication app = RouterHost.Build(settings, builder =>
        {
            // Keep the report readable: the checks report problems themselves.
            builder.Logging.SetMinimumLevel(LogLevel.Error);
            configure?.Invoke(builder);
        }))
        {
            try
            {
                // The egress list first: the embeddings check may download the ONNX model it reports.
                IReadOnlyList<RouterCheck> egress = EgressList.For(app.Services, settings.ConfigurationDirectory);
                checks = [.. HostChecks(app), .. egress, .. await RouterDoctor.CheckAsync(app.Services).ConfigureAwait(false)];
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
            : $"{failed} check(s) failed. Each [FAIL] line names the setting to change.").ConfigureAwait(false);
        return failed == 0 ? 0 : 1;
    }

    private static IEnumerable<RouterCheck> HostChecks(WebApplication app)
    {
        MachinePolicy policy = app.Services.GetRequiredService<RouterRuntimeProvider>().Policy;
        yield return new RouterCheck("policy", RouterCheckStatus.Ok, policy.IsEmpty
            ? "no machine-wide policy is set."
            : $"set by your organization in {policy.Source}: {System.Text.Json.Nodes.JsonNode.Parse(policy.ToJson())!.ToJsonString()}");
        string urls = app.Configuration[WebHostDefaults.ServerUrlsKey] ?? HostSettings.DefaultUrl;
        RouterDashboardOptions dashboard = app.Services.GetRequiredService<IOptions<RouterDashboardOptions>>().Value;
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
            && !dashboard.AllowUnauthenticatedRemoteClients)
        {
            yield return new RouterCheck("dashboard", RouterCheckStatus.Warning,
                "RequireLocalClient is off but no token is set, so remote dashboard requests are refused. Set SemanticRouter:Dashboard:AdminToken.");
        }
    }
}
