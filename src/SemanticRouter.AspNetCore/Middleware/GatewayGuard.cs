using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Keeps web pages from using the gateway. With no CORS a page on another site cannot read the gateway's replies, but
/// a page that rebinds its own host name to 127.0.0.1 becomes same-origin with it and would get the router's keys and
/// subscriptions for free. So a request without a gateway key must name this machine in its Host header when it comes
/// from this machine, and a browser's Origin must be the router's own. Requests with a valid gateway key are not
/// checked: a web page does not have one.
/// </summary>
internal sealed class GatewayGuard(IEnumerable<string> publicOrigins)
{
    private static readonly GatewayGuard Default = new([]);

    private readonly HashSet<string> _publicAuthorities = LocalHosts.Authorities(publicOrigins);

    /// <summary>The registered guard (which knows the dashboard's public origins), or one that knows none.</summary>
    public static GatewayGuard For(HttpContext context) => context.RequestServices.GetService<GatewayGuard>() ?? Default;

    /// <summary>Why a request that presented no gateway key is refused, or <see langword="null"/> when it may go on.</summary>
    public string? Refusal(HttpContext context)
    {
        HttpRequest request = context.Request;
        if (DashboardAccess.IsLocal(context) && !LocalHosts.IsLocalName(request.Host.Host) && !_publicAuthorities.Contains(request.Host.Value ?? string.Empty))
        {
            return $"Host '{request.Host.Host}' is not this machine. Use http://localhost:{request.Host.Port ?? 80}, send a gateway key, " +
                   "or add the address to SemanticRouter:Dashboard:PublicOrigins.";
        }

        string? origin = request.Headers.Origin;
        if (!string.IsNullOrEmpty(origin)
            && (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? uri)
                || (!string.Equals(LocalHosts.Authority(uri), request.Host.Value, StringComparison.OrdinalIgnoreCase)
                    && !_publicAuthorities.Contains(LocalHosts.Authority(uri)))))
        {
            return "Web pages on other sites cannot use the gateway (the request's Origin is not the router's own).";
        }

        return null;
    }
}

/// <summary>Host names that mean this machine.</summary>
internal static class LocalHosts
{
    /// <summary><c>localhost</c>, a <c>*.localhost</c> name, a loopback address, or no host at all (in-process callers).</summary>
    public static bool IsLocalName(string? host) =>
        string.IsNullOrEmpty(host)
        || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? address) && IPAddress.IsLoopback(address));

    /// <summary>The <c>host[:port]</c> a browser puts in the Host header for <paramref name="uri"/>.</summary>
    public static string Authority(Uri uri) => uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}";

    /// <summary>The authorities of absolute origins such as <c>https://router.example.com</c>; anything else is ignored.</summary>
    public static HashSet<string> Authorities(IEnumerable<string> origins) =>
        new(
            origins
                .Select(o => Uri.TryCreate(o.Trim(), UriKind.Absolute, out Uri? uri) ? Authority(uri) : null)
                .Where(a => a is not null)
                .Cast<string>(),
            StringComparer.OrdinalIgnoreCase);
}

/// <summary>Whether a gateway request may go on, and as which client.</summary>
/// <param name="Client">The gateway client its key names, or <see langword="null"/> when it sent no valid key.</param>
/// <param name="Status">The status to refuse it with; 0 when it may go on.</param>
/// <param name="Title">A short reason for the refusal.</param>
/// <param name="Detail">What to do about it.</param>
internal readonly record struct GatewayAdmission(string? Client, int Status = 0, string? Title = null, string? Detail = null)
{
    public bool Refused => Status != 0;

    /// <summary>The key check that refused it (401), rather than the web-page check (403).</summary>
    public bool KeyMissing => Status == StatusCodes.Status401Unauthorized;
}

