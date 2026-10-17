using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// The process the router runs in, for the dashboard's Admin → Service page: where it listens (this machine only, or
/// other machines too), restarting and stopping it, and running it as a background service. The <c>semantic-router</c>
/// tool registers one; an application that hosts the router itself runs it its own way, and the page says so.
/// </summary>
public interface IRouterServiceControl
{
    /// <summary>How the router runs now, what it will listen on at its next start, and what the dashboard may do.</summary>
    Task<RouterServiceDescription> DescribeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves where the router listens from its next start (a restart applies it). Throws
    /// <see cref="ArgumentException"/> for a value it cannot take and <see cref="InvalidOperationException"/> when
    /// something else decides where it listens (an environment variable, Kestrel endpoints, a container).
    /// </summary>
    Task<RouterServiceDescription> SetListenAsync(RouterListenChange change, CancellationToken cancellationToken = default);

    /// <summary>Why <paramref name="action"/> (one of <see cref="RouterServiceActions.All"/>) cannot be done now, or <see langword="null"/> when it can.</summary>
    string? Refusal(string action);

    /// <summary>
    /// Starts <paramref name="action"/>. Called once the response that asked for it has been sent: a restart or a stop
    /// ends the requests in flight, the dashboard's own included.
    /// </summary>
    void Run(string action);
}

/// <summary>What the dashboard can ask of <see cref="IRouterServiceControl"/>.</summary>
public static class RouterServiceActions
{
    /// <summary>Stop the router and start it again in the same process, with the saved settings.</summary>
    public const string Restart = "restart";

    /// <summary>Stop the router. Nothing on the dashboard starts it again.</summary>
    public const string Stop = "stop";

    /// <summary>Install the background service and hand over to it.</summary>
    public const string Install = "install";

    /// <summary>Write the background service's definition again (the saved settings, this program) and restart it.</summary>
    public const string Reinstall = "reinstall";

    /// <summary>Remove the background service; the router it runs stops with it.</summary>
    public const string Uninstall = "uninstall";

    /// <summary>Every action, in the order the page offers them.</summary>
    public static readonly IReadOnlyList<string> All = [Restart, Stop, Install, Reinstall, Uninstall];
}

/// <summary>How the router runs, for the dashboard's Service page.</summary>
/// <param name="Listen">Where it listens, and where it will listen after a restart.</param>
/// <param name="Background">The background service, or <see langword="null"/> when this machine has no service manager to run it.</param>
/// <param name="RunsAs">
/// <c>service</c> (the background service runs this router), <c>container</c>, or <c>foreground</c> (started from a
/// terminal or by another program).
/// </param>
/// <param name="Actions">What the dashboard may do now (<see cref="RouterServiceActions"/>).</param>
/// <param name="LastOutcome">How the last action that could not finish ended, when this router is still running because of it.</param>
public sealed record RouterServiceDescription(
    RouterListenDescription Listen, RouterBackgroundService? Background, string RunsAs, IReadOnlyList<string> Actions, RouterServiceOutcome? LastOutcome);

/// <summary>Where the router listens.</summary>
/// <param name="Scope"><c>local</c> (this machine only), <c>network</c> (every network interface) or <c>custom</c> (anything else) at the next start.</param>
/// <param name="Port">The port at the next start.</param>
/// <param name="Urls">The addresses it will listen on at the next start, as configured (<c>http://0.0.0.0:5080</c>).</param>
/// <param name="Saved">The addresses saved from the dashboard, or <see langword="null"/> when none are.</param>
/// <param name="File">Where the dashboard saves them.</param>
/// <param name="PinnedBy">What decides the addresses instead of the saved setting, e.g. "the --urls option on the command line".</param>
/// <param name="Editable">Whether the dashboard may change them (not when an environment variable, Kestrel endpoints or a container decide).</param>
/// <param name="RestartNeeded">Whether the router listens elsewhere than it will after a restart.</param>
public sealed record RouterListenDescription(
    string Scope, int Port, string Urls, string? Saved, string File, string? PinnedBy, bool Editable, bool RestartNeeded);

/// <summary>The background service this machine's service manager runs.</summary>
/// <param name="Kind">E.g. <c>systemd user service</c>, <c>launchd agent</c>, <c>Windows sign-in entry</c>.</param>
/// <param name="Installed">Whether it is installed.</param>
/// <param name="Status">What the service manager says about it, when installed.</param>
/// <param name="Location">Where its definition lives.</param>
/// <param name="LogHint">How to read what it logs.</param>
/// <param name="Notes">What to know before installing it.</param>
public sealed record RouterBackgroundService(string Kind, bool Installed, string? Status, string Location, string LogHint, IReadOnlyList<string> Notes);

/// <summary>How an action ended.</summary>
/// <param name="Action">One of <see cref="RouterServiceActions.All"/>.</param>
/// <param name="Succeeded">Whether it did what was asked.</param>
/// <param name="Message">What happened, in a sentence or two.</param>
/// <param name="At">When.</param>
public sealed record RouterServiceOutcome(string Action, bool Succeeded, string Message, DateTimeOffset At);

