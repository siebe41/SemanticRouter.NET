namespace SemanticRouter;

/// <summary>
/// How much of a model's input it reads from and writes to the provider's prompt cache lately, so cost estimates price
/// that input at the cache prices (<see cref="ModelDefinition.EstimateCost(int, int, PromptCacheShare?)"/>).
/// </summary>
/// <remarks>
/// <see cref="DefaultModelSelector"/> asks once per eligible model, so implementations must be cheap and thread-safe.
/// </remarks>
public interface IModelCacheShare
{
    /// <summary>
    /// The model's recent shares on <paramref name="route"/>, or over all its turns when the route has too few or is
    /// <see langword="null"/>; <see langword="null"/> when there are too few recent turns to tell.
    /// </summary>
    PromptCacheShare? Typical(ModelDefinition model, string? route);
}

/// <summary>The share of a model's input tokens read from and written to the provider's prompt cache.</summary>
public sealed record PromptCacheShare
{
    /// <summary>Cache reads ÷ all input tokens (uncached input, cache reads and cache writes), 0 to 1.</summary>
    public required double Read { get; init; }

    /// <summary>Cache writes ÷ all input tokens, 0 to 1.</summary>
    public required double Write { get; init; }
}
