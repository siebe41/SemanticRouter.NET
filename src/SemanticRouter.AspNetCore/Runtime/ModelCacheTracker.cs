namespace SemanticRouter.AspNetCore;

/// <summary>
/// The share of each model's input read from and written to the provider's prompt cache over the activity log's recent
/// served turns (<see cref="ModelSelectionOptions.CacheShareWindow"/>), per route and over all its turns, once there are
/// <see cref="ModelSelectionOptions.CacheShareSamples"/> turns with reported usage. Recomputed once a minute in the
/// background: the activity it reads can be a day's worth, which a request must not wait for. Only the first time is it
/// computed while the caller waits.
/// </summary>
internal sealed class ModelCacheTracker(RoutingActivityLog activity, TimeProvider? time = null) : IModelCacheShare
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private ModelSelectionOptions _options = new();
    private (DateTimeOffset At, Dictionary<string, Shares> Shares)? _snapshot;
    private int _refreshing;

    /// <summary>Applies a new configuration's window and sample count.</summary>
    public void Configure(ModelSelectionOptions options)
    {
        lock (_gate)
        {
            _options = options;
            _snapshot = null;
        }
    }

    /// <inheritdoc />
    public PromptCacheShare? Typical(ModelDefinition model, string? route)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!Current().TryGetValue(model.Name, out Shares? shares))
        {
            return null;
        }

        return route is not null && shares.Routes.TryGetValue(route, out PromptCacheShare? onRoute) ? onRoute : shares.All;
    }

    /// <summary>Each model's share over all its recent turns, for the models with enough of them.</summary>
    public IReadOnlyDictionary<string, PromptCacheShare> Typical() =>
        Current().Where(kv => kv.Value.All is not null).ToDictionary(kv => kv.Key, kv => kv.Value.All!, StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, Shares> Current()
    {
        DateTimeOffset now = _time.GetUtcNow();
        (DateTimeOffset At, Dictionary<string, Shares> Shares)? snapshot;
        ModelSelectionOptions options;
        lock (_gate)
        {
            (snapshot, options) = (_snapshot, _options);
        }

        if (snapshot is not { } current)
        {
            return Refresh(options, now);
        }

        if (now - current.At >= RefreshEvery && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    Refresh(options, _time.GetUtcNow());
                }
                finally
                {
                    Volatile.Write(ref _refreshing, 0);
                }
            });
        }

        return current.Shares;
    }

    private Dictionary<string, Shares> Refresh(ModelSelectionOptions options, DateTimeOffset now)
    {
        int samples = options.CacheShareSamples;
        Dictionary<string, Shares> shares = activity.Between(now - options.CacheShareWindow, now.AddSeconds(1))
            .Where(e => e.Outcome == "served" && e.ServedBy is not null && Total(e) > 0)
            .GroupBy(e => e.ServedBy!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => new Shares(
                    Share([.. g], samples),
                    g.Where(e => e.Route is not null)
                        .GroupBy(e => e.Route!, StringComparer.OrdinalIgnoreCase)
                        .Select(r => (r.Key, Share: Share([.. r], samples)))
                        .Where(r => r.Share is not null)
                        .ToDictionary(r => r.Key, r => r.Share!, StringComparer.OrdinalIgnoreCase)),
                StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            if (ReferenceEquals(_options, options))
            {
                _snapshot = (now, shares); // not for a configuration applied meanwhile
            }
        }

        return shares;
    }

    /// <summary>Cache reads and writes as shares of all input tokens over the turns, when there are enough of them.</summary>
    private static PromptCacheShare? Share(RoutingActivityEntry[] turns, int samples)
    {
        if (turns.Length < samples)
        {
            return null;
        }

        double total = turns.Sum(Total);
        return new PromptCacheShare
        {
            Read = turns.Sum(e => (double)(e.CacheReadTokens ?? 0)) / total,
            Write = turns.Sum(e => (double)(e.CacheCreationTokens ?? 0)) / total,
        };
    }

    /// <summary>All input tokens of a turn: uncached input, cache reads and cache writes.</summary>
    private static double Total(RoutingActivityEntry e) =>
        (double)(e.InputTokens ?? 0) + (e.CacheReadTokens ?? 0) + (e.CacheCreationTokens ?? 0);

    /// <summary>A model's share over all its turns, and per route with enough turns.</summary>
    private sealed record Shares(PromptCacheShare? All, Dictionary<string, PromptCacheShare> Routes);
}
