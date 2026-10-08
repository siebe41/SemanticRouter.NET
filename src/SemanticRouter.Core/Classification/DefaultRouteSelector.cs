namespace SemanticRouter;

/// <summary>
/// The default routing policy: decides the minimum model tier and capabilities for a prompt.
/// </summary>
/// <remarks>
/// <list type="number">
///   <item>If the best route is below its similarity threshold, <see cref="SemanticRouterOptions.FallbackTier"/> applies.</item>
///   <item>Otherwise the best route's tier, capabilities and <see cref="RouteDefinition.LocalOnly"/> flag apply.</item>
///   <item>Other routes within <see cref="SemanticRouterOptions.AmbiguityMargin"/> of the best (and above their own
///         thresholds) contribute too: the highest tier wins, capabilities are unioned, and local-only is sticky.</item>
///   <item><see cref="SemanticRouterOptions.ComplexityTiers"/> can raise the tier further.</item>
/// </list>
/// Requirements only ever go up: neither ambiguity nor complexity can lower a route's tier.
/// </remarks>
public sealed class DefaultRouteSelector : IRouteSelector
{
    private readonly SemanticRouterOptions _options;
    private readonly Func<RouteDefinition, float> _threshold;

    /// <summary>Creates a selector using the thresholds in <paramref name="options"/>.</summary>
    public DefaultRouteSelector(SemanticRouterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _threshold = options.GetThreshold;
    }

    /// <summary>Creates a selector whose similarity thresholds come from <paramref name="threshold"/> (calibration sweeps).</summary>
    internal DefaultRouteSelector(SemanticRouterOptions options, Func<RouteDefinition, float> threshold)
    {
        _options = options;
        _threshold = threshold;
    }

    /// <inheritdoc />
    public RouteDecision SelectRoute(IReadOnlyList<RouteScore> rankedScores, ComplexityAnalysis complexity)
    {
        ArgumentNullException.ThrowIfNull(rankedScores);
        ArgumentNullException.ThrowIfNull(complexity);

        int complexityTier = _options.TierForComplexity(complexity.Score);

        if (rankedScores.Count == 0 || rankedScores[0].Similarity < _threshold(rankedScores[0].Route))
        {
            float? bestSimilarity = rankedScores.Count > 0 ? rankedScores[0].Similarity : null;
            return complexityTier > _options.FallbackTier
                ? new RouteDecision(complexityTier, RoutingReason.ComplexityEscalation, Similarity: bestSimilarity)
                : new RouteDecision(_options.FallbackTier, RoutingReason.NoMatchFallback, Similarity: bestSimilarity);
        }

        RouteScore best = rankedScores[0];
        int tier = best.Route.Tier;
        bool localOnly = best.Route.LocalOnly;
        var capabilities = new HashSet<string>(best.Route.RequiredCapabilities, StringComparer.OrdinalIgnoreCase);

        if (_options.AmbiguityMargin > 0f)
        {
            for (int i = 1; i < rankedScores.Count; i++)
            {
                RouteScore candidate = rankedScores[i];
                if (best.Similarity - candidate.Similarity >= _options.AmbiguityMargin)
                {
                    break;
                }

                if (candidate.Similarity >= _threshold(candidate.Route))
                {
                    tier = Math.Max(tier, candidate.Route.Tier);
                    localOnly |= candidate.Route.LocalOnly;
                    capabilities.UnionWith(candidate.Route.RequiredCapabilities);
                }
            }
        }

        RoutingReason reason = tier > best.Route.Tier ? RoutingReason.AmbiguousMatchEscalation : RoutingReason.SemanticMatch;
        if (complexityTier > tier)
        {
            tier = complexityTier;
            reason = RoutingReason.ComplexityEscalation;
        }

        return new RouteDecision(tier, reason, best.Route, best.Similarity)
        {
            RequiredCapabilities = capabilities.ToArray(),
            LocalOnly = localOnly,
        };
    }
}
