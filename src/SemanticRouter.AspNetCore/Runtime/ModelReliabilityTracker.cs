using Microsoft.Extensions.DependencyInjection;

namespace SemanticRouter.AspNetCore;

/// <summary>How one model has done on one kind of work: a route, or turns with tools.</summary>
internal sealed record ModelReliability
{
    /// <summary>The model that answered.</summary>
    public required string Model { get; init; }

    /// <summary>The route, for route work.</summary>
    public string? Route { get; init; }

    /// <summary>The capability (<c>tools</c>), for capability work.</summary>
    public string? Capability { get; init; }

    /// <summary>Turns it answered in the window (since it was last left out or reset).</summary>
    public required int Turns { get; init; }

    /// <summary>Of those, turns that drew feedback (retried, corrected, tool errors, empty reply).</summary>
    public required int Failures { get; init; }

    /// <summary>Failures / turns.</summary>
    public required double Rate { get; init; }

    /// <summary>The same share for the other models with enough turns on this work, when there are any.</summary>
    public double? PeerRate { get; init; }

    /// <summary>Feedback of each kind on its turns.</summary>
    public required IReadOnlyDictionary<FeedbackKind, int> ByKind { get; init; }

    /// <summary>When it stopped being offered for this work, while it is left out.</summary>
    public DateTimeOffset? ExcludedSince { get; init; }

    /// <summary>When it will be offered again.</summary>
    public DateTimeOffset? ExcludedUntil { get; init; }

    /// <summary>Why, e.g. "7 of 20 turns drew feedback (5 tool errors, 2 retried); other models 10%".</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// The router's <see cref="IModelReliability"/> (<see cref="ReliabilityOptions"/>): from the activity log and its
/// feedback, the share of each model's turns that drew feedback, per route and for tool use. A model clearly worse than
/// its peers on a kind of work is left out of that work for <see cref="ReliabilityOptions.Period"/>, then offered again
/// with a fresh count, so a model that improves (or a fixed prompt template) comes back.
/// </summary>
/// <remarks>
/// Only decisions the router made count (not explicit choices). Feedback on a tool-result turn counts against the turn
/// that started the task. A model is never left out of every route it can serve. The figures are recomputed in the
/// background at most once a minute; <see cref="IsReliable"/> only looks them up.
/// </remarks>
internal sealed class ModelReliabilityTracker : IModelReliability
{
    private const string Tools = "tools";
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(1);
    private static readonly HashSet<FeedbackKind> Counted = [FeedbackKind.Retried, FeedbackKind.Corrected, FeedbackKind.ToolErrors, FeedbackKind.EmptyReply, FeedbackKind.Judged];

    private readonly IServiceProvider _services;
    private readonly RoutingActivityLog _activity;
    private readonly RoutingFeedbackLog _feedback;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Exclusion> _exclusions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _since = new(StringComparer.OrdinalIgnoreCase);
    private Snapshot _snapshot = new(DateTimeOffset.MinValue, [], new Dictionary<string, Exclusion>(StringComparer.OrdinalIgnoreCase));
    private int _refreshing;

    public ModelReliabilityTracker(IServiceProvider services, RoutingActivityLog activity, RoutingFeedbackLog feedback, TimeProvider? time = null)
    {
        _services = services;
        _activity = activity;
        _feedback = feedback;
        _time = time ?? TimeProvider.System;
    }

    // Resolved on use: the runtime provider builds the router, which takes this tracker.
    private RouterConfiguration Configuration => _services.GetRequiredService<RouterRuntimeProvider>().Current.Configuration;

    /// <inheritdoc />
    public bool IsReliable(ModelDefinition model, string? route, IReadOnlyList<string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!Configuration.Middleware.Reliability.Enabled)
        {
            return true;
        }

