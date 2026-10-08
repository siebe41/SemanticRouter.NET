namespace SemanticRouter;

/// <summary>
/// The outcome of classifying a prompt: the requirements a model must meet, plus the evidence behind them.
/// </summary>
public sealed record PromptClassificationResult
{
    /// <summary>The minimum <see cref="ModelDefinition.Tier"/> needed to answer the prompt well.</summary>
    public required int RequiredTier { get; init; }

    /// <summary>How <see cref="RequiredTier"/> was decided.</summary>
    public required RoutingReason Reason { get; init; }

    /// <summary>Capability tags the serving model must declare.</summary>
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];

    /// <summary>
    /// <see langword="true"/> when the attributed route is <see cref="RouteDefinition.LocalOnly"/>: callers must
    /// only use <see cref="ModelDefinition.IsLocal"/> models, including on failover.
    /// </summary>
    public bool LocalOnly { get; init; }

    /// <summary>The route the decision is attributed to, or <see langword="null"/> when no route matched.</summary>
    public string? RouteName { get; init; }

    /// <summary>
    /// Similarity of the attributed route, or of the best candidate when nothing matched.
    /// <see langword="null"/> when no embedding comparison was performed.
    /// </summary>
    public float? Similarity { get; init; }

    /// <summary>The heuristic complexity analysis of the prompt.</summary>
    public required ComplexityAnalysis Complexity { get; init; }

    /// <summary>The highest scoring routes, best first (truncated to <see cref="SemanticRouterOptions.MaxReportedScores"/>).</summary>
    public IReadOnlyList<RouteScore> TopScores { get; init; } = [];

    /// <summary>Wall-clock time spent classifying, including the embedding call.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>The decision was reused from the classification cache: no embedding call was made.</summary>
    public bool Cached { get; init; }

    /// <summary><see langword="true"/> when the result is attributed to a configured route.</summary>
    public bool IsRouteMatch => RouteName is not null;

    /// <summary>Creates a result that bypassed semantic matching (empty prompt, short-circuit, failure, override).</summary>
    public static PromptClassificationResult Bypass(
        int requiredTier,
        RoutingReason reason,
        ComplexityAnalysis? complexity = null,
        TimeSpan elapsed = default) => new()
        {
            RequiredTier = requiredTier,
            Reason = reason,
            Complexity = complexity ?? ComplexityAnalysis.Empty,
            Elapsed = elapsed,
        };
}
