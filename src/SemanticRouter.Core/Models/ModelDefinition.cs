namespace SemanticRouter;

/// <summary>
/// A model the router may dispatch to: which connector serves it, how capable it is and what it costs.
/// Bindable from <c>SemanticRouter:Models</c>.
/// </summary>
public sealed class ModelDefinition
{
    /// <summary>Unique catalog name. Clients may request it explicitly as the <c>model</c> of a request.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Name of the connector (configured under <c>SemanticRouter:Connectors</c>) that serves this model.</summary>
    public string Connector { get; set; } = string.Empty;

    /// <summary>
    /// The model identifier the connector should request (e.g. <c>gpt-4.1-mini</c>, <c>qwen/qwen3-coder-30b</c>,
    /// or <c>sonnet</c> for the Claude Code CLI). When <see langword="null"/>, the connector's default is used.
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Ordinal capability tier, compared with <see cref="RouteDefinition.Tier"/>. Suggested scale: 1 = small local model,
    /// 2 = mid-size, 3 = strong general model, 4 = frontier, 5 = frontier with extended reasoning. Default 1.
    /// </summary>
    public int Tier { get; set; } = 1;

    /// <summary>Capability tags this model supports (e.g. <c>code</c>, <c>tools</c>, <c>vision</c>, <c>reasoning</c>).</summary>
    public IList<string> Capabilities { get; } = new List<string>();

    /// <summary>
    /// Whether prompts sent to this model stay on infrastructure you control. Only local models may serve
    /// <see cref="RouteDefinition.LocalOnly"/> routes.
    /// </summary>
    public bool IsLocal { get; set; }

    /// <summary>Context window in tokens. Models too small for a request are skipped. 0 means unknown (never skipped).</summary>
    public int ContextWindow { get; set; }

    /// <summary>
    /// Marginal price in USD per million input tokens. Use 0 for local models and for flat-rate subscriptions
    /// (whose marginal cost is zero until their quota runs out). <see cref="SelectionStrategy.CheapestCapable"/> still
    /// puts a free model that is not <see cref="IsLocal"/> after the local ones, because it uses up quota.
    /// </summary>
    public decimal InputCostPerMillionTokens { get; set; }

    /// <summary>Marginal price in USD per million output tokens.</summary>
    public decimal OutputCostPerMillionTokens { get; set; }

    /// <summary>Price per million input tokens read from the provider's prompt cache. Defaults to <see cref="InputCostPerMillionTokens"/>.</summary>
    public decimal? CacheReadCostPerMillionTokens { get; set; }

    /// <summary>Price per million input tokens written to the provider's prompt cache. Defaults to <see cref="InputCostPerMillionTokens"/>.</summary>
    public decimal? CacheWriteCostPerMillionTokens { get; set; }

    /// <summary>
    /// For a subscription (marginal cost 0, not local): the provider's pay-as-you-go API price per million input
    /// tokens for the same model. Each request is valued at these prices, as what the subscription saved you; the value
    /// is reported apart from spend and never counts against budgets or ranking. <see langword="null"/>: not valued.
    /// </summary>
    public decimal? ApiInputCostPerMillionTokens { get; set; }

    /// <summary>The API price per million output tokens. See <see cref="ApiInputCostPerMillionTokens"/>.</summary>
    public decimal? ApiOutputCostPerMillionTokens { get; set; }

    /// <summary>The API price per million cache-read input tokens. Defaults to <see cref="ApiInputCostPerMillionTokens"/>.</summary>
    public decimal? ApiCacheReadCostPerMillionTokens { get; set; }

    /// <summary>The API price per million cache-write input tokens. Defaults to <see cref="ApiInputCostPerMillionTokens"/>.</summary>
    public decimal? ApiCacheWriteCostPerMillionTokens { get; set; }

    /// <summary>User preference: lower values are preferred. Breaks ties, and is the primary key for <see cref="SelectionStrategy.Priority"/>. Default 0.</summary>
    public int Priority { get; set; }

