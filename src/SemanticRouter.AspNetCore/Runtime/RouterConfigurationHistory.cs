namespace SemanticRouter.AspNetCore;

/// <summary>One applied configuration document.</summary>
public sealed record RouterConfigurationVersion
{
    /// <summary>Increasing version id.</summary>
    public required long Id { get; init; }

    /// <summary>When it was applied.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>What applied it: <c>startup</c>, <c>admin API</c>, <c>reset</c>, <c>rollback</c>, …</summary>
    public required string Source { get; init; }

    /// <summary>Who applied it, when known (e.g. <c>local</c>, <c>admin token from 203.0.113.7</c>).</summary>
    public string? Actor { get; init; }

    /// <summary>Extra detail, e.g. <c>to version 12</c> or <c>not saved</c>.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Keeps every configuration document the router applied, so the dashboard can show what changed and roll back.
/// Documents are stored as applied: with <c>${…}</c> references where the configuration uses them, and with
/// literal keys where it does (as the configuration file itself would hold them).
/// </summary>
public interface IRouterConfigurationHistory
{
    /// <summary>Records <paramref name="document"/> and returns its version id.</summary>
    long Record(RouterConfiguration document, string source, string? actor = null, string? note = null);

    /// <summary>The newest versions first.</summary>
    IReadOnlyList<RouterConfigurationVersion> List(int limit = 50);

    /// <summary>The document of version <paramref name="id"/>, or <see langword="null"/> when it is not kept.</summary>
    RouterConfiguration? GetVersion(long id);
}

/// <summary>The default <see cref="IRouterConfigurationHistory"/>: the latest documents of this process, in memory.</summary>
public sealed class InMemoryRouterConfigurationHistory : IRouterConfigurationHistory
{
    private readonly LinkedList<(RouterConfigurationVersion Version, string Json)> _versions = new();
    private readonly int _capacity;
    private readonly Lock _gate = new();
    private long _next = 1;

    /// <summary>Keeps the latest 50 documents.</summary>
    public InMemoryRouterConfigurationHistory()
        : this(50)
    {
    }

    /// <summary>Keeps the latest <paramref name="capacity"/> documents.</summary>
    public InMemoryRouterConfigurationHistory(int capacity)
    {
        _capacity = Math.Max(2, capacity);
    }

    /// <inheritdoc />
    public long Record(RouterConfiguration document, string source, string? actor = null, string? note = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (_gate)
        {
            var version = new RouterConfigurationVersion
            {
                Id = _next++,
                Timestamp = DateTimeOffset.UtcNow,
                Source = source,
                Actor = actor,
                Note = note,
            };
            _versions.AddFirst((version, document.ToJson()));
            while (_versions.Count > _capacity)
            {
                _versions.RemoveLast();
            }

            return version.Id;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<RouterConfigurationVersion> List(int limit = 50)
    {
        lock (_gate)
        {
            return _versions.Take(Math.Max(0, limit)).Select(v => v.Version).ToArray();
        }
    }

    /// <inheritdoc />
    public RouterConfiguration? GetVersion(long id)
    {
        lock (_gate)
        {
            foreach ((RouterConfigurationVersion version, string json) in _versions)
            {
                if (version.Id == id)
                {
                    return RouterConfiguration.FromJson(json);
                }
            }

            return null;
        }
    }
}

/// <summary>
/// A change the host made to the stored configuration before the router loaded it (an upgrade to a newer template),
/// recorded with the start-up version in the configuration history.
/// </summary>
/// <param name="Source">The history entry's source, e.g. <c>upgrade</c>.</param>
/// <param name="Note">What changed, in words.</param>
internal sealed record StartupConfigurationNote(string Source, string Note);
