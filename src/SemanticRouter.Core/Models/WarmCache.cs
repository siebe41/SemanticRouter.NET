namespace SemanticRouter;

/// <summary>
/// A prompt cache a conversation has on a model: how many tokens of its context the model's provider holds, and until
/// when. Read from the cache, that prefix costs the model's <see cref="ModelDefinition.CacheReadCostPerMillionTokens"/>;
/// moved to another model, it is sent and written again at full price.
/// </summary>
/// <param name="Model">The catalog model whose cache it is.</param>
/// <param name="CachedTokens">The cached prefix: the last turn's cache reads plus cache writes.</param>
/// <param name="ExpiresAt">When the entry is gone: the last turn that used it plus the cache's lifetime.</param>
public sealed record WarmCache(string Model, int CachedTokens, DateTimeOffset ExpiresAt);