    /// <summary>Set to <see langword="false"/> to keep a model in configuration without routing to it. Default <see langword="true"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Optional free-form description.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// Hours the model may be used, e.g. <c>08:00-18:00</c> or <c>22:00-06:00</c>, in
    /// <see cref="ModelSelectionOptions.TimeZone"/>. Outside them it is not tried (an office GPU that is off at night, a
    /// subscription you keep for working hours). <see langword="null"/>: always.
    /// </summary>
    public string? ActiveHours { get; set; }

    /// <summary>Whether <see cref="ActiveHours"/> allows the model at <paramref name="now"/> in <paramref name="zone"/>.</summary>
    public bool IsActiveAt(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return !TimeWindow.TryParse(ActiveHours, out TimeWindow window)
               || window.Contains(TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime));
    }

    /// <summary>Whether the model declares every tag in <paramref name="required"/> (case-insensitive).</summary>
    public bool HasCapabilities(IEnumerable<string> required)
    {
        ArgumentNullException.ThrowIfNull(required);
        foreach (string capability in required)
        {
            if (!Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// What a served request cost at this model's prices. <paramref name="inputTokens"/> excludes cached tokens, which
    /// are priced separately (cache reads and cache writes).
    /// </summary>
    public decimal Cost(long inputTokens, long outputTokens, long cacheReadTokens = 0, long cacheWriteTokens = 0) =>
        ((inputTokens * InputCostPerMillionTokens)
         + (outputTokens * OutputCostPerMillionTokens)
         + (cacheReadTokens * (CacheReadCostPerMillionTokens ?? InputCostPerMillionTokens))
         + (cacheWriteTokens * (CacheWriteCostPerMillionTokens ?? InputCostPerMillionTokens))) / 1_000_000m;

    /// <summary>
    /// What a request on a subscription would have cost at its API prices (<see cref="ApiInputCostPerMillionTokens"/>):
    /// the value it got from the subscription. 0 for a model with a marginal cost, or without API prices.
    /// </summary>
    public decimal ApiValue(long inputTokens, long outputTokens, long cacheReadTokens = 0, long cacheWriteTokens = 0)
    {
        if (InputCostPerMillionTokens > 0 || OutputCostPerMillionTokens > 0 || (ApiInputCostPerMillionTokens is null && ApiOutputCostPerMillionTokens is null))
        {
            return 0m;
        }

        decimal input = ApiInputCostPerMillionTokens ?? 0m;
        return ((inputTokens * input)
                + (outputTokens * (ApiOutputCostPerMillionTokens ?? 0m))
                + (cacheReadTokens * (ApiCacheReadCostPerMillionTokens ?? input))
                + (cacheWriteTokens * (ApiCacheWriteCostPerMillionTokens ?? input))) / 1_000_000m;
    }

    /// <summary>Estimated marginal cost in USD for a request of the given size.</summary>
    public decimal EstimateCost(int inputTokens, int outputTokens) =>
        ((InputCostPerMillionTokens * inputTokens) + (OutputCostPerMillionTokens * outputTokens)) / 1_000_000m;

    /// <summary>
    /// Estimated marginal cost in USD for a request of the given size when <paramref name="cache"/> says how much of
    /// the input is read from and written to the prompt cache: that part is priced at the cache prices, the rest at the
    /// input price. Without a share, the same as <see cref="EstimateCost(int, int)"/>.
    /// </summary>
    public decimal EstimateCost(int inputTokens, int outputTokens, PromptCacheShare? cache)
    {
        if (cache is null)
        {
            return EstimateCost(inputTokens, outputTokens);
        }

        double readShare = Math.Clamp(cache.Read, 0, 1), writeShare = Math.Clamp(cache.Write, 0, 1 - readShare);
        decimal read = (decimal)readShare, write = (decimal)writeShare;
        decimal input = ((1 - read - write) * InputCostPerMillionTokens)
            + (read * (CacheReadCostPerMillionTokens ?? InputCostPerMillionTokens))
            + (write * (CacheWriteCostPerMillionTokens ?? InputCostPerMillionTokens));
        return ((input * inputTokens) + (OutputCostPerMillionTokens * outputTokens)) / 1_000_000m;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Name} via {Connector} (tier {Tier})";
}
