namespace SemanticRouter.AspNetCore;

/// <summary>
/// Where the dashboard is mapped, once <c>MapRouterDashboard</c> has run, so gateway errors can link to the page that
/// fixes them. <see langword="null"/> when the application maps no dashboard.
/// </summary>
internal sealed class RouterDashboardLocation
{
    private string? _prefix;

    /// <summary>The dashboard's path, e.g. <c>/router</c>. The first dashboard mapped wins.</summary>
    public string? Prefix
    {
        get => Volatile.Read(ref _prefix);
        set => Interlocked.CompareExchange(ref _prefix, value, null);
    }
}
