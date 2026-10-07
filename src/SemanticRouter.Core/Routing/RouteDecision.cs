namespace SemanticRouter;

/// <summary>
/// The output of an <see cref="IRouteSelector"/>: where to send a prompt and why.
/// </summary>
/// <param name="Target">The selected endpoint class.</param>
/// <param name="Reason">Why <paramref name="Target"/> was chosen.</param>
/// <param name="Route">The route the decision is attributed to, or <see langword="null"/> for fallbacks.</param>
/// <param name="Similarity">The similarity of <paramref name="Route"/>, when there is one.</param>
public sealed record RouteDecision(
    RouteTarget Target,
    RoutingReason Reason,
    RouteDefinition? Route = null,
    float? Similarity = null);
