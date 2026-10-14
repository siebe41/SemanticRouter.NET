namespace SemanticRouter;

/// <summary>
/// Classifies a prompt into model requirements (tier, capabilities, locality).
/// </summary>
public interface IPromptClassifier
{
    /// <summary>Classifies <paramref name="prompt"/> and decides what a model must be able to do to answer it.</summary>
    ValueTask<PromptClassificationResult> ClassifyAsync(string prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Classifies <paramref name="prompt"/> without sending it anywhere: by its complexity alone, as if no route matched.
    /// Used when a prompt must stay on this machine and the embedding provider is not on it. A classifier that never
    /// sends prompts anywhere can leave the default, which is <see cref="ClassifyAsync"/>.
    /// </summary>
    ValueTask<PromptClassificationResult> ClassifyWithoutEmbeddingAsync(string prompt, CancellationToken cancellationToken = default) =>
        ClassifyAsync(prompt, cancellationToken);

    /// <summary>
    /// Performs any expensive one-off initialization (such as embedding route utterances) ahead of
    /// the first request. Safe to call repeatedly.
    /// </summary>
    ValueTask WarmUpAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
