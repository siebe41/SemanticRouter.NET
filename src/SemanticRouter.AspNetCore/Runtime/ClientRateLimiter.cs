using System.Globalization;

namespace SemanticRouter.AspNetCore;

/// <summary>Whether a request may go ahead (<see cref="ClientRateLimiter.TryAdmit"/>), and if not, when to retry and why.</summary>
/// <param name="Admitted">Whether it may go ahead.</param>
/// <param name="RetryAfter">How long until it would be admitted, when it was not.</param>
/// <param name="Detail">Which limit the client is over, when it was not.</param>
internal readonly record struct RateLimitDecision(bool Admitted, TimeSpan RetryAfter, string? Detail);

/// <summary>A client's rate limit and what it used in the last minute, for the dashboard.</summary>
public sealed record RateLimitStatus
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
/// without resetting what clients used.
/// </summary>
internal sealed class ClientRateLimiter(TimeProvider? time = null)
{
    /// <summary>The entry that applies to every client without its own.</summary>
    public const string Everyone = "*";

    /// <summary>The name shown for <see cref="PromptRouterMiddlewareOptions.RemoteRequestsPerMinute"/>.</summary>
    public const string RemoteDefault = "remote default";

    private const int SweepAbove = 1000;

    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<string, Usage> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

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
            UsageOf(client, now).Add(new Entry(now, tokens, Request: false));
        }
    }

    /// <summary>Every client seen whose limit still applies, with what it used in the last minute.</summary>
    public IReadOnlyList<RateLimitStatus> Status(PromptRouterMiddlewareOptions options)
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            var status = new List<RateLimitStatus>();
            foreach ((string client, Usage usage) in _clients.OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
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
        if (!_clients.TryGetValue(client, out Usage? usage))
        {
            if (_clients.Count >= SweepAbove)
            {
                // Clients without a key are counted per address: forget the idle ones, so the table stays small.
                foreach ((string idle, Usage _) in _clients.Where(c => now - c.Value.LastSeen > TimeSpan.FromHours(1)).ToArray())
                {
                    _clients.Remove(idle);
                }
            }

            _clients[client] = usage = new Usage();
        }

        usage.Prune(now);
        return usage;
    }

    private readonly record struct Entry(DateTimeOffset At, long Tokens, bool Request);

    private sealed class Usage
    {
        public Queue<Entry> Entries { get; } = new();

        public int Requests { get; private set; }

        public long Tokens { get; private set; }

        public long Limited { get; set; }

        /// <summary>Whether the client has sent requests from another machine.</summary>
        public bool Remote { get; set; }

        public DateTimeOffset? LastLimited { get; set; }

        /// <summary>When the client was last seen: its last request, charge or refusal.</summary>
        public DateTimeOffset LastSeen { get; set; }

        public void Add(Entry entry)
        {
            Entries.Enqueue(entry);
            LastSeen = entry.At;
            Tokens += entry.Tokens;
            Requests += entry.Request ? 1 : 0;
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