/// <summary>Who may use the gateway: the same rule for the chat endpoints and the ones the middleware does not intercept.</summary>
internal static class GatewayAuthentication
{
    /// <summary>
    /// Admits a request with a valid gateway key as its client. Without one, a request from another machine is refused
    /// (with no keys configured too, unless <c>AllowUnauthenticatedRemoteClients</c> is on), as is one from this machine
    /// when <c>RequireGatewayKeyForLocalClients</c> is on; a web page is refused by <see cref="GatewayGuard"/>.
    /// </summary>
    public static GatewayAdmission Admit(HttpContext context, RouterRuntime runtime)
    {
        string? client = runtime.GatewayKeys.Count > 0 ? PromptRoutingMiddleware.IdentifyClient(context.Request, runtime.GatewayKeys) : null;
        if (client is not null)
        {
            return new GatewayAdmission(client);
        }

        PromptRouterMiddlewareOptions options = runtime.Configuration.Middleware;

        // Configured keys count even when their references did not resolve: a missing variable must not open the gateway.
        bool keysConfigured = options.GatewayKeys.Count > 0;
        if (!DashboardAccess.IsLocal(context) || options.RequireGatewayKeyForLocalClients)
        {
            if (keysConfigured)
            {
                return new GatewayAdmission(
                    null, StatusCodes.Status401Unauthorized, "Gateway key required",
                    "This router requires a gateway key: send it as 'Authorization: Bearer <key>' or 'x-api-key: <key>' " +
                    "(ANTHROPIC_AUTH_TOKEN, ANTHROPIC_API_KEY or OPENAI_API_KEY in most clients).");
            }

            if (!options.AllowUnauthenticatedRemoteClients && DashboardAccess.IsOtherLocalAccount(context))
            {
                return new GatewayAdmission(
                    null, StatusCodes.Status401Unauthorized, "Gateway key required",
                    "This request comes from another account on this machine, which the router serves only with a gateway key, and none " +
                    "is configured. Set SemanticRouter:Middleware:GatewayKeys and send the key as 'Authorization: Bearer <key>' or " +
                    "'x-api-key: <key>', or set SemanticRouter:Network:TrustOtherLocalAccounts.");
            }

            if (!options.AllowUnauthenticatedRemoteClients)
            {
                return new GatewayAdmission(
                    null, StatusCodes.Status401Unauthorized, "Gateway key required",
                    "This router serves other machines only with a gateway key, and none is configured. Set " +
                    "SemanticRouter:Middleware:GatewayKeys and send the key as 'Authorization: Bearer <key>' or 'x-api-key: <key>', " +
                    "or set Middleware:AllowUnauthenticatedRemoteClients behind a proxy that authenticates callers.");
            }
        }

        return GatewayGuard.For(context).Refusal(context) is { } refusal
            ? new GatewayAdmission(null, StatusCodes.Status403Forbidden, "Forbidden", refusal)
            : new GatewayAdmission(null);
    }
}

/// <summary>Applies the gateway's key and web-page checks to endpoints the middleware does not intercept.</summary>
internal static class GatewayEndpointAccess
{
    /// <summary>Applies <see cref="GatewayAuthentication.Admit"/>, answering a refusal in <paramref name="protocol"/>'s error format.</summary>
    public static RouteHandlerBuilder RequireGatewayAccess(this RouteHandlerBuilder builder, ChatProtocol protocol) =>
        builder.AddEndpointFilter(async (invocation, next) =>
        {
            HttpContext context = invocation.HttpContext;
            RouterRuntime? runtime = context.RequestServices.GetService<RouterRuntimeProvider>()?.Current;
            if (runtime is not null && GatewayAuthentication.Admit(context, runtime) is { Refused: true } refused)
            {
                await ProtocolErrors.WriteAsync(context, protocol, refused.Status, refused.Title!, refused.Detail!, context.RequestAborted)
                    .ConfigureAwait(false);
                return Results.Empty;
            }

            return await next(invocation).ConfigureAwait(false);
        });
}
