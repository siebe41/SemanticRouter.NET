using System.Text.Json.Serialization;

namespace SemanticRouter;

/// <summary>
/// How a route's utterance embeddings are aggregated into a single route score.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RouteScoringStrategy>))]
public enum RouteScoringStrategy
{
    /// <summary>
    /// Route score is the best similarity to any single utterance (nearest neighbour). Robust for
    /// heterogeneous intents; cost grows with the number of utterances.
    /// </summary>
    MaxSimilarity = 0,

    /// <summary>
    /// Route score is the similarity to the normalized mean of its utterances. One comparison per route;
    /// best for tight, homogeneous clusters.
    /// </summary>
    Centroid = 1,
}

/// <summary>
/// Configuration for semantic prompt routing. Bindable from the <c>SemanticRouter</c> configuration section.
/// </summary>
public sealed class SemanticRouterOptions
{
    /// <summary>The conventional configuration section name.</summary>
    public const string SectionName = "SemanticRouter";

    /// <summary>The semantic intent clusters to route between.</summary>
    public IList<RouteDefinition> Routes { get; } = new List<RouteDefinition>();

    /// <summary>
    /// Cosine similarity a prompt must reach to match a route that does not set its own threshold.
    /// The right value depends heavily on the embedding model (e.g. ~0.75–0.85 for OpenAI
    /// <c>text-embedding-3-*</c>, ~0.6–0.7 for <c>nomic-embed-text</c>). Default 0.75.
    /// </summary>
    public float DefaultSimilarityThreshold { get; set; } = 0.75f;

    /// <summary>Where prompts go when no route matches. Default <see cref="RouteTarget.Cloud"/> (quality-safe).</summary>
    public RouteTarget FallbackTarget { get; set; } = RouteTarget.Cloud;

    /// <summary>
    /// Complexity score (0..1) at or above which a prompt bound for the local model is escalated to the
    /// cloud. Set above 1 to disable. Default 0.65.
    /// </summary>
    public double ComplexityEscalationThreshold { get; set; } = 0.65;

    /// <summary>
    /// Complexity score at or above which the embedding call is skipped and the prompt is sent straight to
    /// the cloud. Saves an embedding round-trip for obviously hard prompts. Ignored while any route has
    /// <see cref="RouteDefinition.AllowEscalation"/> disabled, because a pinned route must always get the
    /// chance to match. Set above 1 to disable. Default 0.9.
    /// </summary>
    public double ComplexityShortCircuitThreshold { get; set; } = 0.9;

    /// <summary>
    /// Estimated prompt tokens above which a local-bound prompt is escalated because it would not fit the
    /// local model's context window. 0 disables. Default 4096.
    /// </summary>
    public int LocalMaxPromptTokens { get; set; } = 4096;

    /// <summary>
    /// When a local route wins by less than this similarity margin over a matching cloud route, the cloud
    /// route is chosen instead. 0 disables. Default 0.02.
    /// </summary>
    public float AmbiguityMargin { get; set; } = 0.02f;

    /// <summary>How utterance similarities are aggregated per route. Default <see cref="RouteScoringStrategy.MaxSimilarity"/>.</summary>
    public RouteScoringStrategy ScoringStrategy { get; set; } = RouteScoringStrategy.MaxSimilarity;

    /// <summary>How many of the best route scores to include in <see cref="PromptClassificationResult.TopScores"/>. Default 3.</summary>
    public int MaxReportedScores { get; set; } = 3;

    /// <summary>Complexity heuristic tuning.</summary>
    public ComplexityHeuristicsOptions Complexity { get; set; } = new();

    /// <summary>Returns human-readable validation errors; empty when the options are valid.</summary>
    public IReadOnlyList<string> GetValidationErrors()
    {
        var errors = new List<string>();

        if (DefaultSimilarityThreshold is < -1f or > 1f || float.IsNaN(DefaultSimilarityThreshold))
        {
            errors.Add($"{nameof(DefaultSimilarityThreshold)} must be between -1 and 1.");
        }

        if (AmbiguityMargin is < 0f or > 2f || float.IsNaN(AmbiguityMargin))
        {
            errors.Add($"{nameof(AmbiguityMargin)} must be between 0 and 2.");
        }

        if (double.IsNaN(ComplexityEscalationThreshold) || double.IsNaN(ComplexityShortCircuitThreshold))
        {
            errors.Add("Complexity thresholds must be numbers.");
        }

        if (LocalMaxPromptTokens < 0)
        {
            errors.Add($"{nameof(LocalMaxPromptTokens)} must not be negative.");
        }

        if (MaxReportedScores < 0)
        {
            errors.Add($"{nameof(MaxReportedScores)} must not be negative.");
        }

        if (Complexity is null)
        {
            errors.Add($"{nameof(Complexity)} must not be null.");
        }
        else
        {
            errors.AddRange(Complexity.GetValidationErrors());
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Routes.Count; i++)
        {
            RouteDefinition route = Routes[i];
            if (string.IsNullOrWhiteSpace(route.Name))
            {
                errors.Add($"Routes[{i}] must have a {nameof(RouteDefinition.Name)}.");
            }
            else if (!names.Add(route.Name))
            {
                errors.Add($"Route name '{route.Name}' is used more than once.");
            }

            if (!route.Utterances.Any(u => !string.IsNullOrWhiteSpace(u)))
            {
                errors.Add($"Route '{route.Name}' must have at least one non-empty utterance.");
            }

            if (route.SimilarityThreshold is { } threshold && (threshold is < -1f or > 1f || float.IsNaN(threshold)))
            {
                errors.Add($"Route '{route.Name}' {nameof(RouteDefinition.SimilarityThreshold)} must be between -1 and 1.");
            }

            if (!Enum.IsDefined(route.Target))
            {
                errors.Add($"Route '{route.Name}' has an unknown {nameof(RouteDefinition.Target)} '{route.Target}'.");
            }
        }

        return errors;
    }

    /// <summary>Throws when <see cref="GetValidationErrors"/> reports any problem.</summary>
    /// <exception cref="InvalidOperationException">The options are invalid.</exception>
    public void Validate()
    {
        IReadOnlyList<string> errors = GetValidationErrors();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid SemanticRouter configuration:" + Environment.NewLine + " - " +
                string.Join(Environment.NewLine + " - ", errors));
        }
    }

    internal float GetThreshold(RouteDefinition route) => route.SimilarityThreshold ?? DefaultSimilarityThreshold;
}
