using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SemanticRouter.AspNetCore;

/// <summary>Model reliability per kind of work: how each model did per route and with tools, and which are left out.</summary>
internal static class ReliabilityApi
{
    public static void Map(RouteGroupBuilder api, JsonSerializerOptions json)
    {
        api.MapGet("/reliability", (ModelReliabilityTracker tracker, RouterRuntimeProvider provider) =>
        {
            ReliabilityOptions options = provider.Current.Configuration.Middleware.Reliability;
            IReadOnlyList<ModelReliability> models = tracker.Refresh();
            return Results.Json(new ReliabilityResponse(options.Enabled, options.Window, options.MinimumTurns, [.. models.OrderBy(m => m.Model).ThenBy(m => m.Route ?? m.Capability)]), json);
        });

        api.MapPost("/reliability/reset", (HttpContext context, ModelReliabilityTracker tracker, ResetRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.Model))
            {
                return Results.Problem("'model' is required.", statusCode: StatusCodes.Status400BadRequest);
            }

            tracker.Reset(request.Model, request.Route, request.Capability);
            DashboardAudit.Detail(context, request.Model + (request.Route is { } route ? $" for route {route}" : request.Capability is { } capability ? $" for {capability}" : string.Empty));
            return Results.Json(tracker.Current.Where(m => string.Equals(m.Model, request.Model, StringComparison.OrdinalIgnoreCase)).ToArray(), json);
        }).WithMetadata(DashboardWriteMetadata.Instance).Audited("offered a model again");
    }
}

/// <summary>The models' reliability per kind of work.</summary>
/// <param name="Enabled">Whether unreliable models are left out (<c>Middleware:Reliability:Enabled</c>).</param>
/// <param name="Window">How far back turns count.</param>
/// <param name="MinimumTurns">Turns a model needs on a kind of work before it is judged.</param>
/// <param name="Models">One row per model and route or capability.</param>
internal sealed record ReliabilityResponse(bool Enabled, TimeSpan Window, int MinimumTurns, IReadOnlyList<ModelReliability> Models);

/// <summary>Offer a model again: for one route, one capability, or everything when neither is given.</summary>
internal sealed record ResetRequest(string Model, string? Route, string? Capability);
