namespace SemanticRouter.AspNetCore;

/// <summary>
/// Each model's median time to first byte over the activity log's recent served turns, timed from the start of the
/// attempt that served (a model that answers after another failed is not charged for the failed one's time)
/// (<see cref="ModelSelectionOptions.LatencyWindow"/>, at least <see cref="ModelSelectionOptions.LatencySamples"/> turns),
/// for the latency-aware strategies. Recomputed at most every few seconds.
/// </summary>
internal sealed class ModelLatencyTracker(RoutingActivityLog activity, TimeProvider? time = null) : IModelLatency
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private ModelSelectionOptions _options = new();
    private (DateTimeOffset At, Dictionary<string, TimeSpan> Typical)? _snapshot;

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
        lock (_gate)
        {
            if (_snapshot is { } snapshot && now - snapshot.At < RefreshEvery)
            {
                return snapshot.Typical;
            }

            ModelSelectionOptions options = _options;
            Dictionary<string, TimeSpan> typical = activity.Between(now - options.LatencyWindow, now.AddSeconds(1))
                .Where(e => e.Outcome == "served" && e.ServedBy is not null && e.ServingTimes().FirstByte is not null)
                .GroupBy(e => e.ServedBy!, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= options.LatencySamples)
                .ToDictionary(g => g.Key, g => TimeSpan.FromMilliseconds(Median([.. g.Select(e => e.ServingTimes().FirstByte!.Value)])), StringComparer.OrdinalIgnoreCase);
            _snapshot = (now, typical);
            return typical;
        }
    }

    private static double Median(double[] values)
    {
        Array.Sort(values);
        int middle = values.Length / 2;
        return values.Length % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
    }
}
