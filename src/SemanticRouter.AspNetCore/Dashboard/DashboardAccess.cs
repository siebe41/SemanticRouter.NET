using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace SemanticRouter.AspNetCore;

/// <summary>What a dashboard caller may do.</summary>
public enum RouterDashboardRole
{
    /// <summary>
    /// Aggregate figures only: status and health, usage and cost totals, activity counts and the digest's counts, on the
    /// endpoints marked for it. Never prompt or reply text, utterances, the configuration, memory or the audit log.
    /// </summary>
    Metrics = 0,

    /// <summary>
    /// View status, activity and the (masked) configuration. No changes, and nothing that spends embedding or API calls
    /// or scans the disk: classifying, evaluating, testing embeddings, checking or discovering connectors, rescanning memory.
    /// </summary>
    ReadOnly = 1,

    /// <summary>Everything, including applying, saving and resetting the configuration.</summary>
    Admin = 2,
}

/// <summary>
/// Marks an admin API endpoint that changes the router, or spends embedding or API calls or scans the disk (refused for
/// <see cref="RouterDashboardRole.ReadOnly"/> and <see cref="RouterDashboardRole.Metrics"/>).
/// </summary>
internal sealed class DashboardWriteMetadata
{
    public static DashboardWriteMetadata Instance { get; } = new();
}

/// <summary>
/// Marks an endpoint the <see cref="RouterDashboardRole.Metrics"/> role may call: one that answers figures, never prompt
/// or reply text, utterances, configuration or memory content. Every other endpoint is refused to that role, so a new
/// endpoint stays closed to it until it is marked.
/// </summary>
internal sealed class DashboardMetricsMetadata
{
    public static DashboardMetricsMetadata Instance { get; } = new();
}

/// <summary>
/// Marks a read-only endpoint that needs the admin role all the same (the audit log). Under a read-only machine policy it
/// stays open to those who would otherwise be admin, since reading it changes nothing.
/// </summary>
internal sealed class DashboardAdminReadMetadata
{
    public static DashboardAdminReadMetadata Instance { get; } = new();
}

/// <summary>
/// Set on a request that came straight from a known reverse proxy (<see cref="RouterForwardedHeadersExtensions.UseRouterForwardedHeaders"/>).
/// Internal, so nothing but that middleware can set it.
/// </summary>
/// <param name="Proxy">The proxy's address.</param>
internal sealed record RouterProxyFeature(IPAddress Proxy)
{
    public static bool IsKnown(IPAddress peer, IReadOnlyCollection<IPAddress> proxies, IReadOnlyCollection<System.Net.IPNetwork> networks)
    {
        IPAddress[] forms = peer.IsIPv4MappedToIPv6 ? [peer, peer.MapToIPv4()] : [peer];
        return forms.Any(address => proxies.Contains(address) || networks.Any(network => network.Contains(address)));
    }
}

/// <summary>Marks a dashboard endpoint that answers without a role (static assets and the sign-in endpoints).</summary>
internal sealed class DashboardAnonymousMetadata
{
    public static DashboardAnonymousMetadata Instance { get; } = new();
}

/// <summary>
/// Decides who may use the dashboard and admin API: loopback clients (with a Host header naming this machine, which
/// stops DNS-rebinding pages from driving it), remote clients presenting the admin, read-only or metrics token as a
/// bearer token or a session cookie, or users an authenticating reverse proxy names in
/// <see cref="RouterDashboardOptions.TrustedIdentityHeader"/> (believed only from a known proxy). State-changing calls
/// must come from the dashboard's own origin. A session cookie carries the time it was issued and a random session id,
/// signed with the token, and lapses after <see cref="RouterDashboardOptions.SessionLifetime"/>; signing out revokes the
/// id on the server (<see cref="DashboardSessions"/>). The metrics role reaches only endpoints marked
/// <see cref="DashboardMetricsMetadata"/>. Wrong tokens are
/// throttled per address (<see cref="SignInThrottle"/>). Who each request acts as (<see cref="ActorOf"/>) goes into the
/// configuration history and the audit log.
/// </summary>
internal sealed class DashboardAccess
{
    public const string CookieName = "semantic-router-session";
    private const string RoleKey = "SemanticRouter.DashboardRole";
    private const string ActorKey = "SemanticRouter.DashboardActor";
    private const string ProxyUserKey = "SemanticRouter.DashboardProxyUser";
    private const string WrongTokenKey = "SemanticRouter.DashboardWrongToken";
    private const string SessionKey = "SemanticRouter.DashboardSession";

