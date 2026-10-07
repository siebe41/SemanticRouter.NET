namespace SemanticRouter;

/// <summary>
/// Turns semantic similarity scores and complexity signals into a routing decision.
/// Implementations must be thread-safe and should be pure (no I/O): all expensive work happens
/// before the selector is called.
/// </summary>
public interface IRouteSelector
{
    /// <summary>Selects a target for a prompt.</summary>
    /// <param name="rankedScores">Per-route similarity, sorted best first. May be empty when no routes are configured.</param>
    /// <param name="complexity">Heuristic complexity analysis of the prompt.</param>
    RouteDecision SelectRoute(IReadOnlyList<RouteScore> rankedScores, ComplexityAnalysis complexity);
}
