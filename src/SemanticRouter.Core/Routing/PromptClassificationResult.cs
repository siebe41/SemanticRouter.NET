namespace SemanticRouter;

/// <summary>
/// The full outcome of classifying a prompt: the routing decision plus the evidence behind it.
/// </summary>
public sealed record PromptClassificationResult
{
    /// <summary>Where the prompt should be sent.</summary>
    public required RouteTarget Target { get; init; }

    /// <summary>Why <see cref="Target"/> was chosen.</summary>
    public required RoutingReason Reason { get; init; }

    /// <summary>The route the decision is attributed to, or <see langword="null"/> when no route matched.</summary>
    public string? RouteName { get; init; }

    /// <summary>
    /// Similarity of the attributed route, or of the best candidate when nothing matched.
    /// <see langword="null"/> when no embedding comparison was performed.
    /// </summary>
    public float? Similarity { get; init; }

    /// <summary>
    /// <see langword="true"/> when the attributed route has <see cref="RouteDefinition.AllowEscalation"/> disabled.
    /// Callers must not send the prompt to any other target (including on failover).
    /// </summary>
    public bool IsPinned { get; init; }

    /// <summary>The heuristic complexity analysis of the prompt.</summary>
    public required ComplexityAnalysis Complexity { get; init; }

    /// <summary>The highest scoring routes, best first (truncated to <see cref="SemanticRouterOptions.MaxReportedScores"/>).</summary>
    public IReadOnlyList<RouteScore> TopScores { get; init; } = [];

    /// <summary>Wall-clock time spent classifying, including the embedding call.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary><see langword="true"/> when the result is attributed to a configured route.</summary>
    public bool IsRouteMatch => RouteName is not null;

    /// <summary>Creates a result that bypassed semantic matching (empty prompt, short-circuit, failure).</summary>
    public static PromptClassificationResult Bypass(
        RouteTarget target,
        RoutingReason reason,
        ComplexityAnalysis? complexity = null,
        TimeSpan elapsed = default) => new()
        {
            Target = target,
            Reason = reason,
            Complexity = complexity ?? ComplexityAnalysis.Empty,
            Elapsed = elapsed,
        };
}
