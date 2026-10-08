using System.Diagnostics;

namespace SemanticRouter;

/// <summary>
/// Tracing for the router. Subscribe with OpenTelemetry via <c>tracing.AddSource(RouterTelemetry.ActivitySourceName)</c>.
/// Spans carry decisions (tier, route, model, outcome), never prompt text.
/// </summary>
public static class RouterTelemetry
{
    /// <summary>The <see cref="ActivitySource"/> name for every router span.</summary>
    public const string ActivitySourceName = "SemanticRouter";

    /// <summary>The source the router's spans come from.</summary>
    public static ActivitySource Source { get; } = new(ActivitySourceName, typeof(RouterTelemetry).Assembly.GetName().Version?.ToString());
}
