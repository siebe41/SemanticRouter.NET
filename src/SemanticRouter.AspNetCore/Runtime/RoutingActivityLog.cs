namespace SemanticRouter.AspNetCore;

/// <summary>One routed request, for the dashboard's activity view.</summary>
public sealed class RoutingActivityEntry
{
    /// <summary>Sequence number, increasing.</summary>
    public long Id { get; init; }

    /// <summary>When the request was routed.</summary>
    public DateTimeOffset Timestamp { get; init; }

    /// <summary>The client's protocol.</summary>
    public ChatProtocol Protocol { get; init; }

    /// <summary>The request path.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>The name of the gateway key the client presented, when gateway keys are configured.</summary>
    public string? Client { get; init; }

    /// <summary>The model the client asked for, before alias mapping.</summary>
    public string? RequestedModel { get; init; }

    /// <summary>The first 160 characters of the classified prompt.</summary>
    public string PromptPreview { get; init; } = string.Empty;

    /// <summary>The required tier.</summary>
    public int Tier { get; init; }

    /// <summary>Why the tier is what it is.</summary>
    public RoutingReason Reason { get; init; }

    /// <summary>The matched route, if any.</summary>
    public string? Route { get; init; }

    /// <summary>The route similarity, if computed.</summary>
    public float? Similarity { get; init; }

    /// <summary>The complexity score.</summary>
    public double Complexity { get; init; }

    /// <summary>Whether the request offered tools.</summary>
    public bool HasTools { get; init; }

    /// <summary>Whether the request continued a tool-calling turn.</summary>
    public bool ToolContinuation { get; init; }

    /// <summary>Whether only local models may serve it.</summary>
    public bool LocalOnly { get; init; }

    /// <summary>Estimated input tokens.</summary>
    public int EstimatedInputTokens { get; init; }

    /// <summary>The first-choice model.</summary>
    public string? PlannedModel { get; init; }

    /// <summary>Classification time in milliseconds.</summary>
    public double ClassificationMilliseconds { get; init; }

    /// <summary>The model that answered (Dispatch mode), once known.</summary>
    public string? ServedBy { get; set; }

    /// <summary>The connector that answered.</summary>
    public string? Connector { get; set; }

    /// <summary>Which attempt answered.</summary>
    public int? Attempt { get; set; }

    /// <summary>Whether the answering model was below the required tier.</summary>
    public bool Degraded { get; set; }

    /// <summary>What happened: <c>planned</c> (Annotate mode), <c>served</c> or <c>failed</c>.</summary>
    public string Outcome { get; set; } = "planned";

    /// <summary>Failover notes, when models were skipped.</summary>
    public string? Detail { get; set; }

    /// <summary>Total time from routing to the end of the response, in milliseconds.</summary>
    public double? TotalMilliseconds { get; set; }

    /// <summary>Time from routing until the first byte of the reply was sent, in milliseconds.</summary>
    public double? TimeToFirstByteMilliseconds { get; set; }

    /// <summary>Input tokens the backend reported (excluding cached ones).</summary>
    public int? InputTokens { get; set; }

    /// <summary>Output tokens the backend reported.</summary>
    public int? OutputTokens { get; set; }

    /// <summary>Input tokens read from the backend's prompt cache.</summary>
    public int? CacheReadTokens { get; set; }

    /// <summary>Input tokens written to the backend's prompt cache.</summary>
    public int? CacheCreationTokens { get; set; }

    /// <summary>Cost at the serving model's configured prices, when usage was reported.</summary>
    public decimal? CostUsd { get; set; }

    /// <summary>The routing profile the request ran under, if any.</summary>
    public string? Profile { get; set; }

    /// <summary>The project label (the project header, or the client's user id), if any.</summary>
    public string? Project { get; set; }

    /// <summary>Content signals that kept the request local (<c>secret</c>, <c>file path</c>), if any.</summary>
    public string? Signals { get; set; }

    /// <summary>The decision was reused from the classification cache (no embedding call).</summary>
    public bool Cached { get; set; }

    /// <summary>Budgets that kept a model from being tried, if any.</summary>
    public string? Budget { get; set; }

    /// <summary>Request features the serving backend could not carry (e.g. <c>reasoning</c>, <c>logprobs</c>), if any.</summary>
    public string? Dropped { get; set; }

    /// <summary>The W3C trace id of the request, for finding it in a tracing backend.</summary>
    public string? TraceId { get; set; }

    /// <summary>
    /// The models the request was sent to, in order (Dispatch mode). Failed attempts are included: the backend may have
    /// received the request before it failed.
    /// </summary>
    [System.Text.Json.Serialization.JsonObjectCreationHandling(System.Text.Json.Serialization.JsonObjectCreationHandling.Populate)]
    public IList<string> SentTo { get; } = new List<string>();

