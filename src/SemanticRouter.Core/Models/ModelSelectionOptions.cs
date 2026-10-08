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
