namespace SemanticRouter.AspNetCore;

/// <summary>
/// What went wrong with each connector lately, as <see cref="PromptDispatcher"/> saw it: since when every request to it
/// has failed, and on which days it hit its usage limit. Feeds <see cref="RouterNotices"/>. Kept in memory.
/// </summary>
internal sealed class ConnectorIncidents(TimeProvider? time = null)
{
    private static readonly TimeSpan LimitWindow = TimeSpan.FromDays(7);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<string, State> _connectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>A request through <paramref name="connector"/> was served: it is up.</summary>
    public void Served(string connector)
    {
        lock (_gate)
        {
            if (_connectors.TryGetValue(connector, out State? state))
            {
                state.FailingSince = null;
            }
        }
    }

    /// <summary>A request through <paramref name="connector"/> failed.</summary>
    public void Failed(string connector, ModelFailureKind kind, string? detail)
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            if (!_connectors.TryGetValue(connector, out State? state))
            {
                _connectors[connector] = state = new State();
            }

            if (kind == ModelFailureKind.QuotaExceeded)
            {
                // A usage limit says nothing about whether the connector works.
                state.LimitHits.Add(now);
                state.LimitHits.RemoveAll(t => now - t > LimitWindow);
                state.LimitDetail = detail;
                return;
            }

            state.FailingSince ??= now;
            state.Detail = detail;
        }
    }

    /// <summary>Connectors whose every request has failed since a moment, with the latest failure.</summary>
    public IReadOnlyList<(string Connector, DateTimeOffset Since, string? Detail)> Failing()
    {
        lock (_gate)
        {
            return [.. _connectors.Where(c => c.Value.FailingSince is not null).Select(c => (c.Key, c.Value.FailingSince!.Value, c.Value.Detail))];
        }
    }

    /// <summary>Per connector, the days (UTC) in the last 7 on which it hit its usage limit, with the latest message.</summary>
    public IReadOnlyList<(string Connector, int Days, string? Detail)> LimitDays()
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            return [.. _connectors
                .Select(c => (c.Key, c.Value.LimitHits.Where(t => now - t <= LimitWindow).Select(t => DateOnly.FromDateTime(t.UtcDateTime)).Distinct().Count(), c.Value.LimitDetail))
                .Where(c => c.Item2 > 0)];
        }
    }

    private sealed class State
    {
        public DateTimeOffset? FailingSince { get; set; }

        public string? Detail { get; set; }

        public List<DateTimeOffset> LimitHits { get; } = [];

        public string? LimitDetail { get; set; }
    }
}
