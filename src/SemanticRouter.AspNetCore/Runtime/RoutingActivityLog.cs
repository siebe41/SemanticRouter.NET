namespace SemanticRouter.AspNetCore;

/// <summary>One routed request, for the dashboard's activity view.</summary>
public sealed class RoutingActivityEntry
{
    /// <summary>
    /// The entry's number: unique, also among routers sharing a store, and increasing within one router (not across
    /// routers sharing a store, which take numbers in blocks).
    /// </summary>
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

    /// <summary>The first 160 characters of the classified prompt, as <see cref="StateOptions.StorePrompts"/> allows (empty when off).</summary>
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

    /// <summary>A short hash identifying the conversation (conversation routing); never prompt text.</summary>
    public string? Conversation { get; init; }

    /// <summary>The inline callout that decided this request (<c>@opus</c>…), and whether it was made earlier in the conversation.</summary>
    public string? Callout { get; init; }

    /// <summary>The planned model is the conversation's previous model, kept ahead of the ranking.</summary>
    public bool Sticky { get; init; }

    /// <summary>Models left out of this decision as unreliable for this kind of work (route or tools), comma-separated.</summary>
    public string? Unreliable { get; init; }

    /// <summary>The learned change (automatic tuning) that routed this decision differently, when there is one.</summary>
    public long? Learned { get; init; }

    /// <summary>What <see cref="Learned"/> changed, e.g. "learned 3 Oct: threshold of complex-code 0.52 → 0.48".</summary>
    public string? LearnedNote { get; init; }

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

    /// <summary>
    /// Failover notes, when models were skipped. A failed CLI run's output is in them only as
    /// <see cref="StateOptions.StorePrompts"/> allows.
    /// </summary>
    public string? Detail { get; set; }

    /// <summary>Total time from routing to the end of the response, in milliseconds: what the client waited, failed attempts included.</summary>
    public double? TotalMilliseconds { get; set; }

    /// <summary>Time from routing until the first byte of the reply was sent, in milliseconds, failed attempts included.</summary>
    public double? TimeToFirstByteMilliseconds { get; set; }

    /// <summary>
    /// Time from the start of the serving attempt until the first byte of the reply was sent, in milliseconds: the serving
    /// model's own time to first byte, without the attempts that failed before it (Dispatch mode).
    /// </summary>
    public double? AttemptTimeToFirstByteMilliseconds { get; set; }

    /// <summary>Time from the start of the serving attempt to the end of its response, in milliseconds (Dispatch mode).</summary>
    public double? AttemptMilliseconds { get; set; }

    /// <summary>
    /// The serving model's own time to first byte and total time, for per-model and per-connector latency: the attempt's
    /// figures, or the request's for an entry recorded before they were kept. Never one of each, so total − first stays
    /// the time spent generating.
    /// </summary>
    internal (double? FirstByte, double? Total) ServingTimes() => AttemptMilliseconds is not null
        ? (AttemptTimeToFirstByteMilliseconds, AttemptMilliseconds)
        : (TimeToFirstByteMilliseconds, TotalMilliseconds);

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

    /// <summary>What the serving connector put on the record, e.g. <c>tool call repaired</c>, if anything.</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// How many of the reply's tool calls were read out of the model's text (prompted tool calling, a CLI connector),
    /// rather than returned by a native tool API; null when there were none.
    /// </summary>
    public int? PromptedToolCalls { get; set; }

    /// <summary>The W3C trace id of the request, for finding it in a tracing backend.</summary>
    public string? TraceId { get; set; }

    /// <summary>The judge's grade of the answer, 1 (wrong) to 5 (excellent), when it was graded (<see cref="JudgeOptions"/>).</summary>
    public int? Grade { get; set; }

    /// <summary>
    /// The model that graded the answer and its reason, e.g. <c>qwen3-32b: misses the second question</c>; the reason only
    /// as <see cref="StateOptions.StorePrompts"/> allows (none with <c>Off</c>).
    /// </summary>
    public string? GradeNote { get; set; }

    /// <summary>
    /// A hash of the prompt the judge graded, to find its hand label in the evaluation set (<see cref="JudgeAgreement"/>);
    /// no more of the prompt is kept than <see cref="PromptPreview"/>.
    /// </summary>
    public string? PromptKey { get; set; }

    /// <summary>Why a latency-aware strategy chose the model (<see cref="RoutingPlan.SelectionNote"/>).</summary>
    public string? Selection { get; init; }

    /// <summary>The learned change whose trial this turn was (a cheaper model tried on its route), when it was one.</summary>
    public long? Trial { get; init; }

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
    /// <summary>
    /// Stores a finished entry, or replaces it when it is stored again (<see cref="RoutingActivityLog.Revise"/>). Must not
    /// throw for storage problems: log them and carry on.
    /// </summary>
    void Append(RoutingActivityEntry entry);

    /// <summary>
    /// Stores an entry again after something was learned about it later (<see cref="RoutingActivityLog.Revise"/>),
    /// replacing what <see cref="Append"/> stored. Must not throw for storage problems.
    /// </summary>
    void Replace(RoutingActivityEntry entry) => Append(entry);

