namespace SemanticRouter;

/// <summary>
/// Scores how demanding a prompt is, independent of its semantic intent.
/// Implementations must be thread-safe, synchronous and cheap: they run on every request,
/// before any embedding call.
/// </summary>
public interface IComplexityAnalyzer
{
    /// <summary>Analyzes <paramref name="prompt"/>.</summary>
    ComplexityAnalysis Analyze(string prompt);
}
