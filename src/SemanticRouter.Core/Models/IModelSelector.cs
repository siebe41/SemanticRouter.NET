namespace SemanticRouter;

/// <summary>Orders catalog models for a request. Implementations must be pure and thread-safe.</summary>
public interface IModelSelector
{
    /// <summary>Returns the models that may serve a request, best first.</summary>
    /// <param name="requirements">What the model must satisfy.</param>
    /// <param name="models">The catalog.</param>
    /// <param name="isAvailable">Optional health filter (e.g. models in quota cooldown are excluded).</param>
    IReadOnlyList<ModelCandidate> RankCandidates(
        ModelRequirements requirements,
        IReadOnlyCollection<ModelDefinition> models,
        Func<ModelDefinition, bool>? isAvailable = null);
}
