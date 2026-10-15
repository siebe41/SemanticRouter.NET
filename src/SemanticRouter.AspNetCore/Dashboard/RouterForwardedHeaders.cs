using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SemanticRouter.AspNetCore;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Forwarded headers from known reverse proxies, noting which requests came through one.</summary>
public static class RouterForwardedHeadersExtensions
{
    /// <summary>
    /// <see cref="ForwardedHeadersExtensions.UseForwardedHeaders(IApplicationBuilder)"/>, after noting whether the
    /// request came straight from one of <see cref="ForwardedHeadersOptions.KnownProxies"/> or
    /// <see cref="ForwardedHeadersOptions.KnownIPNetworks"/> (before <c>X-Forwarded-For</c> replaces the address). The
    /// dashboard believes <see cref="RouterDashboardOptions.TrustedIdentityHeader"/> only on those requests. Configure
    /// <see cref="ForwardedHeadersOptions"/> first: ASP.NET Core's defaults trust loopback.
    /// </summary>
    public static IApplicationBuilder UseRouterForwardedHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        ForwardedHeadersOptions options = app.ApplicationServices.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        IPAddress[] proxies = [.. options.KnownProxies];
        System.Net.IPNetwork[] networks = [.. options.KnownIPNetworks];
        app.Use((context, next) =>
        {
            if (context.Connection.RemoteIpAddress is { } peer && RouterProxyFeature.IsKnown(peer, proxies, networks))
            {
                context.Features.Set(new RouterProxyFeature(peer));
            }

            return next(context);
        });
        return app.UseForwardedHeaders();
    }
}
