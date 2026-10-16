namespace SemanticRouter;

/// <summary>
/// Which local models are in memory now, so <see cref="SelectionStrategy.CheapestCapable"/> can prefer one that answers
/// without waiting for a load.
/// </summary>
public interface IModelResidency
{
    /// <summary>Whether the model is loaded on its server now. <see langword="null"/>: unknown (not a local server that says, or not asked yet).</summary>
    bool? IsLoaded(ModelDefinition model);
}
