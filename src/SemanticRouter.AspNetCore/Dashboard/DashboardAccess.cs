using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace SemanticRouter.AspNetCore;

/// <summary>What a dashboard caller may do.</summary>
public enum RouterDashboardRole
{
    /// <summary>View status, activity and the (masked) configuration; classify prompts. No changes.</summary>
    ReadOnly = 1,

    /// <summary>Everything, including applying, saving and resetting the configuration.</summary>
    Admin = 2,
}

/// <summary>Marks an admin API endpoint that changes the router (refused for <see cref="RouterDashboardRole.ReadOnly"/>).</summary>
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
/// token or a session cookie. State-changing calls must come from the dashboard's own origin.
/// </summary>
internal sealed class DashboardAccess
{
    public const string CookieName = "semantic-router-session";
    private const string RoleKey = "SemanticRouter.DashboardRole";
    private static readonly byte[] SessionLabel = Encoding.UTF8.GetBytes("semantic-router-dashboard-session-v1");

    private readonly RouterDashboardOptions _options;
    private readonly byte[]? _admin;
    private readonly byte[]? _readOnly;
    private readonly HashSet<string> _publicAuthorities;

    public DashboardAccess(RouterDashboardOptions options, string? adminToken, string? readOnlyToken)
    {
        _options = options;
        _admin = string.IsNullOrEmpty(adminToken) ? null : Encoding.UTF8.GetBytes(adminToken);
        _readOnly = string.IsNullOrEmpty(readOnlyToken) ? null : Encoding.UTF8.GetBytes(readOnlyToken);
        _publicAuthorities = LocalHosts.Authorities(options.PublicOrigins);
    }

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

    /// <summary>Sets the session cookie for <paramref name="role"/>. The cookie holds a value derived from the token, never the token.</summary>
    public void SignIn(HttpContext context, RouterDashboardRole role, string path)
    {
        context.Response.Cookies.Append(CookieName, SessionValue(role)!, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = path,
            IsEssential = true,
        });
    }

    public static void SignOut(HttpContext context, string path) =>
        context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = path, SameSite = SameSiteMode.Strict, HttpOnly = true });

    private RouterDashboardRole? Authenticate(HttpRequest request)
    {
        string? authorization = request.Headers.Authorization;
        if (authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return RoleForToken(authorization["Bearer ".Length..].Trim());
        }

        if (request.Cookies.TryGetValue(CookieName, out string? cookie) && !string.IsNullOrEmpty(cookie))
        {
            byte[] presented = Encoding.UTF8.GetBytes(cookie);
            foreach (RouterDashboardRole role in new[] { RouterDashboardRole.Admin, RouterDashboardRole.ReadOnly })
            {
                if (SessionValue(role) is { } expected && FixedTimeEquals(presented, Encoding.UTF8.GetBytes(expected)))
                {
                    return role;
                }
            }
        }

        return null;
    }

    private string? SessionValue(RouterDashboardRole role)
    {
        byte[]? token = role == RouterDashboardRole.Admin ? _admin : _readOnly;
        if (token is null)
        {
            return null;
        }

        // Derived from the token, so changing the token signs every browser out.
        return (role == RouterDashboardRole.Admin ? "a." : "r.") + Convert.ToBase64String(HMACSHA256.HashData(token, SessionLabel))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
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
