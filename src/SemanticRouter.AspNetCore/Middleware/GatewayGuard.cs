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

/// <summary>Applies the gateway's key and web-page checks to endpoints the middleware does not intercept.</summary>
internal static class GatewayEndpointAccess
{
    /// <summary>
    /// Requires a gateway key where the middleware would (remote clients, or every client with
    /// <c>RequireGatewayKeyForLocalClients</c>), and refuses web pages without one (<see cref="GatewayGuard"/>).
    /// </summary>
    public static RouteHandlerBuilder RequireGatewayAccess(this RouteHandlerBuilder builder, ChatProtocol protocol) =>
        builder.AddEndpointFilter(async (invocation, next) =>
        {
            HttpContext context = invocation.HttpContext;
            RouterRuntime? runtime = context.RequestServices.GetService<RouterRuntimeProvider>()?.Current;
            IReadOnlyDictionary<string, string> keys = runtime?.GatewayKeys ?? new Dictionary<string, string>();
            string? client = keys.Count > 0 ? PromptRoutingMiddleware.IdentifyClient(context.Request, keys) : null;
            if (client is null && keys.Count > 0
                && (!DashboardAccess.IsLocal(context) || runtime!.Configuration.Middleware.RequireGatewayKeyForLocalClients))
            {
                await ProtocolErrors.WriteAsync(
                    context, protocol, StatusCodes.Status401Unauthorized, "Gateway key required",
                    "This router requires a gateway key: send it as 'Authorization: Bearer <key>' or 'x-api-key: <key>'.",
                    context.RequestAborted).ConfigureAwait(false);
                return Results.Empty;
            }

            if (client is null && GatewayGuard.For(context).Refusal(context) is { } refusal)
            {
                await ProtocolErrors.WriteAsync(context, protocol, StatusCodes.Status403Forbidden, "Forbidden", refusal, context.RequestAborted)
                    .ConfigureAwait(false);
                return Results.Empty;
            }

            return await next(invocation).ConfigureAwait(false);
        });
}
