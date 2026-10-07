namespace SemanticRouter;

/// <summary>
/// The default routing policy.
/// </summary>
/// <remarks>
/// Rules are applied in order:
/// <list type="number">
///   <item>If the best route is below its similarity threshold, use <see cref="SemanticRouterOptions.FallbackTarget"/>
///         (still subject to the escalation rules in 3 and 4 when the fallback is local).</item>
///   <item>A matched cloud route, or a local route with <see cref="RouteDefinition.AllowEscalation"/> disabled, is final.</item>
///   <item>A local match whose estimated tokens exceed <see cref="SemanticRouterOptions.LocalMaxPromptTokens"/> escalates to the cloud.</item>
///   <item>A local match whose complexity score reaches <see cref="SemanticRouterOptions.ComplexityEscalationThreshold"/> escalates to the cloud.</item>
///   <item>A local match that beats a matching cloud route by less than <see cref="SemanticRouterOptions.AmbiguityMargin"/> escalates to that cloud route.</item>
///   <item>Otherwise the local route wins.</item>
/// </list>
/// Escalation is one-way: the selector never downgrades a cloud decision to local.
/// </remarks>
public sealed class DefaultRouteSelector : IRouteSelector
{
    private readonly SemanticRouterOptions _options;

    /// <summary>Creates a selector using the thresholds in <paramref name="options"/>.</summary>
    public DefaultRouteSelector(SemanticRouterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <inheritdoc />
    public RouteDecision SelectRoute(IReadOnlyList<RouteScore> rankedScores, ComplexityAnalysis complexity)
    {
        ArgumentNullException.ThrowIfNull(rankedScores);
        ArgumentNullException.ThrowIfNull(complexity);

        if (rankedScores.Count == 0 || rankedScores[0].Similarity < _options.GetThreshold(rankedScores[0].Route))
        {
            float? bestSimilarity = rankedScores.Count > 0 ? rankedScores[0].Similarity : null;
            RouteTarget fallback = _options.FallbackTarget;
            if (fallback == RouteTarget.Local && TryEscalate(complexity) is { } fallbackEscalation)
            {
                return new RouteDecision(RouteTarget.Cloud, fallbackEscalation, Similarity: bestSimilarity);
            }

            return new RouteDecision(fallback, RoutingReason.NoMatchFallback, Similarity: bestSimilarity);
        }

        RouteScore best = rankedScores[0];
        if (best.Route.Target == RouteTarget.Cloud || !best.Route.AllowEscalation)
        {
            return Match(best);
        }

        if (TryEscalate(complexity) is { } escalation)
        {
            return new RouteDecision(RouteTarget.Cloud, escalation, best.Route, best.Similarity);
        }

        if (_options.AmbiguityMargin > 0f)
        {
            for (int i = 1; i < rankedScores.Count; i++)
            {
                RouteScore candidate = rankedScores[i];
                if (best.Similarity - candidate.Similarity >= _options.AmbiguityMargin)
                {
                    break;
                }

                if (candidate.Route.Target == RouteTarget.Cloud
                    && candidate.Similarity >= _options.GetThreshold(candidate.Route))
                {
                    return new RouteDecision(
                        RouteTarget.Cloud, RoutingReason.AmbiguousMatchEscalation, candidate.Route, candidate.Similarity);
                }
            }
        }

        return Match(best);
    }

    private static RouteDecision Match(RouteScore score) =>
        new(score.Route.Target, RoutingReason.SemanticMatch, score.Route, score.Similarity);

    private RoutingReason? TryEscalate(ComplexityAnalysis complexity)
    {
        if (_options.LocalMaxPromptTokens > 0 && complexity.EstimatedTokens > _options.LocalMaxPromptTokens)
        {
            return RoutingReason.ContextWindowEscalation;
        }

        if (complexity.Score >= _options.ComplexityEscalationThreshold)
        {
            return RoutingReason.ComplexityEscalation;
        }

        return null;
    }
}
