namespace SemanticRouter;

/// <summary>
/// Structural and lexical signals extracted from a prompt, combined into a single complexity score.
/// </summary>
public sealed record ComplexityAnalysis
{
    /// <summary>An analysis of an empty prompt.</summary>
    public static ComplexityAnalysis Empty { get; } = new();

    /// <summary>Approximate number of model tokens (see <see cref="TokenEstimator"/>).</summary>
    public int EstimatedTokens { get; init; }

    /// <summary>Number of lines in the prompt.</summary>
    public int LineCount { get; init; }

    /// <summary>Number of fenced (<c>```</c> or <c>~~~</c>) code blocks.</summary>
    public int CodeBlockCount { get; init; }

    /// <summary>Number of lines that are inside code fences or look like source code.</summary>
    public int CodeLineCount { get; init; }

    /// <summary>Whether the prompt contains source code (fenced, or a run of code-like lines).</summary>
    public bool ContainsCode { get; init; }

    /// <summary>Number of question marks outside code blocks: a proxy for the number of asks.</summary>
    public int QuestionCount { get; init; }

    /// <summary>Number of bullet / numbered list items and markdown headings: a proxy for multi-part instructions.</summary>
    public int StructuralItemCount { get; init; }

    /// <summary>Number of distinct reasoning-intensive keywords found (e.g. "step by step", "trade-offs").</summary>
    public int ReasoningKeywordCount { get; init; }

    /// <summary>Length signal, 0..1.</summary>
    public double LengthSignal { get; init; }

    /// <summary>Code signal, 0..1.</summary>
    public double CodeSignal { get; init; }

    /// <summary>Structure signal, 0..1.</summary>
    public double StructureSignal { get; init; }

    /// <summary>Reasoning signal, 0..1.</summary>
    public double ReasoningSignal { get; init; }

    /// <summary>
    /// Overall complexity in the range 0..1, where higher means the prompt is more likely to need
    /// a frontier model.
    /// </summary>
    public double Score { get; init; }
}
