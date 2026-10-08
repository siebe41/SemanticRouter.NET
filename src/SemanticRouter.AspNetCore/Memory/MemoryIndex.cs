using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace SemanticRouter.AspNetCore;

/// <summary>A memory source as last scanned.</summary>
/// <param name="Name">The source's name.</param>
/// <param name="Kind">The source's kind, e.g. <c>Markdown</c>.</param>
/// <param name="Location">Where it reads from.</param>
/// <param name="Notes">Notes read.</param>
/// <param name="Skipped">Notes left out because the source holds more than the limit.</param>
/// <param name="Error">Why the source could not be read, if it could not.</param>
public sealed record MemorySourceStatus(string Name, string Kind, string? Location, int Notes, int Skipped, string? Error);

/// <summary>A note (or, when <see cref="Missing"/>, a link target with no note behind it) in the memory map.</summary>
internal sealed class MemoryNoteInfo
{
    public required string Id { get; init; }

    public required string Source { get; init; }

    public required string Path { get; init; }

    public required string Title { get; init; }

    public string Folder { get; init; } = string.Empty;

    public IReadOnlyList<string> Aliases { get; init; } = [];

    public IReadOnlyList<string> Tags { get; init; } = [];

    public int Size { get; init; }

    public DateTimeOffset? Modified { get; init; }

    /// <summary>Distinctive lines: the fingerprints that identify this note's text in a request.</summary>
    public int Fingerprints { get; set; }

    /// <summary>A <c>[[link]]</c> target that no note answers to (Obsidian draws these as ghost nodes).</summary>
    public bool Missing { get; init; }
}

/// <summary>One scan of every memory source: the graph and the lookups used to spot notes in requests.</summary>
internal sealed class MemorySnapshot
{
    public static MemorySnapshot Empty { get; } = new();

    public IReadOnlyList<MemoryNoteInfo> Notes { get; init; } = [];

    public IReadOnlyList<(int From, int To)> Links { get; init; } = [];

    public IReadOnlyList<MemorySourceStatus> Sources { get; init; } = [];

    public DateTimeOffset? LoadedAt { get; init; }

    public double LoadMilliseconds { get; init; }

    /// <summary>The settings this scan was made with; a different key means the configuration changed.</summary>
    public string ConfigKey { get; init; } = string.Empty;

    public int MinimumLineLength { get; init; } = 24;

    /// <summary>Line fingerprint to the notes containing that line (lines shared by many notes are left out).</summary>
    public IReadOnlyDictionary<ulong, int[]> Lines { get; init; } = new Dictionary<ulong, int[]>();

    /// <summary>File name (<c>team.md</c>, lower case) to notes.</summary>
    public IReadOnlyDictionary<string, int[]> FileNames { get; init; } = new Dictionary<string, int[]>();

    /// <summary>Title or alias (lower case) to notes.</summary>
    public IReadOnlyDictionary<string, int[]> Titles { get; init; } = new Dictionary<string, int[]>();

    public bool HasNotes => Notes.Count > 0 && Notes.Any(n => !n.Missing);
}

/// <summary>
/// Scans the memory sources (the configured folders plus any registered <see cref="IMemorySource"/>) into a graph of
/// notes and links, and finds those notes in routed requests: by content (distinctive lines of the note, including
/// lines Claude Code's Read tool numbered) and by reference (the note's path or a <c>[[title]]</c>). Scans run in the
/// background; the router keeps using the previous scan until the next one is ready.
/// </summary>
internal sealed partial class MemoryIndex : IDisposable
{
    /// <summary>Lines found in more notes than this are boilerplate (templates, footers), not evidence of one note.</summary>
    private const int MaxNotesPerLine = 3;

    /// <summary>Longest line fingerprinted; longer strings are payloads (base64 images), not note text.</summary>
    private const int MaxLineLength = 8000;

    /// <summary>Most notes reported for one request.</summary>
    private const int MaxReferences = 50;

    private static readonly JsonSerializerOptions KeyJson = new();

    private readonly RouterRuntimeProvider _runtime;
    private readonly IMemorySource[] _registered;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private MemorySnapshot _current = MemorySnapshot.Empty;
    private Task<MemorySnapshot>? _loading;
    private (MemoryOptions? Options, string Key) _key;

    public MemoryIndex(
        RouterRuntimeProvider runtime, IEnumerable<IMemorySource> registered, ILogger<MemoryIndex> logger, TimeProvider? time = null)
    {
        _runtime = runtime;
        _registered = registered.ToArray();
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _runtime.Changed += OnRuntimeChanged;
    }

