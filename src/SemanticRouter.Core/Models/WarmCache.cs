namespace SemanticRouter;

/// <summary>
/// A prompt cache a conversation has on a model: how many tokens of its context the model's provider holds, and until
/// when. Read from the cache, that prefix costs the model's <see cref="ModelDefinition.CacheReadCostPerMillionTokens"/>;
/// moved to another model, it is sent and written again at full price.
/// </summary>
public sealed record WarmCache
{
    /// <summary>The catalog model whose cache it is.</summary>
    public required string Model { get; init; }

    /// <summary>The cached prefix: the last turn's cache reads plus cache writes.</summary>
    public required int CachedTokens { get; init; }

    /// <summary>When the entry is gone: the last turn that used it plus the cache's lifetime.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}
