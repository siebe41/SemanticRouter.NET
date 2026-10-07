namespace SemanticRouter;

/// <summary>
/// Default <see cref="IComplexityAnalyzer"/>: a single line-oriented pass that extracts length,
/// code, structure and reasoning signals without regular expressions or allocations proportional
/// to the prompt size.
/// </summary>
public sealed class HeuristicComplexityAnalyzer : IComplexityAnalyzer
{
    // Prefixes that rarely start an English sentence but commonly start a line of source code.
    private static readonly string[] CodeLinePrefixes =
    [
        "def ", "class ", "import ", "public ", "private ", "protected ", "internal ", "function ", "const ",
        "var ", "return ", "using ", "namespace ", "#include", "package ", "fn ", "func ", "SELECT ", "INSERT INTO",
        "UPDATE ", "DELETE FROM", "if (", "for (", "while (", "} else", "</", "<div", "//", "/*",
    ];

    private readonly ComplexityHeuristicsOptions _options;
    private readonly string[] _reasoningKeywords;

    /// <summary>Creates an analyzer with the given options (or defaults).</summary>
    public HeuristicComplexityAnalyzer(ComplexityHeuristicsOptions? options = null)
    {
        _options = options ?? new ComplexityHeuristicsOptions();
        _reasoningKeywords = ((IEnumerable<string>?)_options.ReasoningKeywords ?? ComplexityHeuristicsOptions.DefaultReasoningKeywords)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <inheritdoc />
    public ComplexityAnalysis Analyze(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return ComplexityAnalysis.Empty;
        }

        int lineCount = 0;
        int nonEmptyLines = 0;
        int codeBlocks = 0;
        int fencedLines = 0;
        int codeLikeLines = 0;
        int structuralItems = 0;
        int questions = 0;
        bool inFence = false;

        foreach (ReadOnlySpan<char> rawLine in prompt.AsSpan().EnumerateLines())
        {
            lineCount++;
            ReadOnlySpan<char> line = rawLine.Trim();
            if (line.IsEmpty)
            {
                continue;
            }

            nonEmptyLines++;

            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                if (!inFence)
                {
                    codeBlocks++;
                }

                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                fencedLines++;
                continue;
            }

            if (IsStructuralItem(line))
            {
                structuralItems++;
            }
            else if (LooksLikeCode(line))
            {
                codeLikeLines++;
            }

            questions += line.Count('?');
        }

        bool unfencedCode = codeLikeLines >= 2 && codeLikeLines * 10 >= nonEmptyLines * 3;
        bool containsCode = codeBlocks > 0 || unfencedCode;
        int estimatedTokens = TokenEstimator.Estimate(prompt);
        int reasoningHits = CountReasoningKeywords(prompt);

        double lengthSignal = Math.Sqrt(Math.Min(1.0, (double)estimatedTokens / _options.TokenSaturation));
        double codeSignal = codeBlocks > 0
            ? Math.Min(1.0, 0.6 + (0.2 * (codeBlocks - 1)) + Math.Min(0.2, fencedLines / 200.0))
            : unfencedCode ? 0.5 : 0.0;
        double structureSignal = Math.Min(
            1.0,
            (Math.Max(0, questions - 1) * 0.2) + (structuralItems * 0.12) + (Math.Max(0, nonEmptyLines - 8) * 0.01));
        double reasoningSignal = Math.Min(1.0, (double)reasoningHits / _options.ReasoningKeywordSaturation);

        double remaining = (1 - (_options.LengthWeight * lengthSignal))
                           * (1 - (_options.CodeWeight * codeSignal))
                           * (1 - (_options.StructureWeight * structureSignal))
                           * (1 - (_options.ReasoningWeight * reasoningSignal));

        return new ComplexityAnalysis
        {
            EstimatedTokens = estimatedTokens,
            LineCount = lineCount,
            CodeBlockCount = codeBlocks,
            CodeLineCount = fencedLines + codeLikeLines,
            ContainsCode = containsCode,
            QuestionCount = questions,
            StructuralItemCount = structuralItems,
            ReasoningKeywordCount = reasoningHits,
            LengthSignal = lengthSignal,
            CodeSignal = codeSignal,
            StructureSignal = structureSignal,
            ReasoningSignal = reasoningSignal,
            Score = Math.Clamp(1 - remaining, 0.0, 1.0),
        };
    }

    private static bool IsStructuralItem(ReadOnlySpan<char> line)
    {
        // Bullets: "- x", "* x", "+ x". Headings: "# x", "## x".
        if (line.Length >= 2 && line[1] == ' ' && line[0] is '-' or '*' or '+')
        {
            return true;
        }

        int hashes = 0;
        while (hashes < line.Length && line[hashes] == '#')
        {
            hashes++;
        }

        if (hashes is > 0 and <= 6 && hashes < line.Length && line[hashes] == ' ')
        {
            return true;
        }

        // Numbered: "1. x", "12) x".
        int digits = 0;
        while (digits < line.Length && char.IsAsciiDigit(line[digits]))
        {
            digits++;
        }

        return digits is > 0 and <= 3
               && digits + 1 < line.Length
               && line[digits] is '.' or ')'
               && line[digits + 1] == ' ';
    }

    private static bool LooksLikeCode(ReadOnlySpan<char> line)
    {
        char last = line[^1];
        if (last is ';' or '{' or '}')
        {
            return true;
        }

        if (line.Contains("=>", StringComparison.Ordinal) || line.Contains("();", StringComparison.Ordinal)
            || line.Contains("::", StringComparison.Ordinal) || line.Contains("!=", StringComparison.Ordinal))
        {
            return true;
        }

        foreach (string prefix in CodeLinePrefixes)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private int CountReasoningKeywords(string prompt)
    {
        int hits = 0;
        foreach (string keyword in _reasoningKeywords)
        {
            if (ContainsWholePhrase(prompt, keyword))
            {
                hits++;
            }
        }

        return hits;
    }

    internal static bool ContainsWholePhrase(string text, string phrase)
    {
        int start = 0;
        while (start <= text.Length - phrase.Length)
        {
            int index = text.IndexOf(phrase, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            int end = index + phrase.Length;
            bool leftBoundary = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            bool rightBoundary = end == text.Length || !char.IsLetterOrDigit(text[end]);
            if (leftBoundary && rightBoundary)
            {
                return true;
            }

            start = index + 1;
        }

        return false;
    }
}
