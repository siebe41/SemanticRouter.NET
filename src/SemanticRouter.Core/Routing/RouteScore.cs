namespace SemanticRouter;

/// <summary>
/// The similarity between a prompt and a single route's intent cluster.
/// </summary>
/// <param name="Route">The scored route.</param>
/// <param name="Similarity">Cosine similarity in the range −1..1 (higher is closer).</param>
/// <remarks>
/// Frozen: a score is a route and a similarity, nothing more. Its constructor and <c>Deconstruct</c> are public API, so
/// it gains no positional members; data that belongs with a score goes in a new type.
/// </remarks>
public readonly record struct RouteScore(RouteDefinition Route, float Similarity);
