namespace SemanticRouter;

/// <summary>
/// The default <see cref="IModelSelector"/>.
/// </summary>
/// <remarks>
/// A model is <em>eligible</em> when it is enabled, available, declares every required capability, is local if the
/// request is local-only, and its context window fits the estimated input plus output. Eligible models that meet the
/// required tier are ordered by the <see cref="SelectionStrategy"/>; when
/// <see cref="ModelSelectionOptions.AllowBelowRequiredTier"/> is set, the remaining eligible models follow, most
/// capable first, so a request degrades gracefully instead of failing. Cost is estimated with the share of input each
/// model reads from and writes to its prompt cache on the request's route, when <see cref="IModelCacheShare"/> knows it.
/// </remarks>
public sealed class DefaultModelSelector : IModelSelector
{
    private readonly ModelSelectionOptions _options;
    private readonly IModelLatency? _latency;
    private readonly IModelCacheShare? _cache;

    /// <summary>Creates a selector.</summary>
    /// <param name="options">The selection settings.</param>
    /// <param name="latency">Recent time to first byte per model, for <see cref="SelectionStrategy.Fastest"/> and <see cref="SelectionStrategy.Balanced"/>.</param>
    /// <param name="cache">Recent prompt-cache shares per model and route, for cost estimates; without it, list prices.</param>
    public DefaultModelSelector(ModelSelectionOptions? options = null, IModelLatency? latency = null, IModelCacheShare? cache = null)
    {
        _options = options ?? new ModelSelectionOptions();
        _latency = latency;
        _cache = cache;
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
            decimal cost = model.EstimateCost(requirements.EstimatedInputTokens, outputTokens, _cache?.Typical(model, requirements.Route));
            var candidate = new ModelCandidate(model, cost, meets);
            (meets ? meeting : below).Add(candidate);
        }

        IEnumerable<ModelCandidate> ordered = (requirements.Strategy ?? _options.Strategy) switch
        {
            SelectionStrategy.MostCapable => meeting
                .OrderByDescending(c => c.Model.Tier).ThenBy(c => c.EstimatedCost).ThenBy(c => c.Model.Priority),
            SelectionStrategy.Priority => meeting
                .OrderBy(c => c.Model.Priority).ThenBy(c => c.EstimatedCost).ThenBy(c => c.Model.Tier),
            SelectionStrategy.Fastest when Latencies(meeting) is { } latency => meeting
                .OrderBy(c => latency[c.Model]).ThenBy(c => c.EstimatedCost).ThenBy(c => c.Model.Tier).ThenBy(c => c.Model.Priority),
            SelectionStrategy.Balanced when Latencies(meeting) is { } latency => meeting
                .OrderBy(c => latency[c.Model] <= latency.Values.Min() * _options.BalancedLatencyFactor ? 0 : 1)
                .ThenBy(c => c.EstimatedCost).ThenBy(c => latency[c.Model]).ThenBy(c => c.Model.Tier).ThenBy(c => c.Model.Priority),
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

    /// <summary>
    /// Each candidate's typical time to first byte; one without enough recent turns gets the median of the others, so
    /// it is neither preferred nor avoided. <see langword="null"/> when none is known: order by cost then.
    /// </summary>
    private Dictionary<ModelDefinition, TimeSpan>? Latencies(List<ModelCandidate> candidates)
    {
        if (_latency is null || candidates.Count == 0)
        {
            return null;
        }

        Dictionary<ModelDefinition, TimeSpan?> known = candidates.ToDictionary(c => c.Model, c => _latency.TypicalTimeToFirstByte(c.Model));
        TimeSpan[] values = [.. known.Values.OfType<TimeSpan>().Order()];
        if (values.Length == 0)
        {
            return null;
        }

        TimeSpan typical = values.Length % 2 == 1 ? values[values.Length / 2] : (values[(values.Length / 2) - 1] + values[values.Length / 2]) / 2;
        return known.ToDictionary(k => k.Key, k => k.Value ?? typical);
    }
}
