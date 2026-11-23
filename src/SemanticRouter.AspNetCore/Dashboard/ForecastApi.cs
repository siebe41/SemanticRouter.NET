using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// The quota forecast on the admin API (#374): when each subscription and budget runs out at the current pace, against
/// its next reset, and which routes to shift. Figures, route and client names only (as <c>GET api/usage</c>), so the
/// metrics role may read it. It reads and changes nothing.
/// </summary>
internal static class ForecastApi
{
    public static void Map(RouteGroupBuilder api, JsonSerializerOptions json) =>
        api.MapGet("/forecast", (RouterRuntimeProvider provider, QuotaForecaster forecaster) =>
            Results.Json(forecaster.Forecast(provider.Current.Configuration), json))
            .WithMetadata(DashboardMetricsMetadata.Instance);
}
