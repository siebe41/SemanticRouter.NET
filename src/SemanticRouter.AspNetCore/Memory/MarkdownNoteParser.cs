using System.Text.RegularExpressions;

namespace SemanticRouter.AspNetCore;

/// <summary>What the memory map needs from a Markdown note: title, aliases, tags and outgoing links.</summary>
internal sealed record ParsedNote(string Title, IReadOnlyList<string> Aliases, IReadOnlyList<string> Tags, IReadOnlyList<NoteLink> Links);

/// <summary>An outgoing link: a <c>[[wikilink]]</c> target (title or path) or a Markdown link's relative path.</summary>
internal sealed record NoteLink(string Target, bool IsPath);

/// <summary>Parses the parts of Obsidian-flavoured Markdown the memory map uses. Code blocks are ignored.</summary>
internal static partial class MarkdownNoteParser
{
    private static readonly HashSet<string> AttachmentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".bmp", ".avif", ".pdf", ".mp3", ".mp4", ".m4a", ".wav", ".ogg", ".webm", ".mov", ".canvas", ".base",
    };

    public static ParsedNote Parse(string path, string content, string? title = null)
    {
        (Dictionary<string, List<string>> frontmatter, string body) = SplitFrontmatter(content);
        body = FencedCode().Replace(body, string.Empty);

        var aliases = new List<string>();
        var tags = new List<string>();
        if (frontmatter.TryGetValue("aliases", out List<string>? a) || frontmatter.TryGetValue("alias", out a))
        {
            aliases.AddRange(a);
        }

        if (frontmatter.TryGetValue("tags", out List<string>? t) || frontmatter.TryGetValue("tag", out t))
        {
            tags.AddRange(t.Select(x => x.TrimStart('#')));
        }

        foreach (Match match in InlineTag().Matches(body))
        {
            tags.Add(match.Groups["tag"].Value);
        }

        var links = new List<NoteLink>();
        foreach (Match match in WikiLink().Matches(body))
        {
            string target = match.Groups["target"].Value.Trim();
            // ![[diagram.png]] embeds an attachment, not a note.
            if (target.Length > 0 && !AttachmentExtensions.Contains(Path.GetExtension(target)))
            {
                links.Add(new NoteLink(StripMd(target), target.Contains('/', StringComparison.Ordinal)));
            }
        }

        foreach (Match match in MarkdownLink().Matches(body))
        {
            string target = Uri.UnescapeDataString(match.Groups["target"].Value);
            if (!target.Contains("://", StringComparison.Ordinal))
            {
                links.Add(new NoteLink(target, IsPath: true));
            }
        }

        string resolvedTitle = title
            ?? (frontmatter.TryGetValue("title", out List<string>? titles) && titles.Count > 0 ? titles[0] : null)
            ?? Path.GetFileNameWithoutExtension(path);

        return new ParsedNote(
            resolvedTitle,
            aliases.Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            tags.Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            links);
    }

    internal static string StripMd(string target) =>
        target.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? target[..^3] : target;

    /// <summary>
    /// Reads the YAML frontmatter keys the map uses. Handles <c>key: value</c>, <c>key: [a, b]</c> and block lists
    /// (<c>key:</c> followed by <c>- a</c> lines); anything more elaborate is ignored rather than misread.
    /// </summary>
    private static (Dictionary<string, List<string>> Values, string Body) SplitFrontmatter(string content)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string text = content.StartsWith('﻿') ? content[1..] : content;
        if (!text.StartsWith("---", StringComparison.Ordinal))
        {
            return (values, content);
        }

        string[] lines = text.Split('\n');
        if (lines[0].TrimEnd() != "---")
        {
            return (values, content);
        }

        int end = Array.FindIndex(lines, 1, l => l.TrimEnd() is "---" or "...");
        if (end < 0)
        {
            return (values, content);
        }

        string? currentKey = null;
        for (int i = 1; i < end; i++)
        {
            string line = lines[i].TrimEnd();
            if (line.TrimStart().StartsWith("- ", StringComparison.Ordinal) && currentKey is not null)
            {
                values[currentKey].Add(Unquote(line.TrimStart()[2..]));
                continue;
            }

            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || char.IsWhiteSpace(line[0]))
            {
                continue;
            }

            currentKey = line[..colon].Trim();
            var list = new List<string>();
            values[currentKey] = list;
            string value = line[(colon + 1)..].Trim();
            if (value.StartsWith('[') && value.EndsWith(']'))
            {
                list.AddRange(value[1..^1].Split(',').Select(Unquote).Where(v => v.Length > 0));
            }
            else if (value.Length > 0)
            {
                list.Add(Unquote(value));
            }
        }

        return (values, string.Join('\n', lines.Skip(end + 1)));
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        return value.Length >= 2 && (value[0] is '"' or '\'') && value[^1] == value[0] ? value[1..^1] : value;
    }

    [GeneratedRegex(@"^(```|~~~).*?^\1", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex FencedCode();

    // [[target]], [[target|label]], [[target#heading]], [[target^block]] and the ![[embed]] forms.
    [GeneratedRegex(@"!?\[\[(?<target>[^\[\]|#^]+)(?:[#^][^\[\]|]*)?(?:\|[^\[\]]*)?\]\]")]
    private static partial Regex WikiLink();

    // [label](relative/path.md) and [label](path.md#heading); URL-encoded paths are decoded.
    [GeneratedRegex(@"\[[^\]]*\]\((?<target>[^)\s]+?\.md)(?:#[^)]*)?\)", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownLink();

    // #tag and #nested/tag, not headings ("# Title") or anchors inside words.
    [GeneratedRegex(@"(?<=^|\s)#(?<tag>[\p{L}_][\p{L}\p{N}_/-]*)", RegexOptions.Multiline)]
    private static partial Regex InlineTag();
}