    /// <summary>The latest completed scan (empty until the first one finishes).</summary>
    public MemorySnapshot Current => Volatile.Read(ref _current);

    /// <summary>Starts a background rescan when the configuration changed or the last scan is older than the refresh interval.</summary>
    public void EnsureFresh()
    {
        MemoryOptions options = _runtime.Current.Configuration.Memory;
        MemorySnapshot snapshot = Current;
        bool stale = snapshot.LoadedAt is not { } loaded
            || snapshot.ConfigKey != KeyFor(options)
            || _time.GetUtcNow() - loaded >= options.RefreshInterval;
        if (stale)
        {
            _ = RefreshAsync(CancellationToken.None);
        }
    }

    /// <summary>Rescans every source; joins a scan already running.</summary>
    public Task<MemorySnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_loading is { IsCompleted: false } running)
            {
                return running.WaitAsync(cancellationToken);
            }

            MemoryOptions options = _runtime.Current.Configuration.Memory;
            _loading = Task.Run(() => LoadAsync(options), CancellationToken.None);
            return _loading.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// The notes in a request body: every string in the JSON (system prompt, messages, tool results) is scanned, so it
    /// works the same for both protocols and for whatever a client injects.
    /// </summary>
    public IReadOnlyList<MemoryReference> Match(JsonElement body)
    {
        MemorySnapshot snapshot = Current;
        if (!snapshot.HasNotes)
        {
            return [];
        }

        var scan = new RequestScan(snapshot);
        scan.Walk(body);
        return scan.Results();
    }

    public void Dispose() => _runtime.Changed -= OnRuntimeChanged;

    private void OnRuntimeChanged(RouterRuntime runtime)
    {
        if (Current.ConfigKey != ConfigKey(runtime.Configuration.Memory))
        {
            _ = RefreshAsync(CancellationToken.None);
        }
    }

    private static string ConfigKey(MemoryOptions options) => JsonSerializer.Serialize(options, KeyJson);

    /// <summary>The configuration key, computed once per applied configuration (this runs on every request).</summary>
    private string KeyFor(MemoryOptions options)
    {
        (MemoryOptions? Options, string Key) cached = _key;
        if (ReferenceEquals(cached.Options, options))
        {
            return cached.Key;
        }

        string key = ConfigKey(options);
        _key = (options, key);
        return key;
    }

    private static IMemorySource? Create(MemorySourceOptions source, MemoryOptions options) => source.Type switch
    {
        MemorySourceType.ClaudeCode => new ClaudeCodeMemorySource(source.Name, source.Path, options.MaxNotesPerSource, options.MaxNoteBytes),
        MemorySourceType.KnowledgeGraph when !string.IsNullOrWhiteSpace(source.Path) => new KnowledgeGraphMemorySource(source.Name, source.Path),
        MemorySourceType.Markdown when !string.IsNullOrWhiteSpace(source.Path) =>
            new MarkdownMemorySource(source.Name, source.Path, source.Exclude, options.MaxNotesPerSource, options.MaxNoteBytes, source.Extensions),
        _ => null,
    };

    private async Task<MemorySnapshot> LoadAsync(MemoryOptions options)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var sources = new List<IMemorySource>();
        foreach (MemorySourceOptions configured in options.Sources.Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Name)))
        {
            if (Create(configured, options) is { } source)
            {
                sources.Add(source);
            }
        }

        sources.AddRange(_registered);

        var builder = new SnapshotBuilder(options.MinimumLineLength);
        var statuses = new List<MemorySourceStatus>();
        foreach (IMemorySource source in sources)
        {
            try
            {
                IReadOnlyList<MemoryDocument> documents = await source.LoadAsync(CancellationToken.None).ConfigureAwait(false);
                int added = builder.Add(source.Name, documents);
                int skipped = source switch
                {
                    MarkdownMemorySource markdown => markdown.Skipped,
                    ClaudeCodeMemorySource claude => claude.Skipped,
                    _ => 0,
                };
                statuses.Add(new MemorySourceStatus(source.Name, source.Kind, source.Location, added, skipped, null));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogSourceFailed(_logger, source.Name, ex);
                statuses.Add(new MemorySourceStatus(source.Name, source.Kind, source.Location, 0, 0, ex.Message));
            }
        }

        MemorySnapshot snapshot = builder.Build(statuses, _time.GetUtcNow(), ConfigKey(options), System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        Volatile.Write(ref _current, snapshot);
        if (sources.Count > 0)
        {
            LogLoaded(_logger, snapshot.Notes.Count(n => !n.Missing), snapshot.Links.Count, sources.Count, snapshot.LoadMilliseconds);
        }

        return snapshot;
    }

    /// <summary>
    /// A line's fingerprint: FNV-1a over its text with runs of whitespace collapsed, after removing the line-number
    /// prefix that file-reading tools add (<c>   12→</c> or <c>   12\t</c>). Returns false for lines too short to say
    /// which note they came from.
    /// </summary>
    internal static bool TryFingerprint(ReadOnlySpan<char> line, int minimumLength, out ulong hash)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        hash = offset;
        if (line.Length > MaxLineLength)
        {
            return false;
        }

        line = StripLineNumber(line).Trim();
        int length = 0;
        bool pendingSpace = false;
        foreach (char c in line)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace)
            {
                hash = (hash ^ ' ') * prime;
                length++;
                pendingSpace = false;
            }

            hash = (hash ^ c) * prime;
            length++;
        }

        return length >= minimumLength;
    }

    private static ReadOnlySpan<char> StripLineNumber(ReadOnlySpan<char> line)
    {
        int i = 0;
        while (i < line.Length && line[i] == ' ')
        {
            i++;
        }

        int digits = i;
        while (i < line.Length && char.IsAsciiDigit(line[i]))
        {
            i++;
        }

        return i > digits && i < line.Length && line[i] is '\t' or '→' ? line[(i + 1)..] : line;
    }

    /// <summary>Collects notes, resolves links per source and builds the lookups.</summary>
    private sealed class SnapshotBuilder(int minimumLineLength)
    {
        private readonly List<MemoryNoteInfo> _notes = [];
        private readonly List<HashSet<ulong>> _fingerprints = [];
        private readonly HashSet<(int, int)> _links = [];
        private readonly Dictionary<string, int> _ghosts = new(StringComparer.Ordinal);

        public int Add(string source, IReadOnlyList<MemoryDocument> documents)
        {
            int first = _notes.Count;
            var parsed = new List<ParsedNote>(documents.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MemoryDocument document in documents)
            {
                string path = document.Path.Replace('\\', '/').TrimStart('/');
                if (path.Length == 0 || !seen.Add(path))
                {
                    continue;
                }

                ParsedNote note = MarkdownNoteParser.Parse(path, document.Content, document.Title);
                if (document.Tags.Count > 0 || document.Links.Count > 0)
                {
                    note = note with
                    {
                        Tags = note.Tags.Concat(document.Tags).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                        Links = note.Links.Concat(document.Links.Select(l => new NoteLink(l, IsPath: false))).ToArray(),
                    };
                }

                parsed.Add(note);
                int slash = path.LastIndexOf('/');
                _notes.Add(new MemoryNoteInfo
                {
                    Id = source + ":" + path,
                    Source = source,
                    Path = path,
                    Title = note.Title,
                    Folder = slash > 0 ? path[..slash] : string.Empty,
                    Aliases = note.Aliases,
                    Tags = note.Tags,
                    Size = document.Content.Length,
                    Modified = document.Modified,
                });

                var fingerprints = new HashSet<ulong>();
                foreach (ReadOnlySpan<char> line in document.Content.AsSpan().EnumerateLines())
                {
                    if (TryFingerprint(line, minimumLineLength, out ulong hash))
                    {
                        fingerprints.Add(hash);
                    }
                }

                _fingerprints.Add(fingerprints);
            }

            ResolveLinks(source, first, parsed);
            return parsed.Count;
        }

        public MemorySnapshot Build(IReadOnlyList<MemorySourceStatus> sources, DateTimeOffset loadedAt, string configKey, double milliseconds)
        {
            var lines = new Dictionary<ulong, List<int>>();
            for (int i = 0; i < _fingerprints.Count; i++)
            {
                foreach (ulong hash in _fingerprints[i])
                {
                    if (!lines.TryGetValue(hash, out List<int>? owners))
                    {
                        lines[hash] = owners = [];
                    }

                    owners.Add(i);
                }
            }

            var distinctive = new Dictionary<ulong, int[]>(lines.Count);
            foreach ((ulong hash, List<int> owners) in lines)
            {
                if (owners.Count <= MaxNotesPerLine)
                {
                    distinctive[hash] = owners.ToArray();
                    foreach (int owner in owners)
                    {
                        _notes[owner].Fingerprints++;
                    }
                }
            }

            var fileNames = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            var titles = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < _notes.Count; i++)
            {
                MemoryNoteInfo note = _notes[i];
                if (note.Missing)
                {
                    continue;
                }

                AddTo(fileNames, System.IO.Path.GetFileName(note.Path).ToLowerInvariant(), i);
                AddTo(titles, note.Title.ToLowerInvariant(), i);
                foreach (string alias in note.Aliases)
                {
                    AddTo(titles, alias.ToLowerInvariant(), i);
                }
            }

            return new MemorySnapshot
            {
                Notes = _notes,
                Links = _links.ToArray(),
                Sources = sources,
                LoadedAt = loadedAt,
                LoadMilliseconds = Math.Round(milliseconds, 1),
                ConfigKey = configKey,
                MinimumLineLength = minimumLineLength,
                Lines = distinctive,
                FileNames = fileNames.ToDictionary(kv => kv.Key, kv => kv.Value.Distinct().ToArray(), StringComparer.Ordinal),
                Titles = titles.ToDictionary(kv => kv.Key, kv => kv.Value.Distinct().ToArray(), StringComparer.Ordinal),
            };
        }

        private static void AddTo(Dictionary<string, List<int>> map, string key, int index)
        {
            if (!map.TryGetValue(key, out List<int>? list))
            {
                map[key] = list = [];
            }

            list.Add(index);
        }

        /// <summary>Resolves links within one source, the way Obsidian does: relative path, vault path, then title or alias.</summary>
        private void ResolveLinks(string source, int first, List<ParsedNote> parsed)
        {
            var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var byTitle = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < parsed.Count; i++)
            {
                MemoryNoteInfo note = _notes[first + i];
                byPath.TryAdd(MarkdownNoteParser.StripMd(note.Path), first + i);
                byTitle.TryAdd(System.IO.Path.GetFileNameWithoutExtension(note.Path), first + i);
                byTitle.TryAdd(note.Title, first + i);
                foreach (string alias in note.Aliases)
                {
                    byTitle.TryAdd(alias, first + i);
                }
            }

            for (int i = 0; i < parsed.Count; i++)
            {
                int from = first + i;
                string folder = _notes[from].Folder;
                foreach (NoteLink link in parsed[i].Links)
                {
                    string target = MarkdownNoteParser.StripMd(link.Target.Replace('\\', '/')).Trim();
                    if (target.Length == 0)
                    {
                        continue;
                    }

                    int? to = null;
                    if (link.IsPath)
                    {
                        foreach (string candidate in new[] { Combine(folder, target), target.TrimStart('/') })
                        {
                            if (byPath.TryGetValue(candidate, out int hit))
                            {
                                to = hit;
                                break;
                            }
                        }

                        to ??= byPath.FirstOrDefault(kv => kv.Key.EndsWith("/" + target.TrimStart('/'), StringComparison.OrdinalIgnoreCase)) is { Key: not null } suffix
                            ? suffix.Value
                            : null;
                    }

                    if (to is null && byTitle.TryGetValue(System.IO.Path.GetFileName(target), out int byName))
                    {
                        to = byName;
                    }

                    to ??= Ghost(source, System.IO.Path.GetFileName(target));
                    if (to.Value != from)
                    {
                        _links.Add((from, to.Value));
                    }
                }
            }
        }

        private int Ghost(string source, string title)
        {
            string id = source + ":?" + title.ToLowerInvariant();
            if (!_ghosts.TryGetValue(id, out int index))
            {
                index = _notes.Count;
                _ghosts[id] = index;
                _notes.Add(new MemoryNoteInfo { Id = id, Source = source, Path = string.Empty, Title = title, Missing = true });
                _fingerprints.Add([]);
            }

            return index;
        }

        private static string Combine(string folder, string relative)
        {
            var parts = new List<string>(folder.Length == 0 ? [] : folder.Split('/'));
            foreach (string part in relative.Split('/'))
            {
                if (part is "" or ".")
                {
                    continue;
                }

                if (part == "..")
                {
                    if (parts.Count > 0)
                    {
                        parts.RemoveAt(parts.Count - 1);
                    }
                }
                else
                {
                    parts.Add(part);
                }
            }

            return string.Join('/', parts);
        }
    }

    /// <summary>Walks one request body, counting fingerprint hits per note and collecting path and title mentions.</summary>
    private sealed partial class RequestScan(MemorySnapshot snapshot)
    {
        private readonly Dictionary<int, int> _lineHits = [];
        private readonly HashSet<ulong> _seenLines = [];
        private readonly HashSet<int> _mentioned = [];

        public void Walk(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        Walk(property.Value);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        Walk(item);
                    }

                    break;
                case JsonValueKind.String:
                    Scan(element.GetString());
                    break;
            }
        }

        public MemoryReference[] Results()
        {
            var results = new List<MemoryReference>();
            foreach ((int index, int hits) in _lineHits)
            {
                MemoryNoteInfo note = snapshot.Notes[index];
                // One distinctive line is enough: lines that occur in several notes were already left out, and a single
                // fact (a knowledge-graph observation, a line of CLAUDE.md) is exactly what this is meant to catch.
                if (hits > 0)
                {
                    results.Add(new MemoryReference(note.Id, note.Title, note.Source, MemoryShareKind.Content, hits, note.Fingerprints));
                }
            }

            foreach (int index in _mentioned)
            {
                MemoryNoteInfo note = snapshot.Notes[index];
                if (!results.Any(r => r.Id == note.Id))
                {
                    results.Add(new MemoryReference(note.Id, note.Title, note.Source, MemoryShareKind.Reference, 0, note.Fingerprints));
                }
            }

            return results
                .OrderByDescending(r => r.Kind)
                .ThenByDescending(r => r.Lines)
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .Take(MaxReferences)
                .ToArray();
        }

        private void Scan(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            foreach (ReadOnlySpan<char> line in text.AsSpan().EnumerateLines())
            {
                if (TryFingerprint(line, snapshot.MinimumLineLength, out ulong hash)
                    && _seenLines.Add(hash)
                    && snapshot.Lines.TryGetValue(hash, out int[]? owners))
                {
                    foreach (int owner in owners)
                    {
                        _lineHits[owner] = _lineHits.GetValueOrDefault(owner) + 1;
                    }
                }

                if (line.Length <= MaxLineLength)
                {
                    if (line.Contains(".md", StringComparison.OrdinalIgnoreCase))
                    {
                        MatchPaths(line.ToString());
                    }

                    if (line.Contains("[[", StringComparison.Ordinal))
                    {
                        MatchWikiLinks(line.ToString());
                    }
                }
            }
        }

        private void MatchPaths(string line)
        {
            foreach (Match match in MentionedPath().Matches(line))
            {
                string path = match.Value.Replace('\\', '/');
                string name = System.IO.Path.GetFileName(path).ToLowerInvariant();
                if (!snapshot.FileNames.TryGetValue(name, out int[]? candidates))
                {
                    continue;
                }

                if (candidates.Length == 1)
                {
                    _mentioned.Add(candidates[0]);
                    continue;
                }

                // Several notes share the file name: only a path that ends with one note's own path picks it.
                foreach (int candidate in candidates)
                {
                    string own = snapshot.Notes[candidate].Path;
                    if (path.EndsWith("/" + own, StringComparison.OrdinalIgnoreCase) || path.Equals(own, StringComparison.OrdinalIgnoreCase))
                    {
                        _mentioned.Add(candidate);
                    }
                }
            }
        }

        private void MatchWikiLinks(string line)
        {
            foreach (Match match in MentionedWikiLink().Matches(line))
            {
                string title = MarkdownNoteParser.StripMd(match.Groups["target"].Value.Trim());
                title = title[(title.LastIndexOf('/') + 1)..].ToLowerInvariant();
                if (snapshot.Titles.TryGetValue(title, out int[]? notes))
                {
                    foreach (int note in notes)
                    {
                        _mentioned.Add(note);
                    }
                }
            }
        }

        [GeneratedRegex(@"[^\s""'`<>()\[\]|*?,;:]*[^\s""'`<>()\[\]|*?,;:/\\]\.md(?![\w])", RegexOptions.IgnoreCase)]
        private static partial Regex MentionedPath();

        [GeneratedRegex(@"\[\[(?<target>[^\[\]|#^]+)")]
        private static partial Regex MentionedWikiLink();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Memory map: {Notes} notes, {Links} links from {Sources} source(s) in {ElapsedMs:0} ms.")]
    private static partial void LogLoaded(ILogger logger, int notes, int links, int sources, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Memory source '{Source}' could not be read.")]
    private static partial void LogSourceFailed(ILogger logger, string source, Exception exception);
}
