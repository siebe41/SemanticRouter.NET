namespace SemanticRouter.AspNetCore;

/// <summary>
/// Claude Code's own memory: the user-level <c>CLAUDE.md</c> (and <c>CLAUDE.local.md</c>) in the <c>.claude</c>
/// folder, and each project's auto-memory folder, <c>projects/{project}/memory/</c>, whose <c>MEMORY.md</c> index
/// links the topic files beside it. Paths in the map are relative to the <c>.claude</c> folder, so each project's notes
/// sit in their own folder.
/// </summary>
public sealed partial class ClaudeCodeMemorySource : IMemorySource
{
    private static readonly HashSet<string> Markdown = new(StringComparer.OrdinalIgnoreCase) { ".md" };

    private readonly string _root;
    private readonly int _maxNotes;
    private readonly long _maxBytes;

    /// <summary>Creates a source reading <paramref name="path"/>, or <c>~/.claude</c> when none is given.</summary>
    public ClaudeCodeMemorySource(string name, string? path = null, int maxNotes = 5000, long maxNoteBytes = 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        _root = MarkdownMemorySource.ExpandPath(string.IsNullOrWhiteSpace(path) ? "~/.claude" : path);
        _maxNotes = Math.Max(1, maxNotes);
        _maxBytes = Math.Max(1, maxNoteBytes);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Kind => nameof(MemorySourceType.ClaudeCode);

    /// <inheritdoc />
    public string? Location => _root;

    /// <summary>Notes that were not read because there are more than the limit (set by <see cref="LoadAsync"/>).</summary>
    public int Skipped { get; private set; }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryDocument>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException($"The folder '{_root}' does not exist. Is Claude Code installed for this user?");
        }

        (IReadOnlyList<MemoryDocument> documents, int skipped) = await MarkdownMemorySource
            .ReadAsync(_root, Files(), _maxNotes, _maxBytes, cancellationToken)
            .ConfigureAwait(false);
        Skipped = skipped;
        return documents.Select(d => d with { Title = TitleFor(d) }).ToArray();
    }

    /// <summary>
    /// <c>CLAUDE.md (user)</c> for the user-level files and <c>{project} · {file}</c> for memory files, unless the
    /// file's frontmatter names it: every project has a <c>MEMORY.md</c>, so file names alone do not say whose it is.
    /// </summary>
    internal static string TitleFor(MemoryDocument document)
    {
        if (FrontmatterName().Match(document.Content) is { Success: true } named)
        {
            return named.Groups["name"].Value.Trim().Trim('"', '\'');
        }

        string[] parts = document.Path.Split('/');
        if (parts.Length < 4 || parts[0] != "projects")
        {
            return document.Path + " (user)";
        }

        return ProjectName(parts[1]) + " · " + Path.GetFileNameWithoutExtension(document.Path);
    }

    /// <summary>
    /// A readable name for a project folder. Claude Code names it after the project's path with separators replaced
    /// by dashes (<c>-home-me-app</c>, <c>C--Users-me-app</c>), so the home-folder prefix is dropped.
    /// </summary>
    internal static string ProjectName(string folder)
    {
        string name = HomePrefix().Replace(folder.TrimStart('-'), string.Empty);
        return name.Length > 0 ? name : folder;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\A---\r?\n(?:.*\r?\n)*?name:[ \t]*(?<name>[^\r\n]+)\r?\n(?:.*\r?\n)*?---")]
    private static partial System.Text.RegularExpressions.Regex FrontmatterName();

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:[A-Za-z]--)?(?:home|Users)-[^-]+-")]
    private static partial System.Text.RegularExpressions.Regex HomePrefix();

    private IEnumerable<string> Files()
    {
        foreach (string name in new[] { "CLAUDE.md", "CLAUDE.local.md" })
        {
            string file = Path.Combine(_root, name);
            if (File.Exists(file))
            {
                yield return file;
            }
        }

        string projects = Path.Combine(_root, "projects");
        if (!Directory.Exists(projects))
        {
            yield break;
        }

        var options = new EnumerationOptions { IgnoreInaccessible = true };
        foreach (string project in Directory.EnumerateDirectories(projects, "*", options).Order(StringComparer.Ordinal))
        {
            string memory = Path.Combine(project, "memory");
            if (Directory.Exists(memory))
            {
                foreach (string file in MarkdownMemorySource.EnumerateFiles(memory, Markdown))
                {
                    yield return file;
                }
            }
        }
    }
}
