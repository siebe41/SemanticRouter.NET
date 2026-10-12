namespace SemanticRouter;

/// <summary>
/// How quickly models start answering lately, for <see cref="SelectionStrategy.Fastest"/> and
/// <see cref="SelectionStrategy.Balanced"/>.
/// </summary>
public interface IModelLatency
{
    /// <summary>The model's recent median time to first byte, or <see langword="null"/> when there are too few recent turns to tell.</summary>
    TimeSpan? TypicalTimeToFirstByte(ModelDefinition model);
}
