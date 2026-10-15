using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace SemanticRouter.AspNetCore;

/// <summary>What a dashboard caller may do.</summary>
public enum RouterDashboardRole
{
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
/// <see cref="RouterDashboardRole.ReadOnly"/>).
/// </summary>
internal sealed class DashboardWriteMetadata
{
    public static DashboardWriteMetadata Instance { get; } = new();
}

/// <summary>Marks a dashboard endpoint that answers without a role (static assets and the sign-in endpoints).</summary>
internal sealed class DashboardAnonymousMetadata
{
    public static DashboardAnonymousMetadata Instance { get; } = new();
}

/// <summary>
/// Decides who may use the dashboard and admin API: loopback clients (with a Host header naming this machine, which
/// stops DNS-rebinding pages from driving it), or remote clients presenting the admin or read-only token as a bearer
/// token or a session cookie. State-changing calls must come from the dashboard's own origin. A session cookie carries
/// the time it was issued, signed with the token, and lapses after <see cref="RouterDashboardOptions.SessionLifetime"/>;
/// nothing is kept on the server. Wrong tokens are throttled per address (<see cref="SignInThrottle"/>).
/// </summary>
internal sealed class DashboardAccess
{
    public const string CookieName = "semantic-router-session";
    private const string RoleKey = "SemanticRouter.DashboardRole";
    private const string SessionLabel = "semantic-router-dashboard-session-v2";

    // A session issued this far in the future (the clock went back) is still accepted.
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    private readonly RouterDashboardOptions _options;
    private readonly byte[]? _admin;
    private readonly byte[]? _readOnly;
    private readonly HashSet<string> _publicAuthorities;
    private readonly DashboardPolicy _policy;
    private readonly TimeProvider _time;

    public DashboardAccess(
        RouterDashboardOptions options, string? adminToken, string? readOnlyToken, DashboardPolicy policy = DashboardPolicy.Default, TimeProvider? time = null)
    {
        _options = options;
        _policy = policy;
        _admin = string.IsNullOrEmpty(adminToken) ? null : Encoding.UTF8.GetBytes(adminToken);
        _readOnly = string.IsNullOrEmpty(readOnlyToken) ? null : Encoding.UTF8.GetBytes(readOnlyToken);
        _publicAuthorities = LocalHosts.Authorities(options.PublicOrigins);
        _time = time ?? TimeProvider.System;
        Throttle = new SignInThrottle(_time);
    }

    /// <summary>Failed sign-ins per address.</summary>
    public SignInThrottle Throttle { get; }

    /// <summary>Whether any token is configured, so remote clients can sign in.</summary>
    public bool TokensConfigured => _admin is not null || _readOnly is not null;

    /// <summary>The role the current request was admitted with, once the filter has run.</summary>
    public static RouterDashboardRole? RoleOf(HttpContext context) =>
        context.Items.TryGetValue(RoleKey, out object? role) ? role as RouterDashboardRole? : null;

    /// <summary>Whether the request comes from this machine (no remote address counts as local, e.g. in-process tests).</summary>
    public static bool IsLocal(HttpContext context) =>
        context.Connection.RemoteIpAddress is not { } remote || IPAddress.IsLoopback(remote);

    /// <summary>Whether this caller must present a token to get a role.</summary>
    public bool TokenRequired(HttpContext context) => !IsLocal(context) || _options.RequireTokenForLocalClients;

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

        if (!local && !TokensConfigured && !_options.AllowUnauthenticatedRemoteClients)
        {
            return Refuse(StatusCodes.Status403Forbidden,
                "Remote access to the router dashboard needs a token: set SemanticRouter:Dashboard:AdminToken (or ReadOnlyToken), " +
                "or AllowUnauthenticatedRemoteClients when a reverse proxy authenticates callers.");
        }

        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !IsSameOrigin(context.Request))
        {
            return Refuse(StatusCodes.Status403Forbidden, "Cross-origin requests cannot change the router (Origin does not match the dashboard).");
        }

