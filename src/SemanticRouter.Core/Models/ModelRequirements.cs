namespace SemanticRouter;

/// <summary>What a model must satisfy to serve a request.</summary>
public sealed record ModelRequirements
{
    /// <summary>Minimum model tier.</summary>
    public int RequiredTier { get; init; } = 1;

    /// <summary>Capability tags the model must declare.</summary>
    public IReadOnlyList<string> RequiredCapabilities { get; init; } = [];

    /// <summary>Only <see cref="ModelDefinition.IsLocal"/> models qualify.</summary>
    public bool LocalOnly { get; init; }

    /// <summary>Estimated input size of the whole request (all messages and tools), for context-window checks and cost.</summary>
    public int EstimatedInputTokens { get; init; }

    /// <summary>Overrides <see cref="ModelSelectionOptions.Strategy"/> for this request.</summary>
    public SelectionStrategy? Strategy { get; init; }

    /// <summary>The <see cref="SemanticRouterOptions.Profiles"/> entry the request was routed under, if any.</summary>
    public string? Profile { get; init; }
}

/// <summary>A model eligible to serve a request, in the order it should be tried.</summary>
/// <param name="Model">The catalog entry.</param>
/// <param name="EstimatedCost">Estimated marginal cost of the request in USD.</param>
/// <param name="MeetsRequiredTier"><see langword="false"/> for below-tier fallbacks.</param>
public sealed record ModelCandidate(ModelDefinition Model, decimal EstimatedCost, bool MeetsRequiredTier);
