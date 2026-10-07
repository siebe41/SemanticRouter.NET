using System.Text.Json.Serialization;

namespace SemanticRouter;

/// <summary>
/// Explains how the required capability tier for a prompt was decided. Surfaced in telemetry and
/// response headers so routing decisions can be audited and thresholds tuned.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RoutingReason>))]
public enum RoutingReason
{
    /// <summary>The prompt matched a route above its similarity threshold; the route's tier applies.</summary>
    SemanticMatch = 0,

    /// <summary>No route cleared its similarity threshold; <see cref="SemanticRouterOptions.FallbackTier"/> applies.</summary>
    NoMatchFallback = 1,

    /// <summary>The prompt's heuristic complexity raised the required tier above the matched route's tier.</summary>
    ComplexityEscalation = 2,

    /// <summary>Two routes scored within the ambiguity margin; the higher of their tiers applies.</summary>
    AmbiguousMatchEscalation = 3,

    /// <summary>The prompt was so complex that embedding was skipped and the tier came from complexity alone.</summary>
    ComplexityShortCircuit = 4,

    /// <summary>The prompt was empty or whitespace; the fallback tier was used without any analysis.</summary>
    EmptyPrompt = 5,

    /// <summary>Classification failed (for example the embedding endpoint was unreachable) and the router failed open to the fallback tier.</summary>
    ClassifierFailure = 6,

    /// <summary>The caller asked for a specific catalog model, so classification was skipped.</summary>
    ModelOverride = 7,
}
