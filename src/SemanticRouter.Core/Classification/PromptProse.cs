namespace SemanticRouter;

/// <summary>
/// The text of a prompt that is compared with route utterances: the prompt without the code and logs pasted into it.
/// Utterances are short instructions ("write a unit test for this function"). A prompt that carries that instruction
/// with a 40-line function embeds mostly as the function and scores below every route, so it falls back to the
/// fallback tier. Complexity is still measured on the whole prompt, code included.
/// </summary>
/// <remarks>
/// Removed:
/// <list type="bullet">
///   <item>Fenced code blocks: from a line starting with three or more backticks or tildes (after any indentation) to a
///   line of at least as many of the same character, or to the end when the fence is never closed. A backtick line whose
///   info string contains a backtick (<c>```x```</c> on one line) is inline code, not a fence.</item>
///   <item>Unfenced runs of at least <see cref="MinimumPastedLines"/> lines that each look like code or log output
///   (indented, ending like a statement, starting like a log line or a closing bracket, or mostly symbols), as pasted
///   by clients that send code without fences. Blank lines inside a run are kept in it; any other line ends it.
///   Bullets and numbered items always count as prose.</item>
/// </list>
/// Inline code (<c>`name`</c>) stays. When nothing is removed, or when fewer than two words of prose are left (a prompt
/// that is only code, or "fix:" above a block), the whole prompt is used, as before.
/// </remarks>
internal static class PromptProse
{
    /// <summary>The shortest unfenced run of code-like lines that is removed: a line or two of code is part of the question.</summary>
    internal const int MinimumPastedLines = 8;

    /// <summary>The prose of <paramref name="prompt"/>, or <paramref name="prompt"/> itself when there is nothing to remove.</summary>
    public static string Extract(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (prompt.Length == 0)
        {
            return prompt;
        }

        // Most prompts have neither a fence nor enough lines for a pasted run.
        if (!prompt.Contains("```", StringComparison.Ordinal) && !prompt.Contains("~~~", StringComparison.Ordinal)
            && prompt.AsSpan().Count('\n') < MinimumPastedLines - 1)
        {
            return prompt;
        }

        List<Range> lines = SplitLines(prompt);
        bool[] removed = new bool[lines.Count];
        bool any = RemoveFences(prompt, lines, removed);
        any |= RemovePastedRuns(prompt, lines, removed);
        if (!any)
        {
            return prompt;
        }

        var prose = new System.Text.StringBuilder(prompt.Length);
        for (int i = 0; i < lines.Count; i++)
        {
            if (!removed[i])
            {
                prose.Append(prompt.AsSpan()[lines[i]]).Append('\n');
            }
        }

        string text = prose.ToString().Trim();
        return CountWords(text) >= 2 ? text : prompt;
    }

    private static List<Range> SplitLines(string text)
    {
        var lines = new List<Range>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                int end = i > start && text[i - 1] == '\r' ? i - 1 : i;
                lines.Add(start..end);
                start = i + 1;
            }
        }

        lines.Add(start..text.Length);
        return lines;
    }

    private static bool RemoveFences(string text, List<Range> lines, bool[] removed)
    {
        bool any = false;
        char fence = '\0';
        int fenceLength = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            ReadOnlySpan<char> line = text.AsSpan()[lines[i]].Trim();
            if (fence != '\0')
            {
                removed[i] = true;
                int run = Run(line, fence);
                if (run >= fenceLength && run == line.Length)
                {
                    fence = '\0';
                }

                continue;
            }

            if (line.Length >= 3 && line[0] is '`' or '~')
            {
                int run = Run(line, line[0]);
                if (run >= 3 && !(line[0] == '`' && line[run..].Contains('`')))
                {
                    (fence, fenceLength) = (line[0], run);
                    removed[i] = any = true;
                }
            }
        }

        return any;
    }

    private static bool RemovePastedRuns(string text, List<Range> lines, bool[] removed)
    {
        bool any = false;
        int first = -1;
        int last = -1;
        int codeLines = 0;
        for (int i = 0; i <= lines.Count; i++)
        {
            ReadOnlySpan<char> line = i < lines.Count ? text.AsSpan()[lines[i]] : default;
            if (i < lines.Count && !removed[i] && line.IsWhiteSpace())
            {
                continue;
            }

            if (i < lines.Count && !removed[i] && LooksPasted(line))
            {
                first = first < 0 ? i : first;
                last = i;
                codeLines++;
                continue;
            }

            // A prose line, a fence that was removed, or the end: the run so far is complete.
            if (codeLines >= MinimumPastedLines)
            {
                removed.AsSpan(first, last - first + 1).Fill(true);
                any = true;
            }

            (first, last, codeLines) = (-1, -1, 0);
        }

        return any;
    }

    /// <summary>Whether a line reads like pasted code, data or log output rather than something a person wrote.</summary>
    private static bool LooksPasted(ReadOnlySpan<char> line)
    {
        ReadOnlySpan<char> trimmed = line.Trim();

        // "- item" is a bullet; "-    return x;" is a line of a diff.
        bool diffLine = trimmed.Length > 2 && trimmed[0] is '-' or '+' && trimmed[1] == ' ' && trimmed[2] is ' ' or '\t';
        if (!diffLine && HeuristicComplexityAnalyzer.IsStructuralItem(trimmed))
        {
            return false;
        }

        if (line[0] == '\t' || line.StartsWith("  ", StringComparison.Ordinal))
        {
            return true; // indented: a body, a stack frame, nested data
        }

        if (HeuristicComplexityAnalyzer.LooksLikeCode(trimmed)
            || trimmed[^1] is '(' or ')' or '[' or ']' or ','
            || trimmed[0] is '}' or ')' or ']' or '[' or '<'
            || char.IsAsciiDigit(trimmed[0]))
        {
            return true; // a statement, a closing bracket, markup, a timestamped log line
        }

        int symbols = 0;
        int visible = 0;
        foreach (char c in trimmed)
        {
            if (!char.IsWhiteSpace(c))
            {
                visible++;
                symbols += char.IsLetterOrDigit(c) ? 0 : 1;
            }
        }

        return symbols * 4 >= visible; // a quarter or more symbols: an assignment, a table row, a separator
    }

    private static int Run(ReadOnlySpan<char> line, char c)
    {
        int run = 0;
        while (run < line.Length && line[run] == c)
        {
            run++;
        }

        return run;
    }

    private static int CountWords(string text)
    {
        int words = 0;
        bool inWord = false;
        foreach (char c in text)
        {
            bool letter = char.IsLetter(c);
            words += letter && !inWord ? 1 : 0;
            inWord = letter;
        }

        return words;
    }
}
