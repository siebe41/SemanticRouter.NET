namespace SemanticRouter.AspNetCore;

/// <summary>
/// Reads a folder of Markdown notes recursively: an Obsidian vault, a Logseq or Foam folder, a folder written by the
/// Claude API's memory tool, or a tree of <c>CLAUDE.md</c> files. Folders whose name starts with a dot
/// (<c>.obsidian</c>, <c>.git</c>, <c>.trash</c>) are skipped, as are files over
/// <see cref="MemoryOptions.MaxNoteBytes"/>; at most <see cref="MemoryOptions.MaxNotesPerSource"/> notes are read.
/// </summary>
internal sealed class MarkdownMemorySource : IMemorySource
{
    private readonly string _root;
    private readonly IReadOnlyList<string> _exclude;
    private readonly HashSet<string> _extensions;
    private readonly int _maxNotes;
    private readonly long _maxBytes;

    /// <summary>Creates a source reading <paramref name="path"/>.</summary>
    /// <param name="name">The source's name.</param>
    /// <param name="path">The folder; <c>~</c> and <c>%VARIABLE%</c> are expanded.</param>
    /// <param name="exclude">Relative paths to skip, as <c>*</c> globs.</param>
    /// <param name="maxNotes">Most notes read.</param>
    /// <param name="maxNoteBytes">Largest file read.</param>
    /// <param name="extensions">File extensions read; default <c>.md</c>.</param>
    public MarkdownMemorySource(
        string name, string path, IEnumerable<string>? exclude = null, int maxNotes = 5000, long maxNoteBytes = 1024 * 1024,
        IEnumerable<string>? extensions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Name = name;
        _root = ExpandPath(path);
        _exclude = exclude?.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Replace('\\', '/')).ToArray() ?? [];
        _extensions = new HashSet<string>(
            (extensions ?? []).Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.StartsWith('.') ? e.Trim() : "." + e.Trim()),
            StringComparer.OrdinalIgnoreCase);
        if (_extensions.Count == 0)
        {
            _extensions.Add(".md");
        }

        _maxNotes = Math.Max(1, maxNotes);
        _maxBytes = Math.Max(1, maxNoteBytes);
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public string Kind => nameof(MemorySourceType.Markdown);

    /// <inheritdoc />
    public string? Location => _root;

    /// <summary>Notes that were not read because the source holds more than the limit (set by <see cref="LoadAsync"/>).</summary>
    public int Skipped { get; private set; }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryDocument>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_root))
        {
            throw new DirectoryNotFoundException($"The folder '{_root}' does not exist.");
        }

        IEnumerable<string> files = EnumerateFiles(_root, _extensions)
            .Where(file => !_exclude.Any(pattern => PromptRoutingMiddleware.GlobMatches(pattern, Path.GetRelativePath(_root, file).Replace('\\', '/'))));
        (IReadOnlyList<MemoryDocument> documents, int skipped) = await ReadAsync(_root, files, _maxNotes, _maxBytes, cancellationToken).ConfigureAwait(false);
        Skipped = skipped;
        return documents;
    }

    /// <summary>Expands <c>~</c> and <c>%VARIABLE%</c> and makes the path absolute.</summary>
    internal static string ExpandPath(string path)
    {
        string expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (expanded == "~" || expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
        {
            expanded = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), expanded.Length > 2 ? expanded[2..] : string.Empty);
        }

        return Path.GetFullPath(expanded);
    }

    /// <summary>
    /// Reads <paramref name="files"/> as notes with paths relative to <paramref name="root"/>, skipping files over
    /// <paramref name="maxBytes"/> and counting the ones over <paramref name="maxNotes"/>. Files that are locked or
    /// being written are left for the next scan.
    /// </summary>
    internal static async Task<(IReadOnlyList<MemoryDocument> Documents, int Skipped)> ReadAsync(
        string root, IEnumerable<string> files, int maxNotes, long maxBytes, CancellationToken cancellationToken)
    {
        var documents = new List<MemoryDocument>();
        int skipped = 0;
        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > maxBytes)
            {
                continue;
            }

            if (documents.Count >= maxNotes)
            {
                skipped++;
                continue;
            }

            string content;
            try
            {
                content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                continue; // Being written, or locked by a sync client: picked up on the next scan.
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            documents.Add(new MemoryDocument(Path.GetRelativePath(root, file).Replace('\\', '/'), content) { Modified = info.LastWriteTimeUtc });
        }

        return (documents, skipped);
    }

    /// <summary>Files with one of <paramref name="extensions"/> under <paramref name="root"/>, skipping dot-folders, in a stable order.</summary>
    internal static IEnumerable<string> EnumerateFiles(string root, IReadOnlySet<string> extensions)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = false, AttributesToSkip = FileAttributes.System };
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string file in Directory.EnumerateFiles(directory, "*", options).Order(StringComparer.Ordinal))
            {
                if (extensions.Contains(Path.GetExtension(file)))
                {
                    yield return file;
                }
            }

            foreach (string child in Directory.EnumerateDirectories(directory, "*", options).OrderDescending(StringComparer.Ordinal))
            {
                // .obsidian, .git, .trash and friends hold settings, history and deleted notes, not memory.
                if (!Path.GetFileName(child).StartsWith('.'))
                {
                    pending.Push(child);
                }
            }
        }
    }
}
