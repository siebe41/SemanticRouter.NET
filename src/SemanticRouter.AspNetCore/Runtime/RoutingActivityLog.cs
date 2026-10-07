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
}

/// <summary>A bounded in-memory log of recent routing decisions.</summary>
public sealed class RoutingActivityLog
{
    private readonly RoutingActivityEntry?[] _entries;
    private readonly Lock _gate = new();
    private long _next;

    /// <summary>Creates a log keeping the latest <paramref name="capacity"/> entries.</summary>
    public RoutingActivityLog(int capacity = 500)
    {
        _entries = new RoutingActivityEntry?[Math.Max(10, capacity)];
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

    /// <summary>Adds an entry; its <see cref="RoutingActivityEntry.Id"/> must be unset.</summary>
    public RoutingActivityEntry Add(RoutingActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _entries[_next % _entries.Length] = entry;
            _next++;
            return entry;
        }
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
