using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SemanticRouter.AspNetCore;
using SemanticRouter.Mcp;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Maps the router's MCP server.</summary>
public static class RouterMcpEndpoints
{
    /// <summary>
    /// Maps the router's MCP server (Streamable HTTP) at <paramref name="pattern"/>, when
    /// <see cref="RouterMcpOptions.Enabled"/> is set; otherwise maps nothing, so the path answers <c>404</c>. Callers are
    /// admitted as the dashboard's admin API admits them (loopback clients, a dashboard token as
    /// <c>Authorization: Bearer</c>, or a user an authenticating proxy names), and each tool needs the role its admin API
    /// endpoint needs. Map it after <c>MapRouterDashboard</c>, whose access check and audit log it shares. See
    /// <c>docs/clients.md#the-routers-mcp-server</c>.
    /// </summary>
    public static IEndpointConventionBuilder MapRouterMcp(
        this IEndpointRouteBuilder endpoints, [System.Diagnostics.CodeAnalysis.StringSyntax("Route")] string pattern = "/router/mcp")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        IServiceProvider services = endpoints.ServiceProvider;
        if (services.GetService<RouterMcpServerMarker>() is not { } marker)
        {
            throw new InvalidOperationException("The router's MCP server is not registered: call AddRouterMcpServer() on AddSemanticPromptRouting(…) first.");
        }

        pattern = "/" + pattern.Trim('/');
        RouterMcpOptions options = services.GetRequiredService<IOptions<RouterMcpOptions>>().Value;
        if (!options.Enabled)
        {
            return endpoints.MapGroup(pattern);
        }

        DashboardGate gate = services.GetService<RouterDashboardLocation>()?.Gate ?? OwnGate(services, pattern);
        marker.Gate = gate;
        IEndpointConventionBuilder mcp = endpoints.MapMcp(pattern);

        // The metrics role may connect (usage and health are figures); which tools each caller sees is decided per request
        // (RouterMcpTools.ForCallerAsync).
        mcp.WithMetadata(DashboardMetricsMetadata.Instance);
        mcp.Add(endpoint =>
        {
            RequestDelegate inner = endpoint.RequestDelegate ?? throw new InvalidOperationException("An MCP endpoint has no request delegate.");
            endpoint.RequestDelegate = context => AdmitAsync(context, inner, gate);
        });

        McpLog.Serving(services.GetRequiredService<ILoggerFactory>().CreateLogger("SemanticRouter.Mcp"), pattern, options.AllowProposals);
        return mcp;
    }

    /// <summary>
    /// Runs the dashboard's access check on the request and, when it admits the caller, the MCP endpoint; otherwise
    /// answers as the admin API does (<c>401</c>, <c>403</c>, <c>429</c>) and records the refusal as it would.
    /// </summary>
    private static async Task AdmitAsync(HttpContext context, RequestDelegate inner, DashboardGate gate)
    {
        bool admitted = false;
        object? refusal = await gate.Access.FilterAsync(new DefaultEndpointFilterInvocationContext(context), _ =>
        {
            admitted = true;
            return ValueTask.FromResult<object?>(null);
        }).ConfigureAwait(false);

        // Answers can hold prompt previews and the decision on them: no cache may keep them.
        context.Response.Headers.CacheControl = "no-store";
        if (!admitted)
        {
            gate.Audit.Refused(context, refusal is IStatusCodeHttpResult { StatusCode: int status } ? status : StatusCodes.Status403Forbidden);
            if (refusal is IResult result)
            {
                await result.ExecuteAsync(context).ConfigureAwait(false);
            }

            return;
        }

        await inner(context).ConfigureAwait(false);
    }

    /// <summary>An access check and audit log of its own, for an application that maps no dashboard (or maps it after).</summary>
    private static DashboardGate OwnGate(IServiceProvider services, string pattern)
    {
        RouterDashboardOptions options = RouterDashboardEndpoints.CopyOf(services.GetService<IOptions<RouterDashboardOptions>>()?.Value);
        DashboardAccess access = RouterDashboardEndpoints.CreateAccess(services, options, pattern);
        var audit = new DashboardAudit(
            services.GetService<IAuditLog>() ?? new InMemoryAuditLog(),
            services.GetService<TimeProvider>() ?? TimeProvider.System,
            services.GetRequiredService<ILoggerFactory>().CreateLogger(DashboardAudit.LogCategory));
        return new DashboardGate(access, audit, options);
    }
}
