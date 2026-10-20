using Microsoft.AspNetCore.Builder;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Options for <see cref="RouterDashboardEndpoints.MapRouterDashboard"/>. Bound from <c>SemanticRouter:Dashboard</c>
/// when routing is registered from configuration; the <c>configure</c> callback of <c>MapRouterDashboard</c> runs after.
/// See <c>docs/dashboard.md</c> for the supported setups.
/// </summary>
public sealed class RouterDashboardOptions
{
    /// <summary>Shortest token accepted, so a token cannot be guessed.</summary>
    public const int MinimumTokenLength = 16;

    /// <summary>
    /// Only answer dashboard and admin requests from the loopback interface. Default <see langword="true"/>. To reach
    /// the dashboard from other machines, turn this off and set <see cref="AdminToken"/> (or, behind a reverse proxy
    /// that authenticates callers itself, <see cref="AllowUnauthenticatedRemoteClients"/>).
    /// </summary>
    public bool RequireLocalClient { get; set; } = true;

    /// <summary>
    /// Token that remote clients present (as <c>Authorization: Bearer {token}</c> or by signing in on the dashboard) for
    /// full access. At least 16 characters; <c>${env:…}</c> and <c>${secret:…}</c> references are resolved.
    /// </summary>
    public string? AdminToken { get; set; }

    /// <summary>Token for view-only access: status, activity and the masked configuration, but nothing that spends embedding or API calls.</summary>
    public string? ReadOnlyToken { get; set; }

    /// <summary>
    /// Token for figures only (<see cref="RouterDashboardRole.Metrics"/>): status and health, usage and cost totals,
    /// activity counts and the digest's counts, never prompt or reply text, utterances, the configuration or memory.
    /// </summary>
    public string? MetricsToken { get; set; }

    /// <summary>
    /// Serve remote clients without a token. Only for a reverse proxy that authenticates every request itself; ignored
    /// when a token is configured. Default <see langword="false"/>.
    /// </summary>
    public bool AllowUnauthenticatedRemoteClients { get; set; }

    /// <summary>
    /// Require the token from loopback clients too. Turn this on when a reverse proxy on this machine forwards remote
    /// traffic, which otherwise arrives from loopback and would be trusted. Default <see langword="false"/>.
    /// </summary>
    public bool RequireTokenForLocalClients { get; set; }

    /// <summary>
    /// Let the admin API change what a CLI or Copilot connector runs (its program, arguments, environment variables and
    /// home directory, beyond a preset started from PATH) and which folders and files memory sources read. Off by
    /// default, because loopback clients are admin without a token, and with this any program on the machine (or any
    /// user, on a shared one) could run whatever it liked as you, or have the router read any file you can. Those
    /// settings are then changed in the configuration file. Default <see langword="false"/>.
    /// </summary>
    public bool AllowProcessSettings { get; set; }

    /// <summary>
    /// Addresses the dashboard is reached at besides <c>localhost</c>, e.g. <c>https://router.home.lan</c>. Changes
    /// are accepted from these origins, and loopback requests may name them in the Host header.
    /// </summary>
    public IList<string> PublicOrigins { get; } = new List<string>();

    /// <summary>
    /// How long a dashboard sign-in lasts: the session cookie then lapses and the token must be entered again. Changing
    /// a token ends every session signed in with it, and signing out ends that session on the server. Default 12 hours.
    /// </summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// Header an authenticating reverse proxy sets to the signed-in user, e.g. <c>X-Forwarded-User</c> or
    /// <c>X-Auth-Request-Email</c> (oauth2-proxy), <c>Cf-Access-Authenticated-User-Email</c> (Cloudflare Access). Believed
    /// only on requests that come straight from a proxy named in <c>SemanticRouter:ForwardedHeaders:KnownProxies</c> or
    /// <c>KnownNetworks</c> (<see cref="RouterForwardedHeadersExtensions.UseRouterForwardedHeaders"/>); from anyone else
    /// it is ignored. The user gets the role <see cref="TrustedIdentityRoles"/> gives them (none: <c>403</c>), tokens are
    /// not consulted, and the user is named in the configuration history and the audit log. The proxy must replace the
    /// header, not add to one the client sent.
    /// </summary>
    public string? TrustedIdentityHeader { get; set; }

    /// <summary>
    /// Header with the signed-in user's groups, comma separated (e.g. <c>X-Forwarded-Groups</c>), for
    /// <see cref="TrustedIdentityRoles"/> entries that name a group. Optional; believed only with <see cref="TrustedIdentityHeader"/>.
    /// </summary>
    public string? TrustedGroupsHeader { get; set; }

    /// <summary>
    /// The role of each user or group a trusted proxy names, e.g. <c>{ "alice@corp.example": "Admin", "router-admins":
    /// "Admin", "finance": "Metrics", "*": "ReadOnly" }</c>. The user's own entry wins, then the highest role among their
    /// groups (<c>Admin</c>, then <c>ReadOnly</c>, then <c>Metrics</c>), then <c>*</c>. Names are compared ignoring case.
    /// </summary>
    public IDictionary<string, RouterDashboardRole> TrustedIdentityRoles { get; } = new Dictionary<string, RouterDashboardRole>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Entries shown by the activity endpoint when the client gives no limit. Default 100.</summary>
    public int DefaultActivityLimit { get; set; } = 100;

    /// <summary>
    /// Directory of drop-in themes: one <c>{id}.css</c> file per theme declaring <c>:root[data-theme="{id}"] { … }</c>,
    /// with an optional <c>{id}.json</c> manifest (<c>{ "name": "Ocean" }</c>). Default: a <c>themes</c> directory next to
    /// the configuration file (<c>UseConfigurationFile</c>), or none. See docs/dashboard.md#themes.
    /// </summary>
    public string? ThemesDirectory { get; set; }
}
