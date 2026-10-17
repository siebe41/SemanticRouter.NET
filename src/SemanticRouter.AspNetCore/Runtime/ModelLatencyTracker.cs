namespace SemanticRouter.AspNetCore;

/// <summary>
/// Each model's median time to first byte over the activity log's recent served turns, timed from the start of the
/// attempt that served (a model that answers after another failed is not charged for the failed one's time)
/// (<see cref="ModelSelectionOptions.LatencyWindow"/>, at least <see cref="ModelSelectionOptions.LatencySamples"/> turns),
/// for the latency-aware strategies. Recomputed every few seconds in the background: the activity it reads can be a
/// day's worth, which a request must not wait for (nor every request behind it). Only the first time is it computed
/// while the caller waits.
/// </summary>
internal sealed class ModelLatencyTracker(RoutingActivityLog activity, TimeProvider? time = null) : IModelLatency
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private ModelSelectionOptions _options = new();
    private (DateTimeOffset At, Dictionary<string, TimeSpan> Typical)? _snapshot;
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
    public TimeSpan? TypicalTimeToFirstByte(ModelDefinition model) =>
        Typical().TryGetValue(model.Name, out TimeSpan typical) ? typical : null;

    /// <summary>The median per model with enough recent turns.</summary>
    public IReadOnlyDictionary<string, TimeSpan> Typical()
    {
        DateTimeOffset now = _time.GetUtcNow();
        (DateTimeOffset At, Dictionary<string, TimeSpan> Typical)? snapshot;
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

        return current.Typical;
    }

    private Dictionary<string, TimeSpan> Refresh(ModelSelectionOptions options, DateTimeOffset now)
    {
        Dictionary<string, TimeSpan> typical = activity.Between(now - options.LatencyWindow, now.AddSeconds(1))
            .Where(e => e.Outcome == "served" && e.ServedBy is not null && e.ServingTimes().FirstByte is not null)
            .GroupBy(e => e.ServedBy!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= options.LatencySamples)
            .ToDictionary(g => g.Key, g => TimeSpan.FromMilliseconds(Median([.. g.Select(e => e.ServingTimes().FirstByte!.Value)])), StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            if (ReferenceEquals(_options, options))
            {
                _snapshot = (now, typical); // not for a configuration applied meanwhile
            }
        }

        return typical;
    }

    private static double Median(double[] values)
    {
        Array.Sort(values);
        int middle = values.Length / 2;
        return values.Length % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
    }
}