    /// <summary>
    /// Reserves <paramref name="count"/> consecutive entry ids that no other log sharing this store has had or will
    /// get, none below <paramref name="floor"/>, and returns the first; or <see langword="null"/> when the store does
    /// not hand out ids (it belongs to one log) or cannot right now.
    /// </summary>
    long? ReserveIds(int count, long floor) => null;

    /// <summary>The newest entries first, at most <paramref name="limit"/>.</summary>
    IReadOnlyList<RoutingActivityEntry> Recent(int limit);

    /// <summary>Entries with <paramref name="since"/> ≤ timestamp &lt; <paramref name="until"/>, oldest first, at most <paramref name="limit"/>.</summary>
    IReadOnlyList<RoutingActivityEntry> Between(DateTimeOffset since, DateTimeOffset until, int limit);

    /// <summary>
    /// Deletes the entries with <paramref name="since"/> ≤ timestamp &lt; <paramref name="until"/> so they cannot be
    /// recovered from the store's files, and returns how many there were.
    /// </summary>
    int Purge(DateTimeOffset since, DateTimeOffset until) => 0;
}

/// <summary>
/// A bounded in-memory log of recent routing decisions, optionally backed by an <see cref="IRoutingActivityStore"/>
/// so the dashboard still shows them after a restart.
/// </summary>
public sealed class RoutingActivityLog
{
    /// <summary>How many ids a log takes from a shared store at a time.</summary>
    internal const int IdBlock = 256;

    private readonly RoutingActivityEntry?[] _entries;
    private readonly IRoutingActivityStore? _store;
    private readonly Lock _gate = new();

    // _next counts slots of the ring; _nextId is the next entry id, and _idsEnd the end of the block of ids reserved
    // from a shared store.
    private long _next;
    private long _nextId;
    private long _idsEnd;

    /// <summary>Creates a log keeping the latest <paramref name="capacity"/> entries, seeded from <paramref name="store"/>.</summary>
    public RoutingActivityLog(int capacity = 500, IRoutingActivityStore? store = null)
    {
        _entries = new RoutingActivityEntry?[Math.Max(10, capacity)];
        _store = store;
        if (store?.Recent(_entries.Length) is { Count: > 0 } stored)
        {
            // Ids may have gaps (a request that never finished, another router's block), so the entries take
            // consecutive slots, oldest first; new ids continue after the newest.
            RoutingActivityEntry[] oldestFirst = [.. stored.OrderBy(e => e.Id)];
            foreach (RoutingActivityEntry entry in oldestFirst)
            {
                _entries[_next++ % _entries.Length] = entry;
            }

            _nextId = oldestFirst[^1].Id + 1;
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

    /// <summary>The next sequence number: every id this log handed out is below it.</summary>
    public long NextId
    {
        get
        {
            lock (_gate)
            {
                return _nextId;
            }
        }
    }

    /// <summary>
    /// Raised when an entry is added and again when its request finishes, for live views. Handlers run on the request's
    /// thread and must return quickly.
    /// </summary>
    public event Action<RoutingActivityEntry>? Updated;

    /// <summary>Adds an entry, with an id from <see cref="Reserve"/>.</summary>
    public RoutingActivityEntry Add(RoutingActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            _entries[_next % _entries.Length] = entry;
            _next++;
            _nextId = Math.Max(_nextId, entry.Id + 1);
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

    /// <summary>
    /// Stores a finished entry again after something was learned about it later (the judge's grade), and raises
    /// <see cref="Updated"/>.
    /// </summary>
    public void Revise(RoutingActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _store?.Replace(entry);
        Updated?.Invoke(entry);
    }

    /// <summary>
    /// Deletes the entries with <paramref name="since"/> ≤ timestamp &lt; <paramref name="until"/>, in memory and in the
    /// store, and returns how many the store held (or memory, without a store).
    /// </summary>
    public int Purge(DateTimeOffset since, DateTimeOffset until)
    {
        int removed = 0;
        lock (_gate)
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                if (_entries[i] is { } entry && entry.Timestamp >= since && entry.Timestamp < until)
                {
                    _entries[i] = null;
                    removed++;
                }
            }
        }

        return _store?.Purge(since, until) ?? removed;
    }

    /// <summary>
    /// Takes the next id for an entry. Each call returns a new one, also among logs sharing a store that hands out ids
    /// (<see cref="IRoutingActivityStore.ReserveIds"/>): those take them in blocks.
    /// </summary>
    public long Reserve()
    {
        lock (_gate)
        {
            if (_nextId >= _idsEnd)
            {
                // Once per block: other routers sharing the store may have taken the ids after this one's. A store
                // that hands out none (or cannot right now) is asked again after a block's worth.
                long? first = _store?.ReserveIds(IdBlock, _nextId);
                (_nextId, _idsEnd) = first is long start ? (Math.Max(start, _nextId), start + IdBlock) : (_nextId, _nextId + IdBlock);
            }

            return _nextId++;
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
                if (entry is null)
                {
                    continue; // never filled, or purged
                }

                if (after is { } a && entry.Id <= a)
                {
                    continue; // added in another order than its id was taken
                }

                result.Add(entry);
            }

            return result;
        }
    }
}
