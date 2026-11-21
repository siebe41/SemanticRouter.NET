using System.Collections.Concurrent;
using System.Globalization;

namespace SemanticRouter.AspNetCore;

/// <summary>Whether a request may go ahead (<see cref="ClientRateLimiter.TryAdmit"/>), and if not, when to retry and why.</summary>
/// <param name="Admitted">Whether it may go ahead.</param>
/// <param name="RetryAfter">How long until it would be admitted, when it was not.</param>
/// <param name="Detail">Which limit the client is over, when it was not.</param>
internal readonly record struct RateLimitDecision(bool Admitted, TimeSpan RetryAfter, string? Detail);

/// <summary>A client's rate limit and what it used in the last minute, for the dashboard.</summary>
internal sealed record RateLimitStatus
{
    /// <summary>The client: a gateway client name, or the address of a client without a key.</summary>
    public required string Client { get; init; }

    /// <summary>The entry that applies: the client's name or <c>*</c>.</summary>
    public required string Limit { get; init; }

    /// <summary>The request limit, if any.</summary>
    public int? RequestsPerMinute { get; init; }

    /// <summary>The token limit, if any.</summary>
    public int? TokensPerMinute { get; init; }

    /// <summary>Requests admitted in the last minute.</summary>
    public required int Requests { get; init; }

    /// <summary>Tokens charged in the last minute.</summary>
    public required long Tokens { get; init; }

    /// <summary>Requests turned away since the router started.</summary>
    public required long Limited { get; init; }

    /// <summary>When the last one was turned away.</summary>
    public DateTimeOffset? LastLimited { get; init; }
}

/// <summary>
/// Per-client rate limits (<see cref="PromptRouterMiddlewareOptions.RateLimits"/>) over a sliding minute: requests per
/// minute and tokens per minute. A request is charged its estimated input tokens when admitted and the difference to its
/// real usage when the reply is in. Kept in memory across configuration reloads, so changing a limit takes effect at once
/// without resetting what clients used. A client without a gateway key is counted by its address (an IPv6 address by
/// its /64, <see cref="ClientAddressKey"/>). At most <see cref="MaxClients"/> clients are kept: those unseen for an hour
/// go, and beyond that the least recently seen (<see cref="TrackerPruning"/>), so a stream of addresses costs constant
/// work each.
/// </summary>
internal sealed class ClientRateLimiter
{
    /// <summary>The entry that applies to every client without its own.</summary>
    public const string Everyone = "*";

    /// <summary>The name shown for <see cref="PromptRouterMiddlewareOptions.RemoteRequestsPerMinute"/>.</summary>
    public const string RemoteDefault = "remote default";

    /// <summary>The most clients kept.</summary>
    public const int MaxClients = 10_000;

    private const int SweepAbove = 1000;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    // A client unseen for this long is forgotten, with its count of requests turned away.
    private static readonly TimeSpan IdleAfter = TimeSpan.FromHours(1);

    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Usage> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly TrackerPruning _pruning;
    private readonly Lock _gate = new();

    public ClientRateLimiter(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _pruning = new TrackerPruning(MaxClients, SweepAbove, _time);
    }

    /// <summary>The clients kept, for tests.</summary>
    internal int Count => _clients.Count;

    /// <summary>The table's pruning, for tests of the cost per write.</summary>
    internal TrackerPruning Pruning => _pruning;

    /// <summary>The limit for <paramref name="client"/>: its own entry, else <c>*</c>; <see langword="null"/> when none applies.</summary>
    public static (string Name, RateLimitOptions Limit)? LimitFor(IDictionary<string, RateLimitOptions> limits, string? client)
    {
        if (client is not null && limits.TryGetValue(client, out RateLimitOptions? own))
        {
            return (client, own);
        }

        return limits.TryGetValue(Everyone, out RateLimitOptions? everyone) ? (Everyone, everyone) : null;
    }