    // v3 added the session id. Cookies of the earlier "{kind}.{issued}.{mac}" form are refused: they cannot be revoked.
    private const string SessionLabel = "semantic-router-dashboard-session-v3";

    /// <summary>A session id's length: 16 random bytes, base64url without padding.</summary>
    private const int SessionIdLength = 22;

    /// <summary>The longest user or group name taken from the identity headers.</summary>
    private const int MaxIdentityLength = 256;

    // A session issued this far in the future (the clock went back) is still accepted.
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    private readonly RouterDashboardOptions _options;
    private readonly byte[]? _admin;
    private readonly byte[]? _readOnly;
    private readonly byte[]? _metrics;
    private readonly HashSet<string> _publicAuthorities;
    private readonly DashboardPolicy _policy;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, RouterDashboardRole> _identityRoles;

    public DashboardAccess(
        RouterDashboardOptions options, string? adminToken, string? readOnlyToken, DashboardPolicy policy = DashboardPolicy.Default, TimeProvider? time = null,
        string? metricsToken = null, IStateValueStore? sessionStore = null)
    {
        _options = options;
        _policy = policy;
        _admin = string.IsNullOrEmpty(adminToken) ? null : Encoding.UTF8.GetBytes(adminToken);
        _readOnly = string.IsNullOrEmpty(readOnlyToken) ? null : Encoding.UTF8.GetBytes(readOnlyToken);
        _metrics = string.IsNullOrEmpty(metricsToken) ? null : Encoding.UTF8.GetBytes(metricsToken);
        _publicAuthorities = LocalHosts.Authorities(options.PublicOrigins);
        _time = time ?? TimeProvider.System;
        _identityRoles = new Dictionary<string, RouterDashboardRole>(options.TrustedIdentityRoles, StringComparer.OrdinalIgnoreCase);
        Throttle = new SignInThrottle(_time);
        Sessions = new DashboardSessions(sessionStore, _time, options.SessionLifetime + ClockSkew);
    }

    /// <summary>Failed sign-ins per address.</summary>
    public SignInThrottle Throttle { get; }

    /// <summary>Sessions ended on the server.</summary>
    public DashboardSessions Sessions { get; }

    /// <summary>Whether any token is configured, so remote clients can sign in.</summary>
    public bool TokensConfigured => _admin is not null || _readOnly is not null || _metrics is not null;

    /// <summary>The role the current request was admitted with, once the filter has run.</summary>
    public static RouterDashboardRole? RoleOf(HttpContext context) =>
        context.Items.TryGetValue(RoleKey, out object? role) ? role as RouterDashboardRole? : null;

    /// <summary>
    /// Who the current request acts as, once the filter has run: the user a trusted proxy named, <c>admin token</c>,
    /// <c>read-only token</c>, <c>metrics token</c>, <c>this machine</c> or <c>unauthenticated remote client</c>. Never
    /// contains a token.
    /// </summary>
    public static string? ActorOf(HttpContext context) =>
        context.Items.TryGetValue(ActorKey, out object? actor) ? actor as string : null;

    /// <summary>Records who the request acts as (the sign-in endpoint, once it has checked the token).</summary>
    public static void SetActor(HttpContext context, string actor) => context.Items[ActorKey] = actor;

    /// <summary>The actor name for a token role.</summary>
    public static string TokenActor(RouterDashboardRole role) => role switch
    {
        RouterDashboardRole.Admin => "admin token",
        RouterDashboardRole.ReadOnly => "read-only token",
        _ => "metrics token",
    };

    /// <summary>Whether the request presented a wrong token (counted by <see cref="Throttle"/>).</summary>
    public static bool PresentedWrongToken(HttpContext context) => context.Items.ContainsKey(WrongTokenKey);

    /// <summary>Counts a wrong token from the caller's address and marks the request for the audit log.</summary>
    public void WrongToken(HttpContext context)
    {
        Throttle.Failed(context);
        context.Items[WrongTokenKey] = true;
    }

    /// <summary>Whether the request comes from this machine (no remote address counts as local, e.g. in-process tests).</summary>
    public static bool IsLocal(HttpContext context) =>
        context.Connection.RemoteIpAddress is not { } remote || IPAddress.IsLoopback(remote);

    /// <summary>Whether this caller must present a token to get a role (not when a trusted proxy named the user).</summary>
    public bool TokenRequired(HttpContext context) =>
        !context.Items.ContainsKey(ProxyUserKey) && (!IsLocal(context) || _options.RequireTokenForLocalClients);

    /// <summary>Whether <see cref="RouterDashboardOptions.TrustedIdentityHeader"/> is set.</summary>
    public bool IdentityHeaderConfigured => !string.IsNullOrWhiteSpace(_options.TrustedIdentityHeader);