/// <summary>Where the router should listen from its next start.</summary>
/// <param name="Scope"><c>local</c> (this machine only) or <c>network</c> (every network interface).</param>
/// <param name="Port">1 to 65535.</param>
public sealed record RouterListenChange(string Scope, int Port);

/// <summary>
/// <c>GET api/service</c>, <c>PUT api/service/listen</c> and <c>POST api/service/{action}</c>: the dashboard's Service
/// page, through the host's <see cref="IRouterServiceControl"/>. Admin only, since the answer names paths on the
/// machine and the actions stop the router.
/// </summary>
internal static class ServiceControlApi
{
    public static void Map(RouteGroupBuilder api, JsonSerializerOptions json, RouterDashboardOptions options)
    {
        api.MapGet("/service", async (IServiceProvider services, CancellationToken cancellationToken) =>
            {
                RouterDashboardAccessSummary dashboard = new(options.RequireLocalClient, !string.IsNullOrEmpty(options.AdminToken) || !string.IsNullOrEmpty(options.ReadOnlyToken) || !string.IsNullOrEmpty(options.MetricsToken));
                return services.GetService<IRouterServiceControl>() is { } control
                    ? Results.Json(new ServiceResponse(true, await control.DescribeAsync(cancellationToken).ConfigureAwait(false), dashboard), json)
                    : Results.Json(new ServiceResponse(false, null, dashboard), json);
            })
            .WithMetadata(DashboardAdminReadMetadata.Instance);

        api.MapPut("/service/listen", async (HttpContext context, IServiceProvider services, RouterListenChange change, CancellationToken cancellationToken) =>
            {
                if (services.GetService<IRouterServiceControl>() is not { } control)
                {
                    return NotAvailable();
                }

                try
                {
                    RouterServiceDescription described = await control.SetListenAsync(change, cancellationToken).ConfigureAwait(false);
                    DashboardAudit.Detail(context, $"{(change.Scope == "network" ? "other machines too" : "this machine only")}, port {change.Port}");
                    return Results.Json(described, json);
                }
                catch (ArgumentException ex)
                {
                    return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
                }
                catch (InvalidOperationException ex)
                {
                    return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
                }
            })
            .WithMetadata(DashboardWriteMetadata.Instance)
            .Audited("changed where the router listens");

        api.MapPost("/service/{action}", (HttpContext context, IServiceProvider services, string action) =>
            {
                if (services.GetService<IRouterServiceControl>() is not { } control)
                {
                    return NotAvailable();
                }

                if (!RouterServiceActions.All.Contains(action))
                {
                    return Results.Problem($"Unknown action '{action}'. Use {string.Join(", ", RouterServiceActions.All)}.", statusCode: StatusCodes.Status404NotFound);
                }

                DashboardAudit.Summary(context, Summaries[action]);
                if (control.Refusal(action) is { } refusal)
                {
                    return Results.Problem(refusal, statusCode: StatusCodes.Status409Conflict);
                }

                // After the answer has gone out: a restart or a stop ends the requests in flight, this one included.
                context.Response.OnCompleted(() =>
                {
                    control.Run(action);
                    return Task.CompletedTask;
                });
                return Results.Json(new ServiceActionResponse(action), json, statusCode: StatusCodes.Status202Accepted);
            })
            .WithMetadata(DashboardWriteMetadata.Instance)
            .Audited("controlled the router");
    }

    private static readonly Dictionary<string, string> Summaries = new(StringComparer.Ordinal)
    {
        [RouterServiceActions.Restart] = "restarted the router",
        [RouterServiceActions.Stop] = "stopped the router",
        [RouterServiceActions.Install] = "installed the background service",
        [RouterServiceActions.Reinstall] = "reinstalled the background service",
        [RouterServiceActions.Uninstall] = "removed the background service",
    };

    private static IResult NotAvailable() => Results.Problem(
        "The application this router runs in decides how it runs and where it listens; the dashboard cannot change it.",
        statusCode: StatusCodes.Status404NotFound);
}

/// <summary>What the Service page shows.</summary>
/// <param name="Available">Whether the router's host lets the dashboard control it (the <c>semantic-router</c> tool does).</param>
/// <param name="Service">How it runs, when <paramref name="Available"/>.</param>
/// <param name="Dashboard">Whether the dashboard can be used from other machines.</param>
internal sealed record ServiceResponse(bool Available, RouterServiceDescription? Service, RouterDashboardAccessSummary Dashboard);

/// <summary>What using the dashboard from another machine needs.</summary>
/// <param name="RequireLocalClient">Whether only this machine may use it (<c>SemanticRouter:Dashboard:RequireLocalClient</c>).</param>
/// <param name="TokensConfigured">Whether a token to sign in with is configured.</param>
internal sealed record RouterDashboardAccessSummary(bool RequireLocalClient, bool TokensConfigured);

/// <summary>An action has been accepted and starts once this answer is sent.</summary>
internal sealed record ServiceActionResponse(string Action);
