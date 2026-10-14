using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// The context blocks agentic clients put into user messages around what the person typed: Claude Code's
/// <c>&lt;system-reminder&gt;</c>, the GitHub Copilot app's <c>&lt;system_reminder&gt;</c>, <c>&lt;current_datetime&gt;</c>
/// and <c>&lt;copilot_tauri_workspace&gt;</c>, plus any tags configured in
/// <see cref="PromptRouterMiddlewareOptions.InjectedTags"/>. They are removed before classifying, so a request is routed
/// on what the person asked rather than on the client's boilerplate.
/// </summary>
internal static class InjectedBlocks
{
    /// <summary>The tags removed whatever the configuration.</summary>
    public static readonly IReadOnlyList<string> BuiltIn = ["system-reminder", "system_reminder", "current_datetime", "copilot_tauri_workspace"];

    private static readonly ConcurrentDictionary<string, (Regex Any, Regex Leading)> Patterns = new(StringComparer.Ordinal);

    /// <summary><paramref name="text"/> without its injected blocks; a block that is never closed runs to the end.</summary>
    public static string Remove(string text, IEnumerable<string>? extra = null)
    {
        if (text.IndexOf('<') < 0)
        {
            return text;
        }

        Regex any = For(extra).Any;
        return any.IsMatch(text) ? any.Replace(text, string.Empty) : text;
    }

    /// <summary>
    /// Where the person's text starts: the index after leading whitespace and injected blocks, or -1 when a leading
    /// block is never closed (the message is all injected context).
    /// </summary>
    public static int SkipLeading(string text, IEnumerable<string>? extra = null)
    {
        Regex leading = For(extra).Leading;
        int position = 0;
        while (position < text.Length && leading.Match(text, position) is { Success: true } block)
        {
            if (!block.Groups["closed"].Success)
            {
                return -1;
            }

            position = block.Index + block.Length;
        }

        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }

        return position;
    }

    private static (Regex Any, Regex Leading) For(IEnumerable<string>? extra)
    {
        IEnumerable<string> names = BuiltIn;
        if (extra is not null)
        {
            names = names.Concat(extra.Select(t => t.Trim().Trim('<', '>', '/').Trim()).Where(IsTagName));
        }

        string alternation = string.Join("|", names.Distinct(StringComparer.OrdinalIgnoreCase).Select(Regex.Escape));
        return Patterns.GetOrAdd(alternation, static a =>
        {
            const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;
            string block = $@"<(?<tag>{a})(?:\s[^<>]*)?>.*?(?:(?<closed></\k<tag>\s*>)|\z)";
            return (new Regex(block, Options), new Regex(@"\G\s*" + block, Options));
        });
    }

    private static bool IsTagName(string name) =>
        name.Length > 0 && char.IsLetter(name[0]) && name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ':' or '.');
}
