using System.Text.Json;
using System.Text.Json.Serialization;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// What went wrong with each connector lately, as <see cref="PromptDispatcher"/> saw it: since when every request to it
/// has failed, on which days it hit its usage limit, and each time it ran out (<see cref="LimitEpisode"/>: when, when the
/// provider said it resets, and when it served again). Feeds <see cref="RouterNotices"/> and <see cref="QuotaForecaster"/>.
/// Kept in an <see cref="IStateValueStore"/> when there is one (the SQLite state), so a restart does not start the counts
/// again.
/// </summary>
/// <remarks>
/// A request that fails only changes memory: the state is saved in the background, outside the lock, and only when
/// something a restart would miss changed (#392). A usage limit is kept as the latest hit of each day, so a connector
/// that hits its limit on every request keeps at most eight of them. A limit episode is saved when it starts, when its
/// reported reset changes and when the connector serves again; a further refusal within it only moves
/// <see cref="LimitEpisode.LastHit"/> in memory, which the next save carries along.
/// </remarks>
internal sealed class ConnectorIncidents : IDisposable
{
    internal const string StoreKey = "connector_incidents";

    /// <summary>How long a limit episode is kept: long enough to learn a monthly limit from a few cycles.</summary>
    internal static readonly TimeSpan EpisodeRetention = TimeSpan.FromDays(100);

    /// <summary>The most limit episodes kept per connector (an API answering 429 every minute must not grow it).</summary>
    internal const int MaxEpisodes = 100;

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
            if (!_connectors.TryGetValue(connector, out State? state))
            {
                return;
            }

            bool changed = state.FailingSince is not null;
            state.FailingSince = null;
            if (state.Episodes is [.., { ServedAgain: null } open])
            {
                // Out of its limit no longer: the limit reset at this moment at the latest.
                state.Episodes[^1] = open with { ServedAgain = _time.GetUtcNow() };
                changed = true;
            }

            if (!changed)
            {
                return;
            }
        }

        _saver?.Request();
    }

    /// <summary>A request through <paramref name="connector"/> failed.</summary>
    public void Failed(string connector, ModelFailureKind kind, string? detail) => Failed(connector, kind, detail, null);

    /// <summary>
    /// A request through <paramref name="connector"/> failed; for a usage limit, <paramref name="resetsAt"/> is when the
    /// provider said it ends (<see cref="ConnectorResult.RetryAt"/>), if it did.
    /// </summary>
    public void Failed(string connector, ModelFailureKind kind, string? detail, DateTimeOffset? resetsAt)
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
                DateTimeOffset? reset = resetsAt is { } r && r > now && r - now <= ModelHealthTracker.MaximumResetCooldown ? r : null;
                changed = state.Hit(now) | !string.Equals(state.LimitDetail, detail, StringComparison.Ordinal) | state.AddLimitHit(now, reset);
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
    /// Each time <paramref name="connector"/> ran out of its usage limit in the last <see cref="EpisodeRetention"/>,
    /// oldest first.
    /// </summary>
    public IReadOnlyList<LimitEpisode> LimitEpisodes(string connector)
    {
        lock (_gate)
        {
            return _connectors.TryGetValue(connector, out State? state) ? [.. state.Episodes] : [];
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

        /// <summary>Each time the connector ran out of its limit, oldest first. Absent from state saved before #374.</summary>
        public List<LimitEpisode> Episodes { get; init; } = [];

        /// <summary>
        /// A limit hit at <paramref name="now"/>: part of the open episode while the connector has not served since and the
        /// reset it reported (if any) has not passed; otherwise the start of a new one. Returns whether a restart would
        /// miss it: a new episode, or a reset reported anew (not a further refusal within the same episode).
        /// </summary>
        public bool AddLimitHit(DateTimeOffset now, DateTimeOffset? resetsAt)
        {
            if (Episodes is [.., { ServedAgain: null } open] && (open.ResetsAt is null || now <= open.ResetsAt))
            {
                DateTimeOffset? reset = resetsAt ?? open.ResetsAt;
                Episodes[^1] = open with { LastHit = now, ResetsAt = reset };
                return reset != open.ResetsAt;
            }

            Episodes.Add(new LimitEpisode { Hit = now, LastHit = now, ResetsAt = resetsAt });
            Prune(now);
            return true;
        }

        /// <summary>Keeps the latest hit of each day in the window, and the episodes within their retention.</summary>
        public void Compact(DateTimeOffset now)
        {
            DateTimeOffset[] kept = [.. LimitHits.Where(t => now - t <= LimitWindow).GroupBy(DayOf).Select(day => day.Max()).Order()];
            LimitHits.Clear();
            LimitHits.AddRange(kept);
            Prune(now);
        }

        private void Prune(DateTimeOffset now)
        {
            Episodes.RemoveAll(e => now - e.Hit > EpisodeRetention);
            if (Episodes.Count > MaxEpisodes)
            {
                Episodes.RemoveRange(0, Episodes.Count - MaxEpisodes);
            }
        }
    }
}

/// <summary>
/// One time a connector ran out of its usage limit: from the first request refused for it until the connector served
/// again. Holds times only, never a message.
/// </summary>
internal sealed record LimitEpisode
{
    /// <summary>The first request refused for the limit.</summary>
    public required DateTimeOffset Hit { get; init; }

    /// <summary>The latest request refused for it.</summary>
    public required DateTimeOffset LastHit { get; init; }

    /// <summary>When the provider said the limit resets (the latest it said), if it did.</summary>
    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>When the connector served a request again; <see langword="null"/> while it has not.</summary>
    public DateTimeOffset? ServedAgain { get; init; }

    /// <summary>
    /// When the limit reset, as far as is known: the reported reset, or the first request served after it when that came
    /// sooner (or nothing was reported). Usage between the true reset and the first request served is nil either way.
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset? Reset => (ResetsAt, ServedAgain) switch
    {
        ({ } reported, { } served) => served < reported ? served : reported,
        ({ } reported, null) => reported,
        (null, var served) => served,
    };
}
