namespace SemanticRouter;

/// <summary>
/// Tuning knobs for <see cref="HeuristicComplexityAnalyzer"/>.
/// </summary>
/// <remarks>
/// Each weight is the <em>maximum</em> contribution of a signal. Signals are combined with a
/// noisy-OR (<c>1 − Π(1 − weightᵢ · signalᵢ)</c>), so any single strong signal can push a prompt
/// towards the cloud on its own, and several moderate signals reinforce each other without the
/// total ever exceeding 1.
/// </remarks>
public sealed class ComplexityHeuristicsOptions
{
    /// <summary>Phrases that indicate multi-step reasoning or deep analysis.</summary>
    public static IReadOnlyList<string> DefaultReasoningKeywords { get; } =
    [
        "step by step", "analyze", "analyse", "compare", "contrast", "trade-off", "trade-offs", "tradeoffs",
        "pros and cons", "architecture", "architect", "design a", "prove", "derive", "optimize", "optimise",
        "refactor", "debug", "root cause", "explain why", "evaluate", "in depth", "in-depth", "comprehensive",
        "strategy", "reasoning", "implications", "edge cases", "migrate", "threat model",
    ];

    /// <summary>Token count at which the length signal saturates at 1. Default 2048.</summary>
    public int TokenSaturation { get; set; } = 2048;

    /// <summary>Maximum contribution of prompt length. Default 0.6.</summary>
    public double LengthWeight { get; set; } = 0.6;

    /// <summary>Maximum contribution of embedded source code. Default 0.6.</summary>
    public double CodeWeight { get; set; } = 0.6;

    /// <summary>Maximum contribution of multi-part structure (lists, headings, multiple questions). Default 0.5.</summary>
    public double StructureWeight { get; set; } = 0.5;

    /// <summary>Maximum contribution of reasoning keywords. Default 0.6.</summary>
    public double ReasoningWeight { get; set; } = 0.6;

    /// <summary>Number of distinct reasoning keywords at which the reasoning signal saturates. Default 3.</summary>
    public int ReasoningKeywordSaturation { get; set; } = 3;

    /// <summary>
    /// Overrides <see cref="DefaultReasoningKeywords"/>. Matching is case-insensitive and whole-word.
    /// When <see langword="null"/> the defaults are used.
    /// </summary>
#pragma warning disable CA2227 // Settable so configuration binding replaces (rather than appends to) the defaults.
    public IList<string>? ReasoningKeywords { get; set; }
#pragma warning restore CA2227

    internal IEnumerable<string> GetValidationErrors()
    {
        if (TokenSaturation <= 0)
        {
            yield return $"{nameof(TokenSaturation)} must be positive.";
        }

        if (ReasoningKeywordSaturation <= 0)
        {
            yield return $"{nameof(ReasoningKeywordSaturation)} must be positive.";
        }

        foreach ((string name, double weight) in new[]
                 {
                     (nameof(LengthWeight), LengthWeight), (nameof(CodeWeight), CodeWeight),
                     (nameof(StructureWeight), StructureWeight), (nameof(ReasoningWeight), ReasoningWeight),
                 })
        {
            if (weight is < 0 or > 1 || double.IsNaN(weight))
            {
                yield return $"Complexity.{name} must be between 0 and 1.";
            }
        }
    }
}
