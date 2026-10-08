using System.Text.Json.Serialization;

namespace SemanticRouter.AspNetCore;

/// <summary>Built-in memory sources selectable from configuration.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MemorySourceType>))]
public enum MemorySourceType
{
    /// <summary>
    /// A folder of Markdown notes, read recursively: an Obsidian vault, a Logseq or Foam folder, Claude Code's
    /// <c>memory</c> directory, or a tree of <c>CLAUDE.md</c> files. <c>[[wikilinks]]</c>, Markdown links, frontmatter
    /// <c>aliases</c> and <c>tags</c>, and inline <c>#tags</c> are understood.
    /// </summary>
    Markdown = 0,

    /// <summary>
    /// Claude Code's own memory under <c>~/.claude</c> (or <see cref="MemorySourceOptions.Path"/>): the user-level
    /// <c>CLAUDE.md</c> and every project's auto-memory folder (<c>projects/{project}/memory/*.md</c>). For a
    /// repository's <c>CLAUDE.md</c> files, add the repository as a <see cref="Markdown"/> source.
    /// </summary>
    ClaudeCode = 1,

    /// <summary>
    /// The knowledge graph of the MCP memory server (<c>@modelcontextprotocol/server-memory</c>): a JSONL file of
    /// entities, observations and relations (its <c>MEMORY_FILE_PATH</c>, <c>memory.jsonl</c> by default). Each
    /// entity is a note whose text is its observations; relations are links; the entity type is a tag and a folder.
    /// </summary>
    KnowledgeGraph = 2,
}

/// <summary>
/// Memory sources the dashboard maps, and whether the router records which of their notes reach which model. Bound
/// from <c>SemanticRouter:Memory</c>. Note text never leaves the router: the dashboard gets titles, paths, tags and
/// links, and the activity log gets the titles of the notes a request carried.
/// </summary>
public sealed class MemoryOptions
{
    /// <summary>The memory sources, e.g. an Obsidian vault and Claude Code's memory folder.</summary>
    public IList<MemorySourceOptions> Sources { get; } = new List<MemorySourceOptions>();

    /// <summary>
    /// Look for the sources' notes in every routed request and record which models they were sent to. Default
    /// <see langword="true"/> (it only runs when a source has notes).
    /// </summary>
    public bool TrackSharing { get; set; } = true;

    /// <summary>
    /// Shortest line, after whitespace is collapsed, that counts as a fingerprint of a note's content. Shorter lines
    /// (headings, list stubs, frontmatter) are too common to say which note they came from. Default 24.
    /// </summary>
    public int MinimumLineLength { get; set; } = 24;

    /// <summary>How long a scan stays current before the next request triggers a background rescan. Default 1 minute.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Most notes read from one source; the rest are skipped and the dashboard says so. Default 5000.</summary>
    public int MaxNotesPerSource { get; set; } = 5000;

    /// <summary>Largest note read, in bytes; bigger files are skipped. Default 1 MB.</summary>
    public long MaxNoteBytes { get; set; } = 1024 * 1024;
}

/// <summary>One memory source.</summary>
public sealed class MemorySourceOptions
{
    /// <summary>A short name shown in the dashboard, e.g. <c>vault</c> or <c>claude-memory</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What kind of source this is. Default <see cref="MemorySourceType.Markdown"/>.</summary>
    public MemorySourceType Type { get; set; } = MemorySourceType.Markdown;

    /// <summary>
    /// The folder to read (a Markdown source), the <c>.claude</c> folder (a ClaudeCode source; default <c>~/.claude</c>)
    /// or the JSONL file (a KnowledgeGraph source; a folder is searched for <c>memory.jsonl</c>). <c>~</c> expands to
    /// the user's home directory and <c>%VARIABLE%</c> to environment variables; relative paths resolve against the
    /// current directory.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// File extensions read by a Markdown source. Default <c>.md</c>; add <c>.txt</c> for a folder written by the
    /// Claude API's memory tool, for example.
    /// </summary>
    public IList<string> Extensions { get; } = new List<string>();

    /// <summary>
    /// Relative paths to skip, as <c>*</c> globs (e.g. <c>Templates/*</c>, <c>*.excalidraw.md</c>). Folders whose name
    /// starts with a dot (<c>.obsidian</c>, <c>.git</c>, <c>.trash</c>) are always skipped.
    /// </summary>
    public IList<string> Exclude { get; } = new List<string>();

    /// <summary>Whether the source is read. Default <see langword="true"/>.</summary>
    public bool Enabled { get; set; } = true;
}
