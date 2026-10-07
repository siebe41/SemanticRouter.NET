using System.Text.Json.Serialization;

namespace SemanticRouter;

/// <summary>
/// Explains why a <see cref="RouteTarget"/> was selected. Surfaced in telemetry and response headers
/// so routing decisions can be audited and thresholds tuned.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RoutingReason>))]
public enum RoutingReason
{
    /// <summary>The prompt matched a route above its similarity threshold and was sent to that route's target.</summary>
    SemanticMatch = 0,

    /// <summary>No route cleared its similarity threshold; the configured fallback target was used.</summary>
    NoMatchFallback = 1,

    /// <summary>A local route matched, but the prompt's heuristic complexity score required the cloud model.</summary>
    ComplexityEscalation = 2,

    /// <summary>A local route matched, but the estimated prompt size exceeds the local model's context budget.</summary>
    ContextWindowEscalation = 3,

    /// <summary>A local route won by less than the ambiguity margin over a cloud route, so the safer cloud target was chosen.</summary>
    AmbiguousMatchEscalation = 4,

    /// <summary>The prompt was so complex that embedding was skipped entirely and it was sent straight to the cloud.</summary>
    ComplexityShortCircuit = 5,

    /// <summary>The prompt was empty or whitespace; the fallback target was used without any analysis.</summary>
    EmptyPrompt = 6,

    /// <summary>Classification failed (for example the embedding endpoint was unreachable) and the router failed open to the fallback target.</summary>
    ClassifierFailure = 7,
}
