namespace SemanticRouter.Host;

/// <summary>
/// Pages of the documentation site, for links in the tool's output: someone who installed the tool has no clone of the
/// repository, so <c>docs/TERMS.md</c> means nothing to them. A guide <c>docs/name.md</c> is the page <c>/name</c>.
/// </summary>
internal static class SiteLinks
{
    public const string Root = "https://siebe41.github.io/SemanticRouter.NET";

    /// <summary>Why the subscription CLI connectors are built the way they are.</summary>
    public const string Terms = Root + "/terms";

    /// <summary>What to check before using a subscription CLI on a work machine.</summary>
    public const string WorkUse = Terms + "#using-it-at-work";

    public const string Host = Root + "/host";

    public const string KeyRing = Host + "#the-key-ring";

    public const string ListeningBeyond = Host + "#listening-beyond-this-machine";

    public const string Dashboard = Root + "/dashboard";

    public const string DashboardAccess = Dashboard + "#access-and-security";

    public const string Clients = Root + "/clients";

    public const string Desktop = Root + "/desktop";
}