        if (context.Request.Headers.Authorization.Count > 0 && TokenRequired(context) && Throttle.RetryAfter(context) is { } wait)
        {
            return TooManyAttempts(context, wait);
        }

        RouterDashboardRole? role = Authenticate(context.Request);
        if (role is null)
        {
            if (local && !_options.RequireTokenForLocalClients)
            {
                if (!IsLocalHost(context.Request))
                {
                    return Refuse(StatusCodes.Status403Forbidden,
                        $"Host '{context.Request.Host.Host}' is not this machine. Browse to http://localhost:{context.Request.Host.Port} " +
                        "or add the public address to RouterDashboardOptions.PublicOrigins.");
                }

                role = RouterDashboardRole.Admin;
            }
            else if (!local && !TokensConfigured && _options.AllowUnauthenticatedRemoteClients)
            {
                role = RouterDashboardRole.Admin;
            }
        }

        Endpoint? endpoint = context.GetEndpoint();
        if (role is null)
        {
            return endpoint?.Metadata.GetMetadata<DashboardAnonymousMetadata>() is not null
                ? await next(invocation).ConfigureAwait(false)
                : Refuse(StatusCodes.Status401Unauthorized, "Sign in with the dashboard token (Authorization: Bearer <token>).");
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

        if (role == RouterDashboardRole.ReadOnly && endpoint?.Metadata.GetMetadata<DashboardWriteMetadata>() is not null)
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

        return _readOnly is not null && FixedTimeEquals(presented, _readOnly) ? RouterDashboardRole.ReadOnly : null;
    }

    /// <summary>
    /// Sets the session cookie for <paramref name="role"/>, good for <see cref="RouterDashboardOptions.SessionLifetime"/>.
    /// The cookie holds the time it was issued and a value derived from it and the token, never the token.
    /// </summary>
    public void SignIn(HttpContext context, RouterDashboardRole role, string path)
    {
        context.Response.Cookies.Append(CookieName, SessionValue(role, _time.GetUtcNow().ToUnixTimeSeconds())!, new CookieOptions
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

    public static void SignOut(HttpContext context, string path) =>
        context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = path, SameSite = SameSiteMode.Strict, HttpOnly = true });

    private RouterDashboardRole? Authenticate(HttpRequest request)
    {
        string? authorization = request.Headers.Authorization;
        if (authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            RouterDashboardRole? bearer = RoleForToken(authorization["Bearer ".Length..].Trim());
            if (bearer is null)
            {
                Throttle.Failed(request.HttpContext);
            }

            return bearer;
        }

        // "a.{issued}.{mac}" or "r.{issued}.{mac}": issued in Unix seconds, the mac signed with that role's token.
        if (request.Cookies.TryGetValue(CookieName, out string? cookie) && cookie?.Split('.') is [var kind, var stamp, _]
            && long.TryParse(stamp, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long issued)
            && issued <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            RouterDashboardRole role = kind == "a" ? RouterDashboardRole.Admin : RouterDashboardRole.ReadOnly;
            TimeSpan age = _time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(issued);
            if (kind is "a" or "r" && age < _options.SessionLifetime && age > -ClockSkew
                && SessionValue(role, issued) is { } expected && FixedTimeEquals(Encoding.UTF8.GetBytes(cookie), Encoding.UTF8.GetBytes(expected)))
            {
                return role;
            }
        }

        return null;
    }

    private string? SessionValue(RouterDashboardRole role, long issued)
    {
        byte[]? token = role == RouterDashboardRole.Admin ? _admin : _readOnly;
        if (token is null)
        {
            return null;
        }

        // Signed with the token, so changing the token signs every browser out, and the issue time cannot be moved.
        string kind = role == RouterDashboardRole.Admin ? "a" : "r";
        string stamp = issued.ToString(System.Globalization.CultureInfo.InvariantCulture);
        byte[] mac = HMACSHA256.HashData(token, Encoding.UTF8.GetBytes($"{SessionLabel}\n{kind}\n{stamp}"));
        return $"{kind}.{stamp}." + Convert.ToBase64String(mac).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

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
