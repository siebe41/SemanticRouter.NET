namespace SemanticRouter;

/// <summary>
/// Classifies a prompt into model requirements (tier, capabilities, locality).
/// </summary>
public interface IPromptClassifier
{
    /// <summary>Classifies <paramref name="prompt"/> and decides what a model must be able to do to answer it.</summary>
    ValueTask<PromptClassificationResult> ClassifyAsync(string prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs any expensive one-off initialization (such as embedding route utterances) ahead of
    /// the first request. Safe to call repeatedly.
    /// </summary>
    ValueTask WarmUpAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
