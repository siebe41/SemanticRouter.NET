using System.Text.RegularExpressions;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Recognises a message that corrects the previous answer ("that's wrong", "it still doesn't compile", "no, I meant…"),
/// for <see cref="FeedbackKind.Corrected"/>. English phrases, anchored and short, so new instructions that merely
/// mention an error do not count.
/// </summary>
internal static partial class Corrections
{
    /// <summary>Whether <paramref name="prompt"/> reads as a correction of the previous answer.</summary>
    public static bool Matches(string prompt)
    {
        string text = prompt.Trim();
        if (text.Length == 0 || text.Length > 400)
        {
            return false;
        }

        return Leading().IsMatch(text) || (text.Length <= 200 && Anywhere().IsMatch(text));
    }

    [GeneratedRegex(
        @"^(no\b[,.!]|nope\b|wrong\b|incorrect\b|that['’]?s (wrong|incorrect|not (right|it|what i))|this is (wrong|incorrect)|not what i (asked|meant|wanted)"
        + @"|(it|that|this) (still )?(doesn['’]?t|didn['’]?t|does not|did not|won['’]?t|will not) (work|compile|build|run|help|fix)"
        + @"|you (missed|forgot|ignored|broke)|try again\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex Leading();

    [GeneratedRegex(
        @"\b(still (fails|failing|broken|not working|wrong|crashes|crashing)|same error|(doesn['’]?t|didn['’]?t|does not|did not) (work|fix it|compile))\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Anywhere();
}
