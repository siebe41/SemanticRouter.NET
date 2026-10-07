namespace SemanticRouter;

/// <summary>
/// The output of an <see cref="IRouteSelector"/>: what a model must be able to do to answer the prompt, and why.
/// </summary>
/// <param name="RequiredTier">The minimum model tier.</param>
/// <param name="Reason">How <paramref name="RequiredTier"/> was decided.</param>
/// <param name="Route">The route the decision is attributed to, or <see langword="null"/> for fallbacks.</param>
/// <param name="Similarity">The similarity of <paramref name="Route"/>, or of the best candidate on a fallback.</param>
public sealed record RouteDecision(
    int RequiredTier,
    RoutingReason Reason,
    RouteDefinition? Route = null,
    float? Similarity = null)
{
    /// <summary>Capability tags the serving model must declare.</summary>
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];

    /// <summary>Whether only <see cref="ModelDefinition.IsLocal"/> models may serve the prompt.</summary>
    public bool LocalOnly { get; init; }
}