        DateTimeOffset now = _time.GetUtcNow();
        Snapshot snapshot = Volatile.Read(ref _snapshot);
        if (now - snapshot.At >= RefreshEvery && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    Refresh();
                }
                finally
                {
                    Volatile.Write(ref _refreshing, 0);
                }
            });
        }

        bool Left(string work) => snapshot.Exclusions.TryGetValue(Key(model.Name, work), out Exclusion? exclusion) && exclusion.Until > now;
        return !(route is not null && Left(RouteWork(route))) && !(capabilities.Contains(Tools, StringComparer.OrdinalIgnoreCase) && Left(Tools));
    }

    /// <summary>Every model on every kind of work it did in the window, and the ones left out; recomputed now.</summary>
    public IReadOnlyList<ModelReliability> Refresh()
    {
        RouterConfiguration configuration = Configuration;
        ReliabilityOptions options = configuration.Middleware.Reliability;
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();

            // A period that ended starts a fresh count: what led to it does not count against the model again.
            foreach ((string key, Exclusion exclusion) in _exclusions.Where(e => e.Value.Until <= now).ToArray())
            {
                _exclusions.Remove(key);
                _since[key] = exclusion.Until;
            }

            Dictionary<string, Tally> tallies = options.Enabled ? Count(now, options) : [];
            var results = new List<ModelReliability>();
            foreach (IGrouping<string, KeyValuePair<string, Tally>> work in tallies.GroupBy(t => t.Value.Work, StringComparer.OrdinalIgnoreCase))
            {
                KeyValuePair<string, Tally>[] qualified = [.. work.Where(t => t.Value.Turns >= options.MinimumTurns)];
                foreach ((string key, Tally tally) in work)
                {
                    KeyValuePair<string, Tally>[] peers = [.. qualified.Where(p => p.Key != key)];
                    int peerTurns = peers.Sum(p => p.Value.Turns);
                    double? peerRate = peerTurns > 0 ? (double)peers.Sum(p => p.Value.Failures) / peerTurns : null;
                    double rate = tally.Turns > 0 ? (double)tally.Failures / tally.Turns : 0;
                    string detail = Describe(tally, peerRate);
                    bool unreliable = tally.Turns >= options.MinimumTurns
                        && rate >= options.MaxFailureRate
                        && (peerRate is null || rate >= peerRate + options.PeerMargin);
                    if (unreliable && !_exclusions.ContainsKey(key) && KeepsARoute(configuration, tally))
                    {
                        _exclusions[key] = new Exclusion(now, now + options.Period, detail);
                    }

                    _exclusions.TryGetValue(key, out Exclusion? left);
                    results.Add(Result(tally, rate, peerRate, left, left?.Detail ?? detail));
                }
            }

            // Left out with no turns since (they are not offered any): still listed.
            foreach ((string key, Exclusion exclusion) in _exclusions.Where(e => !tallies.ContainsKey(e.Key)))
            {
                (string model, string work) = Split(key);
                results.Add(Result(new Tally(model, work), 0, null, exclusion, exclusion.Detail));
            }

            Volatile.Write(ref _snapshot, new Snapshot(now, results, new Dictionary<string, Exclusion>(_exclusions, StringComparer.OrdinalIgnoreCase)));
            return results;
        }
    }

    /// <summary>The figures as last computed.</summary>
    public IReadOnlyList<ModelReliability> Current => Volatile.Read(ref _snapshot).Results;

    /// <summary>Offers a model again (for one route or capability, or everything) and starts its count afresh.</summary>
    public void Reset(string model, string? route = null, string? capability = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        lock (_gate)
        {
            DateTimeOffset now = _time.GetUtcNow();
            string? only = route is not null ? Key(model, RouteWork(route)) : capability is not null ? Key(model, capability) : null;
            string prefix = model + "\n";
            string[] keys = only is not null ? [only] : [.. _exclusions.Keys.Concat(_snapshot.Results.Select(r => Key(r.Model, r.Route is not null ? RouteWork(r.Route) : r.Capability!)))
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase)];
            foreach (string key in keys)
            {
                _exclusions.Remove(key);
                _since[key] = now;
            }
        }

        Refresh();
    }

    /// <summary>Turns and turns with feedback per model and kind of work, in the window.</summary>
    private Dictionary<string, Tally> Count(DateTimeOffset now, ReliabilityOptions options)
    {
        DateTimeOffset since = now - options.Window;
        IReadOnlyList<RoutingActivityEntry> entries = _activity.Between(since, now.AddSeconds(1));

        // Feedback on a tool-result turn is about the task: count it against the turn that started it.
        var task = new Dictionary<long, long>();
        var started = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (RoutingActivityEntry entry in entries.OrderBy(e => e.Id))
        {
            if (!entry.ToolContinuation)
            {
                task[entry.Id] = entry.Id;
                if (entry.Conversation is { } conversation)
                {
                    started[conversation] = entry.Id;
                }
            }
            else if (entry.Conversation is { } conversation && started.TryGetValue(conversation, out long first))
            {
                task[entry.Id] = first;
            }
        }

        var failed = new Dictionary<long, HashSet<FeedbackKind>>();
        foreach (RoutingFeedback item in _feedback.Between(since, now.AddSeconds(1)).Where(f => Counted.Contains(f.Kind)))
        {
            long turn = task.TryGetValue(item.EntryId, out long t) ? t : item.EntryId;
            (failed.TryGetValue(turn, out HashSet<FeedbackKind>? kinds) ? kinds : failed[turn] = []).Add(item.Kind);
        }

        var tallies = new Dictionary<string, Tally>(StringComparer.OrdinalIgnoreCase);
        foreach (RoutingActivityEntry entry in entries.Where(e => !e.ToolContinuation && e.Outcome == "served" && e.ServedBy is not null
            && e.Reason is not (RoutingReason.ModelOverride or RoutingReason.ExplicitCallout or RoutingReason.ConversationAffinity)))
        {
            IEnumerable<string> works = (entry.Route is { } route ? [RouteWork(route)] : Array.Empty<string>()).Concat(entry.HasTools ? [Tools] : []);
            foreach (string work in works)
            {
                string key = Key(entry.ServedBy!, work);
                if (_since.TryGetValue(key, out DateTimeOffset fresh) && entry.Timestamp < fresh)
                {
                    continue;
                }

                Tally tally = tallies.TryGetValue(key, out Tally? seen) ? seen : tallies[key] = new Tally(entry.ServedBy!, work);
                tally.Turns++;
                if (failed.TryGetValue(entry.Id, out HashSet<FeedbackKind>? kinds))
                {
                    tally.Failures++;
                    foreach (FeedbackKind kind in kinds)
                    {
                        tally.ByKind[kind] = tally.ByKind.GetValueOrDefault(kind) + 1;
                    }
                }
            }
        }

        return tallies;
    }

    /// <summary>
    /// Whether the model keeps a route it can serve when left out of this one. Capability work (tools) always does:
    /// the model still serves turns without tools.
    /// </summary>
    private bool KeepsARoute(RouterConfiguration configuration, Tally tally)
    {
        if (tally.Work == Tools)
        {
            return true;
        }

        ModelDefinition? model = configuration.Models.FirstOrDefault(m => string.Equals(m.Name, tally.Model, StringComparison.OrdinalIgnoreCase));
        if (model is null)
        {
            return false;
        }

        RouteDefinition[] servable = [.. configuration.Routes.Where(r => r.Tier <= model.Tier)];
        return servable.Any(r => !string.Equals(RouteWork(r.Name), tally.Work, StringComparison.OrdinalIgnoreCase)
            && !_exclusions.ContainsKey(Key(model.Name, RouteWork(r.Name))));
    }

    private static ModelReliability Result(Tally tally, double rate, double? peerRate, Exclusion? left, string? detail) => new ModelReliability
    {
        Model = tally.Model,
        Route = tally.Work.StartsWith("route:", StringComparison.Ordinal) ? tally.Work["route:".Length..] : null,
        Capability = tally.Work.StartsWith("route:", StringComparison.Ordinal) ? null : tally.Work,
        Turns = tally.Turns,
        Failures = tally.Failures,
        Rate = Math.Round(rate, 4),
        PeerRate = peerRate is { } peers ? Math.Round(peers, 4) : null,
        ByKind = new Dictionary<FeedbackKind, int>(tally.ByKind),
        ExcludedSince = left?.Since,
        ExcludedUntil = left?.Until,
        Detail = detail,
    };

    /// <summary>"7 of 20 turns drew feedback (5 tool errors, 2 retried); other models 10%".</summary>
    private static string Describe(Tally tally, double? peerRate)
    {
        string kinds = string.Join(", ", tally.ByKind.OrderByDescending(k => k.Value).Select(k => $"{k.Value} {Name(k.Key, k.Value)}"));
        return $"{tally.Failures} of {tally.Turns} turns drew feedback" + (kinds.Length > 0 ? $" ({kinds})" : string.Empty)
            + (peerRate is { } peers ? $"; other models {peers.ToString("P0", System.Globalization.CultureInfo.InvariantCulture)}" : string.Empty);
    }

    private static string Name(FeedbackKind kind, int count) => kind switch
    {
        FeedbackKind.Retried => "retried",
        FeedbackKind.Corrected => "corrected",
        FeedbackKind.ToolErrors => "with tool errors",
        FeedbackKind.Judged => "graded low by the judge",
        _ => count == 1 ? "empty reply" : "empty replies",
    };

    private static string RouteWork(string route) => "route:" + route;

    private static string Key(string model, string work) => model + "\n" + work;

    private static (string Model, string Work) Split(string key)
    {
        int split = key.IndexOf('\n', StringComparison.Ordinal);
        return (key[..split], key[(split + 1)..]);
    }

    private sealed class Tally(string model, string work)
    {
        public string Model { get; } = model;

        public string Work { get; } = work;

        public int Turns { get; set; }

        public int Failures { get; set; }

        public Dictionary<FeedbackKind, int> ByKind { get; } = [];
    }

    private sealed record Exclusion(DateTimeOffset Since, DateTimeOffset Until, string Detail);

    private sealed record Snapshot(DateTimeOffset At, IReadOnlyList<ModelReliability> Results, IReadOnlyDictionary<string, Exclusion> Exclusions);
}
