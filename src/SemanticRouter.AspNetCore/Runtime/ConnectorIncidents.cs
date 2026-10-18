using System.Text.Json;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// What went wrong with each connector lately, as <see cref="PromptDispatcher"/> saw it: since when every request to it
/// has failed, and on which days it hit its usage limit. Feeds <see cref="RouterNotices"/>. Kept in an
/// <see cref="IStateValueStore"/> when there is one (the SQLite state), so a restart does not start the counts again.
/// </summary>
internal sealed class ConnectorIncidents
{
    internal const string StoreKey = "connector_incidents";

    private static readonly TimeSpan LimitWindow = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TimeProvider _time;
    private readonly IStateValueStore? _store;
    private readonly Dictionary<string, State> _connectors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    public ConnectorIncidents(TimeProvider? time = null, IStateValueStore? store = null)
    {
        _time = time ?? TimeProvider.System;
        _store = store;
        if (store?.Load(StoreKey) is { } saved)
        {
            try
            {
                foreach ((string connector, State state) in JsonSerializer.Deserialize<Dictionary<string, State>>(saved, Json) ?? [])
                {
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
            if (_connectors.TryGetValue(connector, out State? state) && state.FailingSince is not null)
            {
                state.FailingSince = null;
                Save();
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
            }
            else
            {
                state.FailingSince ??= now;
                state.Detail = detail;
            }

            Save();
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

    /// <summary>
    /// Forgets the latest failure messages, which can quote a failed CLI run's output (and so the prompt), for when the
    /// activity is deleted. Since when each connector fails, and its limit days, stay.
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

            Save();
        }
    }

    /// <summary>Writes the state through (under the lock). Failures are the store's to log.</summary>
    private void Save() => _store?.Save(StoreKey, JsonSerializer.Serialize(_connectors, Json));

    private sealed class State
    {
        public DateTimeOffset? FailingSince { get; set; }

        public string? Detail { get; set; }

        public List<DateTimeOffset> LimitHits { get; init; } = [];

        public string? LimitDetail { get; set; }
    }
}
