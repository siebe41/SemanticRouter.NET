using System.Globalization;
using System.Text;

namespace SemanticRouter.Host;

/// <summary>
/// Text from someone else (an issue body), made safe to read and to write into Markdown that GitHub renders. Such text
/// is only ever placed inside a code span or a code block: there GitHub renders no HTML, links, images or <c>@mentions</c>
/// and references no issues, and the text cannot close the span or block it is in.
/// </summary>
internal static class UntrustedText
{
    /// <summary>
    /// <paramref name="text"/> without control characters (but line breaks, when <paramref name="multiline"/>) and invisible
    /// formatting characters (zero-width spaces, bidirectional overrides), whitespace runs collapsed on one line, trimmed
    /// and cut to <paramref name="max"/> characters with an ellipsis.
    /// </summary>
    public static string Clean(string text, int max, bool multiline)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(max, 1);
        var builder = new StringBuilder(Math.Min(text.Length, max) + 2);
        bool truncated = false;

        // By Unicode scalar value, so an emoji's surrogate pair stays whole and a lone half becomes U+FFFD.
        foreach (Rune rune in text.EnumerateRunes())
        {
            string? append = rune.Value switch
            {
                '\n' when multiline => "\n",
                _ when Rune.IsWhiteSpace(rune) => builder.Length > 0 && builder[^1] == ' ' && !multiline ? null : " ",
                _ => Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse
                    or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                    ? null
                    : rune.ToString(),
            };

            if (append is null)
            {
                continue;
            }

            if (builder.Length + append.Length > max)
            {
                truncated = true;
                break;
            }

            builder.Append(append);
        }

        string clean = builder.ToString().Trim();
        return truncated ? clean + "…" : clean;
    }

    /// <summary>
    /// A Markdown code span with <paramref name="text"/> on one line. Backticks become apostrophes, so the text cannot end
    /// the span early; an empty value is shown as <c>(none)</c>.
    /// </summary>
    public static string Code(string? text, int max = 100)
    {
        string clean = text is null ? string.Empty : Clean(text, max, multiline: false).Replace('`', '\'');
        return clean.Length == 0 ? "(none)" : "`" + clean + "`";
    }

    /// <summary>
    /// A fenced Markdown code block around <paramref name="text"/>, its fence one backtick longer than the longest run
    /// of backticks in it, so nothing in the text can close it.
    /// </summary>
    public static string Block(string text, string language = "")
    {
        ArgumentNullException.ThrowIfNull(text);
        int longest = 0, run = 0;
        foreach (char c in text)
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        string fence = new('`', Math.Max(3, longest + 1));
        return $"{fence}{language}\n{text}\n{fence}";
    }
}