    public async ValueTask<object?> FilterAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        HttpContext context = invocation.HttpContext;
        if (_policy == DashboardPolicy.Off)
        {
            return Refuse(StatusCodes.Status403Forbidden, "The router dashboard is turned off by your organization's policy.");
        }

        bool local = IsLocal(context);
        if (!local && _options.RequireLocalClient)
        {
            return Refuse(StatusCodes.Status403Forbidden,
                "The router dashboard only answers requests from this machine (RouterDashboardOptions.RequireLocalClient).");
        }

        // A user an authenticating proxy named. Only a request that came straight from a known proxy may name one; from
        // anyone else the header is ignored, as if it were not there.
        (string User, RouterDashboardRole? Role)? identity = null;
        if (IdentityHeaderConfigured && context.Features.Get<RouterProxyFeature>() is not null
            && context.Request.Headers.TryGetValue(_options.TrustedIdentityHeader!, out StringValues named))
        {
            if (ReadIdentity(named) is not { } user)
            {
                return Refuse(StatusCodes.Status403Forbidden,
                    $"The {_options.TrustedIdentityHeader} header from the proxy does not name one user. Make the proxy replace the header rather than add to what the client sent.");
            }

            // As for loopback clients without a token: a page that reached the router through DNS rebinding (and so from a
            // proxy's address, when the proxy runs on this machine) could set the header itself, but not a Host naming us.
            if (!IsLocalHost(context.Request))
            {
                return Refuse(StatusCodes.Status403Forbidden,
                    $"Host '{context.Request.Host.Host}' is not this machine. Add the address people browse to (the proxy's public address) to RouterDashboardOptions.PublicOrigins.");
            }

            identity = (user, RoleForIdentity(user, Groups(context.Request)));
            context.Items[ProxyUserKey] = user;
            SetActor(context, user);
        }

