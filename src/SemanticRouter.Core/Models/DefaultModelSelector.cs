namespace SemanticRouter;

/// <summary>
/// The default <see cref="IModelSelector"/>.
/// </summary>
/// <remarks>
/// A model is <em>eligible</em> when it is enabled, available, declares every required capability, is local if the
/// request is local-only, and its context window fits the estimated input plus output. Eligible models that meet the
/// required tier are ordered by the <see cref="SelectionStrategy"/>; when
/// <see cref="ModelSelectionOptions.AllowBelowRequiredTier"/> is set, the remaining eligible models follow, most
/// capable first, so a request degrades gracefully instead of failing.
/// </remarks>
public sealed class DefaultModelSelector : IModelSelector
{
    private readonly ModelSelectionOptions _options;

    /// <summary>Creates a selector.</summary>
    public DefaultModelSelector(ModelSelectionOptions? options = null)
    {
        _options = options ?? new ModelSelectionOptions();
    }

    /// <inheritdoc />
    public IReadOnlyList<ModelCandidate> RankCandidates(
        ModelRequirements requirements,
        IReadOnlyCollection<ModelDefinition> models,
        Func<ModelDefinition, bool>? isAvailable = null)
    {
        ArgumentNullException.ThrowIfNull(requirements);
        ArgumentNullException.ThrowIfNull(models);

        int outputTokens = _options.EstimatedOutputTokens;
        var meeting = new List<ModelCandidate>();
        var below = new List<ModelCandidate>();

        foreach (ModelDefinition model in models)
        {
            if (!model.Enabled
                || (requirements.LocalOnly && !model.IsLocal)
                || !model.HasCapabilities(requirements.RequiredCapabilities)
                || (model.ContextWindow > 0 && model.ContextWindow < requirements.EstimatedInputTokens + outputTokens)
                || (isAvailable is not null && !isAvailable(model)))
            {
                continue;
            }

            bool meets = model.Tier >= requirements.RequiredTier;
            var candidate = new ModelCandidate(model, model.EstimateCost(requirements.EstimatedInputTokens, outputTokens), meets);
            (meets ? meeting : below).Add(candidate);
        }

        IEnumerable<ModelCandidate> ordered = (requirements.Strategy ?? _options.Strategy) switch
        {
            SelectionStrategy.MostCapable => meeting
                .OrderByDescending(c => c.Model.Tier).ThenBy(c => c.EstimatedCost).ThenBy(c => c.Model.Priority),
            SelectionStrategy.Priority => meeting
                .OrderBy(c => c.Model.Priority).ThenBy(c => c.EstimatedCost).ThenBy(c => c.Model.Tier),
            _ => meeting
                .OrderBy(c => c.EstimatedCost).ThenBy(c => c.Model.Tier).ThenBy(c => c.Model.Priority),
        };

        var result = ordered.ToList();
        if (_options.AllowBelowRequiredTier)
        {
            result.AddRange(below
                .OrderByDescending(c => c.Model.Tier).ThenBy(c => c.EstimatedCost).ThenBy(c => c.Model.Priority));
        }

        return result;
    }
}
