namespace SemanticRouter.AspNetCore;

/// <summary>
/// Where the dashboard is mapped, once <c>MapRouterDashboard</c> has run, so gateway errors can link to the page that
/// fixes them. <see langword="null"/> when the application maps no dashboard.
/// </summary>
internal sealed class RouterDashboardLocation
{
    private string? _prefix;
    private DashboardGate? _gate;

    /// <summary>The dashboard's path, e.g. <c>/router</c>. The first dashboard mapped wins.</summary>
    public string? Prefix
    {
        get => Volatile.Read(ref _prefix);
        set => Interlocked.CompareExchange(ref _prefix, value, null);
    }

    /// <summary>
    /// The first dashboard's access check and audit log, so the router's MCP server admits the same callers with the same
    /// roles, counts wrong tokens against the same addresses and records into the same audit log. The first wins.
    /// </summary>
    public DashboardGate? Gate
    {
        get => Volatile.Read(ref _gate);
        set => Interlocked.CompareExchange(ref _gate, value, null);
    }
}

/// <summary>A dashboard's access check, its audit log and the options they were made from.</summary>
/// <param name="Access">Who may call, with which role.</param>
/// <param name="Audit">Where calls are recorded.</param>
/// <param name="Options">The dashboard's options, as bound and configured.</param>
internal sealed record DashboardGate(DashboardAccess Access, DashboardAudit Audit, RouterDashboardOptions Options);