        if (!local && !TokensConfigured && !_options.AllowUnauthenticatedRemoteClients && identity is null)
        {
            return Refuse(StatusCodes.Status403Forbidden,
                "Remote access to the router dashboard needs a token: set SemanticRouter:Dashboard:AdminToken (or ReadOnlyToken, MetricsToken), " +
                "or AllowUnauthenticatedRemoteClients when a reverse proxy authenticates callers.");
        }

        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !IsSameOrigin(context.Request))
        {
            return Refuse(StatusCodes.Status403Forbidden, "Cross-origin requests cannot change the router (Origin does not match the dashboard).");
        }

        Endpoint? endpoint = context.GetEndpoint();
        RouterDashboardRole? role;
        if (identity is { } person)
        {
            // Behind the proxy, the proxy decides who this is: tokens and session cookies are not consulted.
            role = person.Role;
            if (role is null)
            {
                return endpoint?.Metadata.GetMetadata<DashboardAnonymousMetadata>() is not null
                    ? await next(invocation).ConfigureAwait(false)
                    : Refuse(StatusCodes.Status403Forbidden,
                        $"'{person.User}' has no dashboard role: add the user, one of their groups or \"*\" to SemanticRouter:Dashboard:TrustedIdentityRoles.");
            }
        }
        else
        {
            if (context.Request.Headers.Authorization.Count > 0 && TokenRequired(context) && Throttle.RetryAfter(context) is { } wait)
            {
                return TooManyAttempts(context, wait);
            }

            role = Authenticate(context.Request);
            if (role is not null)
            {
                SetActor(context, TokenActor(role.Value));
            }
            else if (local && !_options.RequireTokenForLocalClients)
            {
                if (!IsLocalHost(context.Request))
                {
                    return Refuse(StatusCodes.Status403Forbidden,
                        $"Host '{context.Request.Host.Host}' is not this machine. Browse to http://localhost:{context.Request.Host.Port} " +
                        "or add the public address to RouterDashboardOptions.PublicOrigins.");
                }

                role = RouterDashboardRole.Admin;
                SetActor(context, "this machine");
            }
            else if (!local && !TokensConfigured && _options.AllowUnauthenticatedRemoteClients)
            {
                role = RouterDashboardRole.Admin;
                SetActor(context, "unauthenticated remote client");
            }
        }

        if (role is null)
        {
            return endpoint?.Metadata.GetMetadata<DashboardAnonymousMetadata>() is not null
                ? await next(invocation).ConfigureAwait(false)
                : Refuse(StatusCodes.Status401Unauthorized, "Sign in with the dashboard token (Authorization: Bearer <token>).");
        }

        // The metrics role reaches only the endpoints marked for it (and those that need no role): a new endpoint is closed
        // to it until someone decides it shows no prompt text.
        if (role == RouterDashboardRole.Metrics && endpoint?.Metadata.GetMetadata<DashboardMetricsMetadata>() is null
            && endpoint?.Metadata.GetMetadata<DashboardAnonymousMetadata>() is null)
        {
            return Refuse(StatusCodes.Status403Forbidden, "The metrics role sees figures only: status, usage and the digest's counts.");
        }

        if (role != RouterDashboardRole.Admin && endpoint?.Metadata.GetMetadata<DashboardAdminReadMetadata>() is not null)
        {
            return Refuse(StatusCodes.Status403Forbidden, "Only the admin role can read the audit log.");
        }

        if (_policy == DashboardPolicy.ReadOnly && role == RouterDashboardRole.Admin)
        {
            // Nobody changes the router from here: the role is clamped, so the page shows it read-only too.
            if (endpoint?.Metadata.GetMetadata<DashboardWriteMetadata>() is not null)
            {
                return Refuse(StatusCodes.Status403Forbidden, "Your organization's policy makes the dashboard read-only: the router cannot be changed from it.");
            }

            role = RouterDashboardRole.ReadOnly;
        }

        if (role != RouterDashboardRole.Admin && endpoint?.Metadata.GetMetadata<DashboardWriteMetadata>() is not null)
        {
            return Refuse(StatusCodes.Status403Forbidden, "The read-only token cannot change the router.");
        }

        context.Items[RoleKey] = role;
        return await next(invocation).ConfigureAwait(false);
    }

    /// <summary>The role a token grants, or <see langword="null"/>.</summary>
    public RouterDashboardRole? RoleForToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        byte[] presented = Encoding.UTF8.GetBytes(token);
        if (_admin is not null && FixedTimeEquals(presented, _admin))
        {
            return RouterDashboardRole.Admin;
        }

        if (_readOnly is not null && FixedTimeEquals(presented, _readOnly))
        {
            return RouterDashboardRole.ReadOnly;
        }

        return _metrics is not null && FixedTimeEquals(presented, _metrics) ? RouterDashboardRole.Metrics : null;
    }

    /// <summary>
    /// Sets the session cookie for <paramref name="role"/>, good for <see cref="RouterDashboardOptions.SessionLifetime"/>
    /// or until signed out. The cookie holds the time it was issued, a random session id and a value derived from them and
    /// the token, never the token.
    /// </summary>
    public void SignIn(HttpContext context, RouterDashboardRole role, string path)
    {
        string id = Base64Url(RandomNumberGenerator.GetBytes(16));
        context.Response.Cookies.Append(CookieName, SessionValue(role, _time.GetUtcNow().ToUnixTimeMilliseconds(), id)!, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = path,
            IsEssential = true,
            MaxAge = _options.SessionLifetime,
        });
    }

    /// <summary>A 429 telling a caller with too many wrong tokens when to try again.</summary>
    public static IResult TooManyAttempts(HttpContext context, TimeSpan wait)
    {
        int seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Results.Problem(
            $"Too many wrong tokens from this address. Try again in {Math.Max(1, (int)Math.Ceiling(seconds / 60.0))} minute(s).",
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Too many sign-in attempts");
    }

    /// <summary>
    /// Clears the session cookie and, when the request was admitted with one, revokes its id on the server so the cookie
    /// is refused even if replayed. Returns whether a session was revoked.
    /// </summary>
    public bool SignOut(HttpContext context, string path)
    {
        context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = path, SameSite = SameSiteMode.Strict, HttpOnly = true });
        if (context.Items.TryGetValue(SessionKey, out object? value) && value is Session session)
        {
            Sessions.Revoke(session.Id, session.Issued);
            return true;
        }

        return false;
    }

    private RouterDashboardRole? Authenticate(HttpRequest request)
    {
        string? authorization = request.Headers.Authorization;
        if (authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            RouterDashboardRole? bearer = RoleForToken(authorization["Bearer ".Length..].Trim());
            if (bearer is null)
            {
                WrongToken(request.HttpContext);
            }

            return bearer;
        }

        // "{kind}.{issued}.{id}.{mac}": kind a, r or m; issued in Unix milliseconds; the mac signed with that role's token.
        if (request.Cookies.TryGetValue(CookieName, out string? cookie) && cookie?.Split('.') is [var kind, var stamp, var id, _]
            && RoleOfKind(kind) is { } role && id.Length == SessionIdLength && id.All(IsBase64UrlChar)
            && long.TryParse(stamp, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long issued)
            && issued <= DateTimeOffset.MaxValue.ToUnixTimeMilliseconds())
        {
            TimeSpan age = _time.GetUtcNow() - DateTimeOffset.FromUnixTimeMilliseconds(issued);
            if (age < _options.SessionLifetime && age > -ClockSkew
                && SessionValue(role, issued, id) is { } expected && FixedTimeEquals(Encoding.UTF8.GetBytes(cookie), Encoding.UTF8.GetBytes(expected))
                && !Sessions.IsRevoked(id, issued))
            {
                request.HttpContext.Items[SessionKey] = new Session(id, issued);
                return role;
            }
        }

        return null;
    }

    private static RouterDashboardRole? RoleOfKind(string kind) => kind switch
    {
        "a" => RouterDashboardRole.Admin,
        "r" => RouterDashboardRole.ReadOnly,
        "m" => RouterDashboardRole.Metrics,
        _ => null,
    };

    private static bool IsBase64UrlChar(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// The role for a user a trusted proxy named: the user's own entry in <see cref="RouterDashboardOptions.TrustedIdentityRoles"/>,
    /// else the highest role among their groups' entries, else the <c>*</c> entry; <see langword="null"/> when none applies.
    /// </summary>
    internal RouterDashboardRole? RoleForIdentity(string user, IEnumerable<string> groups)
    {
        if (_identityRoles.TryGetValue(user, out RouterDashboardRole own))
        {
            return own;
        }

        RouterDashboardRole? best = null;
        foreach (string group in groups)
        {
            if (group != "*" && _identityRoles.TryGetValue(group, out RouterDashboardRole role) && (best is null || role > best))
            {
                best = role;
            }
        }

        return best ?? (_identityRoles.TryGetValue("*", out RouterDashboardRole everyone) ? everyone : null);
    }

    /// <summary>
    /// The one user an identity header names, or <see langword="null"/> when it is empty, repeated, a list, too long or
    /// holds control characters: a proxy that appends to a client's header instead of replacing it must not be believed.
    /// </summary>
    private static string? ReadIdentity(StringValues values)
    {
        if (values.Count != 1 || values[0] is not { } value)
        {
            return null;
        }

        value = value.Trim();
        return value.Length is > 0 and <= MaxIdentityLength && value != "*" && !value.Contains(',', StringComparison.Ordinal) && !value.Any(char.IsControl)
            ? value
            : null;
    }

    private IEnumerable<string> Groups(HttpRequest request) =>
        string.IsNullOrWhiteSpace(_options.TrustedGroupsHeader)
            ? []
            : request.Headers[_options.TrustedGroupsHeader]
                .SelectMany(v => (v ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                .Where(g => g.Length <= MaxIdentityLength && !g.Any(char.IsControl));

    private string? SessionValue(RouterDashboardRole role, long issued, string id)
    {
        (byte[]? token, string kind) = role switch
        {
            RouterDashboardRole.Admin => (_admin, "a"),
            RouterDashboardRole.ReadOnly => (_readOnly, "r"),
            _ => (_metrics, "m"),
        };
        if (token is null)
        {
            return null;
        }

        // Signed with the token, so changing the token signs every browser out, and the issue time and id cannot be moved.
        string stamp = issued.ToString(System.Globalization.CultureInfo.InvariantCulture);
        byte[] mac = HMACSHA256.HashData(token, Encoding.UTF8.GetBytes($"{SessionLabel}\n{kind}\n{stamp}\n{id}"));
        return $"{kind}.{stamp}.{id}." + Base64Url(mac);
    }

    /// <summary>The session a request was admitted with: its id and when it was issued (Unix milliseconds).</summary>
    private sealed record Session(string Id, long Issued);

    private bool IsSameOrigin(HttpRequest request)
    {
        string? origin = request.Headers.Origin;
        if (string.IsNullOrEmpty(origin))
        {
            // Browsers send Origin on cross-origin writes; without it, only Fetch Metadata can say where a request came from.
            string? site = request.Headers["Sec-Fetch-Site"];
            return site is null || site is "same-origin" or "none";
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        string authority = LocalHosts.Authority(uri);
        return string.Equals(authority, request.Host.Value, StringComparison.OrdinalIgnoreCase) || _publicAuthorities.Contains(authority);
    }

    private bool IsLocalHost(HttpRequest request) =>
        LocalHosts.IsLocalName(request.Host.Host) || _publicAuthorities.Contains(request.Host.Value!);

    private static bool FixedTimeEquals(byte[] a, byte[] b) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(a), SHA256.HashData(b));

    private static IResult Refuse(int status, string detail) =>
        Results.Problem(detail, statusCode: status, title: status == StatusCodes.Status401Unauthorized ? "Sign-in required" : "Forbidden");
}
