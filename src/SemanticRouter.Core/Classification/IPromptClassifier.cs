namespace SemanticRouter;

/// <summary>
/// Classifies a prompt into a routing decision. The main entry point of the library.
/// </summary>
public interface IPromptClassifier
{
    /// <summary>Classifies <paramref name="prompt"/> and decides where it should be sent.</summary>
    ValueTask<PromptClassificationResult> ClassifyAsync(string prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs any expensive one-off initialization (such as embedding route utterances) ahead of
    /// the first request. Safe to call repeatedly.
    /// </summary>
    ValueTask WarmUpAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
