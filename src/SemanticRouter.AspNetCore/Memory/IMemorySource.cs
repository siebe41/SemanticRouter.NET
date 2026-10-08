using System.Text.Json.Serialization;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// A store of notes the dashboard maps and the router watches for in requests: an Obsidian vault, Claude Code's
/// memory folder, a database of saved facts, an MCP memory server. Folders of Markdown are built in
/// (<see cref="MemorySourceType.Markdown"/>); register other kinds with <c>AddMemorySource</c>.
/// </summary>
public interface IMemorySource
{
    /// <summary>A short, unique name shown in the dashboard.</summary>
    string Name { get; }

    /// <summary>What kind of source this is, e.g. <c>Markdown</c>.</summary>
    string Kind { get; }

    /// <summary>Where the notes come from (a folder, a URL), for display; <see langword="null"/> when there is nothing to show.</summary>
    string? Location { get; }

    /// <summary>Reads every note. Called in the background on start-up, on refresh and when the configuration changes.</summary>
    Task<IReadOnlyList<MemoryDocument>> LoadAsync(CancellationToken cancellationToken);
}

/// <summary>A note as a source returns it. Links, tags and aliases are parsed from <see cref="Content"/> as Markdown.</summary>
/// <param name="Path">The note's path within its source, with <c>/</c> separators (e.g. <c>projects/router.md</c>). Unique per source.</param>
/// <param name="Content">The note's text.</param>
public sealed record MemoryDocument(string Path, string Content)
{
    /// <summary>The title; defaults to the file name without its extension (Obsidian's rule).</summary>
    public string? Title { get; init; }

    /// <summary>When the note last changed, if known.</summary>
    public DateTimeOffset? Modified { get; init; }

    /// <summary>Tags in addition to the ones written in <see cref="Content"/> (e.g. a knowledge-graph entity type).</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// Titles of notes this one links to, in addition to the links written in <see cref="Content"/> (e.g. a
    /// knowledge-graph relation). Resolved like <c>[[title]]</c> within the same source.
    /// </summary>
    public IReadOnlyList<string> Links { get; init; } = [];
}

/// <summary>How a note showed up in a request.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MemoryShareKind>))]
public enum MemoryShareKind
{
    /// <summary>The note's path or <c>[[title]]</c> was mentioned, but its text was not in the request.</summary>
    Reference = 0,

    /// <summary>Lines of the note's text were in the request: the model received (part of) the note.</summary>
    Content = 1,
}

/// <summary>A note found in a routed request.</summary>
/// <param name="Id">The note's id in the memory map (<c>{source}:{path}</c>).</param>
/// <param name="Title">The note's title.</param>
/// <param name="Source">The memory source's name.</param>
/// <param name="Kind">Whether the note's text was sent, or only mentioned.</param>
/// <param name="Lines">Distinctive lines of the note found in the request.</param>
/// <param name="TotalLines">Distinctive lines the note has.</param>
public sealed record MemoryReference(string Id, string Title, string Source, MemoryShareKind Kind, int Lines, int TotalLines);
