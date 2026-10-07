namespace SemanticRouter;

/// <summary>
/// The similarity between a prompt and a single route's intent cluster.
/// </summary>
/// <param name="Route">The scored route.</param>
/// <param name="Similarity">Cosine similarity in the range −1..1 (higher is closer).</param>
public readonly record struct RouteScore(RouteDefinition Route, float Similarity);