    /// <summary>
    /// The limit for <paramref name="client"/>: its own entry, else <c>*</c>, else for a client on another machine
    /// <see cref="PromptRouterMiddlewareOptions.RemoteRequestsPerMinute"/>; <see langword="null"/> when none applies.
    /// </summary>
    public static (string Name, RateLimitOptions Limit)? LimitFor(PromptRouterMiddlewareOptions options, string? client, bool remote) =>
        LimitFor(options.RateLimits, client)
        ?? (remote && options.RemoteRequestsPerMinute > 0
            ? (RemoteDefault, new RateLimitOptions { RequestsPerMinute = options.RemoteRequestsPerMinute })
            : null);

    /// <summary>Admits a request from <paramref name="client"/> and charges it, or says when to retry.</summary>
    public RateLimitDecision TryAdmit(string client, RateLimitOptions limit, int estimatedTokens, bool remote = false)
    {
        RateLimitDecision decision = Admit(client, limit, estimatedTokens, remote);
        _pruning.Written(_clients, static u => u.LastSeen, IdleAfter);
        return decision;
    }

    /// <summary>Charges <paramref name="client"/> the difference between a request's real usage and its estimate (may be negative).</summary>
    public void Charge(string client, long tokens)
    {
        if (tokens == 0)
        {
            return;
        }

        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            Usage usage = UsageOf(client, now);
            if (tokens > 0)
            {
                usage.Add(new Entry(now, tokens, Request: false));
            }
            else
            {
                usage.Refund(-tokens, now);
            }
        }

