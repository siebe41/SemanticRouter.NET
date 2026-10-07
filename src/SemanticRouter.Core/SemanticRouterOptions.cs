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

/// <summary>Raises the required tier once a prompt's complexity score reaches <see cref="MinScore"/>.</summary>
public sealed class ComplexityTierRule
{
    /// <summary>Complexity score (0..1) at which the rule applies.</summary>
    public double MinScore { get; set; }

    /// <summary>The minimum tier required at that complexity.</summary>
    public int Tier { get; set; }
}

/// <summary>
/// Configuration for semantic prompt routing. Bindable from the <c>SemanticRouter</c> configuration section.
/// </summary>
public sealed class SemanticRouterOptions
{
    /// <summary>The conventional configuration section name.</summary>
    public const string SectionName = "SemanticRouter";

    /// <summary>Default complexity → tier rules used when <see cref="ComplexityTiers"/> is not configured.</summary>
    public static IReadOnlyList<ComplexityTierRule> DefaultComplexityTiers { get; } =
    [
        new() { MinScore = 0.45, Tier = 2 },
        new() { MinScore = 0.65, Tier = 3 },
        new() { MinScore = 0.85, Tier = 4 },
    ];

    /// <summary>The semantic intent clusters that decide how capable a model must be.</summary>
    public IList<RouteDefinition> Routes { get; } = new List<RouteDefinition>();

    /// <summary>The models the router may dispatch to.</summary>
    public IList<ModelDefinition> Models { get; } = new List<ModelDefinition>();

    /// <summary>
    /// Cosine similarity a prompt must reach to match a route that does not set its own threshold.
    /// The right value depends heavily on the embedding model. Default 0.75.
    /// </summary>
    public float DefaultSimilarityThreshold { get; set; } = 0.75f;

    /// <summary>Tier required when no route matches (or classification fails). Default 3, a strong general model.</summary>
    public int FallbackTier { get; set; } = 3;

    /// <summary>
    /// Complexity → minimum tier rules: the highest rule whose <see cref="ComplexityTierRule.MinScore"/> the prompt
    /// reaches raises the required tier. When <see langword="null"/>, <see cref="DefaultComplexityTiers"/> apply
    /// (0.45 → 2, 0.65 → 3, 0.85 → 4). Configure an empty list to disable complexity escalation.
    /// </summary>
#pragma warning disable CA2227 // Settable so configuration binding replaces (rather than appends to) the defaults.
    public IList<ComplexityTierRule>? ComplexityTiers { get; set; }
#pragma warning restore CA2227

    /// <summary>
    /// Complexity score at or above which the embedding call is skipped and the tier comes from
    /// <see cref="ComplexityTiers"/> alone. Ignored while any route is <see cref="RouteDefinition.LocalOnly"/>, because
    /// such routes must always get the chance to match. Set above 1 to disable. Default 0.9.
    /// </summary>
    public double ComplexityShortCircuitThreshold { get; set; } = 0.9;

    /// <summary>
    /// When other routes score within this margin of the best route (and above their own thresholds), the highest of
    /// their tiers applies. 0 disables. Default 0.02.
    /// </summary>
    public float AmbiguityMargin { get; set; } = 0.02f;

    /// <summary>How utterance similarities are aggregated per route. Default <see cref="RouteScoringStrategy.MaxSimilarity"/>.</summary>
    public RouteScoringStrategy ScoringStrategy { get; set; } = RouteScoringStrategy.MaxSimilarity;

    /// <summary>How many of the best route scores to include in <see cref="PromptClassificationResult.TopScores"/>. Default 3.</summary>
    public int MaxReportedScores { get; set; } = 3;

    /// <summary>Complexity heuristic tuning.</summary>
    public ComplexityHeuristicsOptions Complexity { get; set; } = new();

    /// <summary>Model selection and failover settings.</summary>
    public ModelSelectionOptions Selection { get; set; } = new();

    /// <summary>The tier implied by a complexity score alone (0 when no rule applies).</summary>
    public int TierForComplexity(double score)
    {
        int tier = 0;
        foreach (ComplexityTierRule rule in (IEnumerable<ComplexityTierRule>?)ComplexityTiers ?? DefaultComplexityTiers)
        {
            if (score >= rule.MinScore && rule.Tier > tier)
            {
                tier = rule.Tier;
            }
        }

        return tier;
    }

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

        if (FallbackTier < 0)
        {
            errors.Add($"{nameof(FallbackTier)} must not be negative.");
        }

        if (double.IsNaN(ComplexityShortCircuitThreshold))
        {
            errors.Add($"{nameof(ComplexityShortCircuitThreshold)} must be a number.");
        }

        if (MaxReportedScores < 0)
        {
            errors.Add($"{nameof(MaxReportedScores)} must not be negative.");
        }

        foreach (ComplexityTierRule rule in ComplexityTiers ?? [])
        {
            if (rule.MinScore is < 0 or > 1 || double.IsNaN(rule.MinScore) || rule.Tier < 0)
            {
                errors.Add($"{nameof(ComplexityTiers)} entries need a MinScore between 0 and 1 and a non-negative Tier.");
            }
        }

        if (Complexity is null)
        {
            errors.Add($"{nameof(Complexity)} must not be null.");
        }
        else
        {
            errors.AddRange(Complexity.GetValidationErrors());
        }

        if (Selection is null)
        {
            errors.Add($"{nameof(Selection)} must not be null.");
        }
        else
        {
            errors.AddRange(Selection.GetValidationErrors());
        }

        ValidateRoutes(errors);
        ValidateModels(errors);
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

    private void ValidateRoutes(List<string> errors)
    {
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

            if (route.Tier < 0)
            {
                errors.Add($"Route '{route.Name}' {nameof(RouteDefinition.Tier)} must not be negative.");
            }
        }
    }

    private void ValidateModels(List<string> errors)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Models.Count; i++)
        {
            ModelDefinition model = Models[i];
            if (string.IsNullOrWhiteSpace(model.Name))
            {
                errors.Add($"Models[{i}] must have a {nameof(ModelDefinition.Name)}.");
            }
            else if (!names.Add(model.Name))
            {
                errors.Add($"Model name '{model.Name}' is used more than once.");
            }

            if (string.IsNullOrWhiteSpace(model.Connector))
            {
                errors.Add($"Model '{model.Name}' must name a {nameof(ModelDefinition.Connector)}.");
            }

            if (model.Tier < 0 || model.ContextWindow < 0 || model.InputCostPerMillionTokens < 0 || model.OutputCostPerMillionTokens < 0)
            {
                errors.Add($"Model '{model.Name}' must not have a negative tier, context window or cost.");
            }
        }

        if (Models.Count > 0
            && Routes.Any(r => r.LocalOnly)
            && !Models.Any(m => m.Enabled && m.IsLocal))
        {
            errors.Add("A route is LocalOnly but no enabled model is marked IsLocal, so it could never be served.");
        }
    }
}
