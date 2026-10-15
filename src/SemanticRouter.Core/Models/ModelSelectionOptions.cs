namespace SemanticRouter;

/// <summary>Model selection and failover settings. Bound from <c>SemanticRouter:Selection</c>.</summary>
public sealed class ModelSelectionOptions
{
    /// <summary>Default ordering strategy. Clients can override it per request. Default <see cref="SelectionStrategy.CheapestCapable"/>.</summary>
    public SelectionStrategy Strategy { get; set; } = SelectionStrategy.CheapestCapable;

    /// <summary>
    /// When no available model meets the required tier (for example the frontier subscription is out of quota), fall
    /// back to the most capable model that is available rather than failing. Default <see langword="true"/>.
    /// </summary>
    public bool AllowBelowRequiredTier { get; set; } = true;

    /// <summary>Output tokens assumed when estimating cost and checking context windows. Default 512.</summary>
    public int EstimatedOutputTokens { get; set; } = 512;

    /// <summary>Maximum number of models tried for a single request before giving up. Default 3.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>How long a model is skipped after it reports an exhausted quota or rate limit. Default 15 minutes.</summary>
    public TimeSpan QuotaCooldown { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>How long a model is skipped after it fails or is unreachable. Default 30 seconds.</summary>
    public TimeSpan FailureCooldown { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// For <see cref="SelectionStrategy.Balanced"/>: how many times slower than the fastest model a model may be and
    /// still be chosen for being cheaper. Default 1.5.
    /// </summary>
    public double BalancedLatencyFactor { get; set; } = 1.5;

    /// <summary>How far back time to first byte is measured for the latency-aware strategies. Default one day.</summary>
    public TimeSpan LatencyWindow { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Turns a model needs in <see cref="LatencyWindow"/> before its latency counts; with fewer it counts as typical. Default 5.</summary>
    public int LatencySamples { get; set; } = 5;

    /// <summary>
    /// How far back the share of input each model reads from and writes to its prompt cache is measured, for cost
    /// estimates. Default one day.
    /// </summary>
    public TimeSpan CacheShareWindow { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Turns with reported usage a model needs in <see cref="CacheShareWindow"/> (on a route, or over all its turns)
    /// before its cache share counts; with fewer its list prices are used. Default 5.
    /// </summary>
    public int CacheShareSamples { get; set; } = 5;

    /// <summary>The time zone of models' <see cref="ModelDefinition.ActiveHours"/> (an IANA or Windows id). Default: this machine's.</summary>
    public string? TimeZone { get; set; }

    /// <summary>The <see cref="TimeZone"/>, or this machine's when unset or unknown.</summary>
    public TimeZoneInfo EffectiveTimeZone =>
        TimeZone is { Length: > 0 } id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out TimeZoneInfo? zone) ? zone : TimeZoneInfo.Local;

    internal IEnumerable<string> GetValidationErrors()
    {
        if (!Enum.IsDefined(Strategy))
        {
            yield return $"Selection.{nameof(Strategy)} '{Strategy}' is unknown.";
        }

        if (!(BalancedLatencyFactor >= 1))
        {
            yield return $"Selection.{nameof(BalancedLatencyFactor)} must be at least 1.";
        }

        if (LatencyWindow <= TimeSpan.Zero || LatencySamples < 1)
        {
            yield return $"Selection.{nameof(LatencyWindow)} must be positive and {nameof(LatencySamples)} at least 1.";
        }

        if (CacheShareWindow <= TimeSpan.Zero || CacheShareSamples < 1)
        {
            yield return $"Selection.{nameof(CacheShareWindow)} must be positive and {nameof(CacheShareSamples)} at least 1.";
        }

        if (EstimatedOutputTokens < 0)
        {
            yield return $"Selection.{nameof(EstimatedOutputTokens)} must not be negative.";
        }

        if (MaxAttempts < 1)
        {
            yield return $"Selection.{nameof(MaxAttempts)} must be at least 1.";
        }

        if (QuotaCooldown < TimeSpan.Zero || FailureCooldown < TimeSpan.Zero)
        {
            yield return "Selection cooldowns must not be negative.";
        }
    }
}
