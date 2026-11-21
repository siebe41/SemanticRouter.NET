using System.Text.Json;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// What went wrong with each connector lately, as <see cref="PromptDispatcher"/> saw it: since when every request to it
/// has failed, and on which days it hit its usage limit. Feeds <see cref="RouterNotices"/>. Kept in an
/// <see cref="IStateValueStore"/> when there is one (the SQLite state), so a restart does not start the counts again.
/// </summary>
/// <remarks>
/// A request that fails only changes memory: the state is saved in the background, outside the lock, and only when
/// something a restart would miss changed (#392). A usage limit is kept as the latest hit of each day, so a connector
/// that hits its limit on every request keeps at most eight of them.
/// </remarks>
internal sealed class ConnectorIncidents : IDisposable
{
    internal const string StoreKey = "connector_incidents";

    private static readonly TimeSpan LimitWindow = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TimeProvider _time;
    private readonly BackgroundSaver? _saver;
    private readonly Dictionary<string, State> _connectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public ConnectorIncidents(TimeProvider? time = null, IStateValueStore? store = null)
    {
        _time = time ?? TimeProvider.System;
        if (store is null)
        {
            return;
        }

        _saver = new BackgroundSaver(Snapshot, value => store.Save(StoreKey, value));
        if (store.Load(StoreKey) is { } saved)
        {
            DateTimeOffset now = _time.GetUtcNow();
            try
            {
                foreach ((string connector, State state) in JsonSerializer.Deserialize<Dictionary<string, State>>(saved, Json) ?? [])
                {
                    // Earlier versions kept every hit; one per day is all that is read.
                    state.Compact(now);
                    _connectors[connector] = state;
                }
            }
            catch (JsonException)
            {
                // Unreadable: start counting afresh.
            }
        }
    }

    /// <summary>A request through <paramref name="connector"/> was served: it is up.</summary>
    public void Served(string connector)
    {
        lock (_gate)
        {
            if (!_connectors.TryGetValue(connector, out State? state) || state.FailingSince is null)
            {
                return;
            }

            state.FailingSince = null;
        }

        _saver?.Request();
    }

    /// <summary>A request through <paramref name="connector"/> failed.</summary>
    public void Failed(string connector, ModelFailureKind kind, string? detail)
    {
        DateTimeOffset now = _time.GetUtcNow();
        bool changed;
        lock (_gate)
        {
            if (!_connectors.TryGetValue(connector, out State? state))
            {
                _connectors[connector] = state = new State();
            }

            if (kind == ModelFailureKind.QuotaExceeded)
            {
                // A usage limit says nothing about whether the connector works.
                changed = state.Hit(now) | !string.Equals(state.LimitDetail, detail, StringComparison.Ordinal);
                state.LimitDetail = detail;
            }
            else
            {
                changed = state.FailingSince is null || !string.Equals(state.Detail, detail, StringComparison.Ordinal);
                state.FailingSince ??= now;
                state.Detail = detail;
            }
        }

        if (changed)
        {
            _saver?.Request();
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
                .Select(c => (c.Key, c.Value.LimitHits.Where(t => now - t <= LimitWindow).Select(DayOf).Distinct().Count(), c.Value.LimitDetail))
                .Where(c => c.Item2 > 0)];
        }
    }

    /// <summary>
    /// Forgets the latest failure messages, which can quote a failed CLI run's output (and so the prompt), for when the
    /// activity is deleted. Since when each connector fails, and its limit days, stay. Saved before this returns, so a
    /// purge that empties the store's files afterwards leaves no message in them.
    /// </summary>
    public void ForgetDetails()
    {
        lock (_gate)
        {
            if (_connectors.Values.All(s => s.Detail is null && s.LimitDetail is null))
            {
                return;
            }

            foreach (State state in _connectors.Values)
            {
                state.Detail = null;
                state.LimitDetail = null;
            }
        }

        _saver?.SaveNow();
    }

    /// <summary>Waits until every change is saved (at most a few seconds), as a restart would.</summary>
    public void Dispose() => _saver?.Flush(TimeSpan.FromSeconds(5));

    /// <summary>Waits until every change so far is saved (tests).</summary>
    internal bool Flush(TimeSpan timeout) => _saver?.Flush(timeout) ?? true;

    private string Snapshot()
    {
        lock (_gate)
        {
            return JsonSerializer.Serialize(_connectors, Json);
        }
    }

    private static DateOnly DayOf(DateTimeOffset at) => DateOnly.FromDateTime(at.UtcDateTime);

    private sealed class State
    {
        public DateTimeOffset? FailingSince { get; set; }

        public string? Detail { get; set; }

        /// <summary>The latest limit hit of each day (UTC) in the window: at most eight. The stored format is a plain list.</summary>
        public List<DateTimeOffset> LimitHits { get; init; } = [];

        public string? LimitDetail { get; set; }

        /// <summary>A limit hit at <paramref name="now"/>. Returns whether a restart would miss it: a new day, or one leaving the window.</summary>
        public bool Hit(DateTimeOffset now)
        {
            DateOnly today = DayOf(now);
            bool newDay = !LimitHits.Exists(t => DayOf(t) == today);
            int expired = LimitHits.RemoveAll(t => now - t > LimitWindow);
            LimitHits.RemoveAll(t => DayOf(t) == today);
            LimitHits.Add(now);
            return newDay || expired > 0;
        }

        /// <summary>Keeps the latest hit of each day in the window.</summary>
        public void Compact(DateTimeOffset now)
        {
            DateTimeOffset[] kept = [.. LimitHits.Where(t => now - t <= LimitWindow).GroupBy(DayOf).Select(day => day.Max()).Order()];
            LimitHits.Clear();
            LimitHits.AddRange(kept);
        }
    }
}