    /// <summary>The memory notes found in the request (titles and ids, never their text), when memory sources are configured.</summary>
    public IReadOnlyList<MemoryReference> Memory { get; init; } = [];
}

/// <summary>
/// Keeps routing activity beyond the in-memory window and across restarts. <see cref="RoutingActivityLog"/> appends
/// each entry once its request has finished and seeds itself from <see cref="Recent"/> on start.
/// </summary>
public interface IRoutingActivityStore
{
    /// <summary>Stores a finished entry. Must not throw for storage problems: log them and carry on.</summary>
    void Append(RoutingActivityEntry entry);

    /// <summary>The newest entries first, at most <paramref name="limit"/>.</summary>
    IReadOnlyList<RoutingActivityEntry> Recent(int limit);

    /// <summary>Entries with <paramref name="since"/> ≤ timestamp &lt; <paramref name="until"/>, oldest first, at most <paramref name="limit"/>.</summary>
    IReadOnlyList<RoutingActivityEntry> Between(DateTimeOffset since, DateTimeOffset until, int limit);
}

/// <summary>
/// A bounded in-memory log of recent routing decisions, optionally backed by an <see cref="IRoutingActivityStore"/>
/// so the dashboard still shows them after a restart.
/// </summary>
public sealed class RoutingActivityLog
{
    private readonly RoutingActivityEntry?[] _entries;
    private readonly IRoutingActivityStore? _store;
    private readonly Lock _gate = new();
    private long _next;

    /// <summary>Creates a log keeping the latest <paramref name="capacity"/> entries, seeded from <paramref name="store"/>.</summary>
    public RoutingActivityLog(int capacity = 500, IRoutingActivityStore? store = null)
    {
        _entries = new RoutingActivityEntry?[Math.Max(10, capacity)];
        _store = store;
        if (store?.Recent(_entries.Length) is { Count: > 0 } stored)
        {
            // Newest first. Ids may have gaps (a request that never finished), so the entries take consecutive slots
            // counting back from the newest id; new entries continue the sequence after it.
            RoutingActivityEntry[] newestFirst = [.. stored.OrderByDescending(e => e.Id)];
            long newest = newestFirst[0].Id;
            for (int rank = 0; rank < newestFirst.Length && newest - rank >= 0; rank++)
            {
                _entries[(newest - rank) % _entries.Length] = newestFirst[rank];
            }

            _next = newest + 1;
        }
    }

    /// <summary>
    /// Entries in a time range, oldest first: from the store when there is one (beyond the in-memory window),
    /// otherwise from memory.
    /// </summary>
    public IReadOnlyList<RoutingActivityEntry> Between(DateTimeOffset since, DateTimeOffset until, int limit = 100_000)
    {
        if (_store is not null)
        {
            return _store.Between(since, until, limit);
        }

        return Recent(_entries.Length).Where(e => e.Timestamp >= since && e.Timestamp < until).OrderBy(e => e.Id).Take(limit).ToArray();
    }

    /// <summary>The next sequence number.</summary>
    public long NextId
    {
        get
        {
            lock (_gate)
            {
                return _next;
            }
        }
    }

    /// <summary>
    /// Raised when an entry is added and again when its request finishes, for live views. Handlers run on the request's
    /// thread and must return quickly.
    /// </summary>
    public event Action<RoutingActivityEntry>? Updated;

    /// <summary>Adds an entry; its <see cref="RoutingActivityEntry.Id"/> must be unset.</summary>
    public RoutingActivityEntry Add(RoutingActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _entries[_next % _entries.Length] = entry;
            _next++;
        }

        Updated?.Invoke(entry);
        return entry;
    }

    /// <summary>
    /// Hands a finished entry (outcome and timing known) to the store, when there is one, and raises
    /// <see cref="Updated"/> for live views.
    /// </summary>
    public void Complete(RoutingActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _store?.Append(entry);
        Updated?.Invoke(entry);
    }

    /// <summary>Reserves the next id.</summary>
    public long Reserve()
    {
        lock (_gate)
        {
            return _next;
        }
    }

    /// <summary>The newest entries first, at most <paramref name="limit"/>, newer than <paramref name="after"/> when given.</summary>
    public IReadOnlyList<RoutingActivityEntry> Recent(int limit = 100, long? after = null)
    {
        lock (_gate)
        {
            var result = new List<RoutingActivityEntry>(Math.Min(limit, _entries.Length));
            for (long i = _next - 1; i >= 0 && i >= _next - _entries.Length && result.Count < limit; i--)
            {
                RoutingActivityEntry? entry = _entries[i % _entries.Length];
                if (entry is null || (after is { } a && entry.Id <= a))
                {
                    break;
                }

                result.Add(entry);
            }

            return result;
        }
    }
}
