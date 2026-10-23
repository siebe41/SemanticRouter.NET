namespace SemanticRouter;

/// <summary>
/// The output of an <see cref="IRouteSelector"/>: what a model must be able to do to answer the prompt, and why.
/// </summary>
public sealed record RouteDecision
{
    /// <summary>The minimum model tier.</summary>
    public required int RequiredTier { get; init; }

    /// <summary>How <see cref="RequiredTier"/> was decided.</summary>
    public required RoutingReason Reason { get; init; }

    /// <summary>The route the decision is attributed to, or <see langword="null"/> for fallbacks.</summary>
    public RouteDefinition? Route { get; init; }

    /// <summary>The similarity of <see cref="Route"/>, or of the best candidate on a fallback.</summary>
    public float? Similarity { get; init; }

    /// <summary>Capability tags the serving model must declare.</summary>
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];

    /// <summary>Whether only <see cref="ModelDefinition.IsLocal"/> models may serve the prompt.</summary>
    public bool LocalOnly { get; init; }
}