        _pruning.Written(_clients, static u => u.LastSeen, IdleAfter);
    }

    private RateLimitDecision Admit(string client, RateLimitOptions limit, int estimatedTokens, bool remote)
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            Usage usage = UsageOf(client, now);
            usage.Remote |= remote;
            TimeSpan wait = TimeSpan.Zero;
            string? detail = null;
            if (limit.RequestsPerMinute is { } requests && usage.Requests >= requests)
            {
                // Admitted once enough of the oldest requests have left the window.
                wait = usage.Entries.Where(e => e.Request).Skip(usage.Requests - requests).First().At + Window - now;
                detail = string.Create(CultureInfo.InvariantCulture, $"its limit of {requests} requests per minute");
            }

            if (limit.TokensPerMinute is { } tokens && usage.Tokens >= tokens)
            {
                TimeSpan tokenWait = UntilBelow(usage, tokens, now);
                if (tokenWait > wait)
                {
                    wait = tokenWait;
                }

                detail = detail is null
                    ? string.Create(CultureInfo.InvariantCulture, $"its limit of {tokens:N0} tokens per minute")
                    : string.Create(CultureInfo.InvariantCulture, $"{detail} and {tokens:N0} tokens per minute");
            }

            if (detail is not null)
            {
                usage.Limited++;
                usage.LastLimited = usage.LastSeen = now;
                return new RateLimitDecision(false, wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait, detail);
            }

            usage.Add(new Entry(now, Math.Max(0, estimatedTokens), Request: true));
            return new RateLimitDecision(true, TimeSpan.Zero, null);
        }
    }

    /// <summary>Every client seen whose limit still applies, with what it used in the last minute.</summary>
    public IReadOnlyList<RateLimitStatus> Status(PromptRouterMiddlewareOptions options)
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            var status = new List<RateLimitStatus>();
            foreach ((string client, Usage usage) in _clients.Where(c => !c.Value.Retired).OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
            {
                usage.Prune(now);
                if (LimitFor(options, client, usage.Remote) is { } limit)
                {
                    status.Add(new RateLimitStatus
                    {
                        Client = client,
                        Limit = limit.Name,
                        RequestsPerMinute = limit.Limit.RequestsPerMinute,
                        TokensPerMinute = limit.Limit.TokensPerMinute,
                        Requests = usage.Requests,
                        Tokens = usage.Tokens,
                        Limited = usage.Limited,
                        LastLimited = usage.LastLimited,
                    });
                }
            }

            return status;
        }
    }

    /// <summary>How long until the tokens charged in the window drop below <paramref name="limit"/>.</summary>
    private static TimeSpan UntilBelow(Usage usage, long limit, DateTimeOffset now)
    {
        long remaining = usage.Tokens;
        foreach (Entry entry in usage.Entries)
        {
            remaining -= entry.Tokens;
            if (remaining < limit)
            {
                return entry.At + Window - now;
            }
        }

        return Window;
    }

    private Usage UsageOf(string client, DateTimeOffset now)
    {
        // Clients without a key are counted per address; the pruning after each write keeps the table bounded. Seen from
        // the start, so a sweep running beside it cannot take the new entry for an idle one. Called holding _gate, which
        // the pruning holds to retire an entry: one it retired is not written to (the write would be lost with it).
        Usage usage;
        while ((usage = _clients.GetOrAdd(client, static (_, state) => new Usage(state.Gate) { LastSeen = state.At }, (Gate: _gate, At: now))).Retired)
        {
            _clients.TryRemove(new KeyValuePair<string, Usage>(client, usage));
        }

        usage.Prune(now);
        return usage;
    }

    private readonly record struct Entry(DateTimeOffset At, long Tokens, bool Request);

    /// <summary>One client's use in the window. Changed only holding the limiter's lock (<paramref name="gate"/>).</summary>
    private sealed class Usage(Lock gate) : IPrunableEntry
    {
        private long _lastSeenTicks;

        /// <summary>Whether the pruning took this entry out: it is replaced, not written to.</summary>
        public bool Retired { get; private set; }

        public bool TryRetire(DateTimeOffset seen)
        {
            lock (gate)
            {
                Retired |= LastSeen <= seen;
                return Retired;
            }
        }

        public Queue<Entry> Entries { get; } = new();

        public int Requests { get; private set; }

        public long Tokens { get; private set; }

        public long Limited { get; set; }

        /// <summary>Whether the client has sent requests from another machine.</summary>
        public bool Remote { get; set; }

        public DateTimeOffset? LastLimited { get; set; }

        /// <summary>
        /// When the client was last seen: its last request, charge or refusal. Written under the limiter's lock, read
        /// without it by the pruning, so it is kept as ticks that are read and written whole.
        /// </summary>
        public DateTimeOffset LastSeen
        {
            get => new(Interlocked.Read(ref _lastSeenTicks), TimeSpan.Zero);
            set => Interlocked.Exchange(ref _lastSeenTicks, value.UtcTicks);
        }

        public void Add(Entry entry)
        {
            Entries.Enqueue(entry);
            LastSeen = entry.At;
            Tokens += entry.Tokens;
            Requests += entry.Request ? 1 : 0;
        }

        /// <summary>
        /// Takes <paramref name="tokens"/> off the charges still in the window, newest first and none below zero. A refund
        /// is not an entry of its own: one stamped later than the estimate it corrects would outlast it and take the
        /// count below what was used.
        /// </summary>
        public void Refund(long tokens, DateTimeOffset now)
        {
            LastSeen = now;
            Entry[] entries = Entries.ToArray();
            for (int i = entries.Length - 1; i >= 0 && tokens > 0; i--)
            {
                long taken = Math.Min(tokens, entries[i].Tokens);
                entries[i] = entries[i] with { Tokens = entries[i].Tokens - taken };
                Tokens -= taken;
                tokens -= taken;
            }

            Entries.Clear();
            foreach (Entry entry in entries)
            {
                Entries.Enqueue(entry);
            }
        }

        public void Prune(DateTimeOffset now)
        {
            while (Entries.TryPeek(out Entry oldest) && now - oldest.At >= Window)
            {
                Entries.Dequeue();
                Tokens -= oldest.Tokens;
                Requests -= oldest.Request ? 1 : 0;
            }
        }
    }
}
