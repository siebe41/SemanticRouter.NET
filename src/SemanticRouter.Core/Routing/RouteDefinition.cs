namespace SemanticRouter;

/// <summary>
/// A semantic intent cluster: a named set of example utterances that, when matched, sends a prompt
/// to a specific <see cref="RouteTarget"/>.
/// </summary>
/// <remarks>
/// The type is a mutable POCO so it can be bound directly from <c>appsettings.json</c>.
/// Treat instances as immutable once they have been handed to a classifier.
/// </remarks>
public sealed class RouteDefinition
{
    /// <summary>Unique, human-readable route name (emitted in headers and telemetry).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Where prompts matching this route are sent.</summary>
    public RouteTarget Target { get; set; } = RouteTarget.Local;

    /// <summary>
    /// Representative example prompts for this intent. More diverse examples produce a more robust cluster;
    /// 5–20 short utterances per route is a good starting point.
    /// </summary>
    public IList<string> Utterances { get; } = new List<string>();

    /// <summary>
    /// Minimum cosine similarity (−1..1) required to match this route. When <see langword="null"/>,
    /// <see cref="SemanticRouterOptions.DefaultSimilarityThreshold"/> is used.
    /// </summary>
    public float? SimilarityThreshold { get; set; }

    /// <summary>
    /// When <see langword="false"/>, a match on this route is never escalated to the cloud by complexity,
    /// context-window or ambiguity rules. Use this to pin data-sensitive intents (PII, internal documents)
    /// to the local model. Defaults to <see langword="true"/>.
    /// </summary>
    public bool AllowEscalation { get; set; } = true;

    /// <summary>Optional free-form description for documentation and diagnostics.</summary>
    public string? Description { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"{Name} -> {Target}";
}
