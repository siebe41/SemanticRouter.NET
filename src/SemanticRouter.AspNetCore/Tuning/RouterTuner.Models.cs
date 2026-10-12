using System.Globalization;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// The cheapest good-enough model per route: a trial of the next cheaper model able to do a route's work, judged on
/// the feedback its turns draw against the route's current model, then kept (<see cref="RouteDefinition.PreferredModel"/>)
/// or dropped.
/// </summary>
internal sealed partial class RouterTuner
{
    /// <summary>Feedback that says a turn was not good enough; a callout says what the person wanted, not that the answer failed.</summary>
    private static readonly HashSet<FeedbackKind> TrialFeedback =
        [FeedbackKind.Retried, FeedbackKind.Corrected, FeedbackKind.ToolErrors, FeedbackKind.EmptyReply, FeedbackKind.Judged];

    /// <summary>The applied change that made <paramref name="route"/>'s learned model, for the Activity page's badge.</summary>
    public TuningChange? ExplainModel(string route) =>
        Volatile.Read(ref _models).LastOrDefault(c => string.Equals(c.Route, route, StringComparison.OrdinalIgnoreCase));

    /// <summary>Ends the trials that ran their turns (the cheaper model takes the route over or is dropped) and starts new ones.</summary>
    private void LearnModels(RouterRuntime runtime, TuningOptions options, DateTimeOffset now, List<TuningChange> made)
    {
        if (options.Mode != TuningMode.Apply || options.ModelTrialRate <= 0)
        {
            return;
        }

        DateTimeOffset since = now - TimeSpan.FromDays(options.FeedbackDays);
        RoutingActivityEntry[] turns = [.. _activity.Between(since, now.AddSeconds(1)).Where(e => e.Outcome == "served" && e.ServedBy is not null && !e.ToolContinuation)];
        Dictionary<long, HashSet<FeedbackKind>> feedback = _feedback.Between(since, now.AddSeconds(1))
            .Where(f => TrialFeedback.Contains(f.Kind))
            .GroupBy(f => f.EntryId)
            .ToDictionary(g => g.Key, g => g.Select(f => f.Kind).ToHashSet());

        foreach (TuningChange trial in _store.Changes().Where(c => c.Kind == TuningChangeKind.RouteModel && c.State == TuningChangeState.Trying).ToArray())
        {
            if (Conclude(trial, runtime.Configuration, options, turns, feedback, now) is { } ended)
            {
                made.Add(ended);
            }
        }

        IReadOnlyList<TuningChange> history = _store.Changes();
        RouterConfiguration configuration = _provider.Current.Configuration;
        foreach (RouteDefinition route in configuration.Routes)
        {
            if (StartTrial(route, configuration, options, history, turns, feedback, now) is { } started)
            {
                made.Add(started);
            }
        }
    }

    /// <summary>Judges a trial that has run its turns: the cheaper model takes the route over when its turns did as well.</summary>
    private TuningChange? Conclude(
        TuningChange trial, RouterConfiguration configuration, TuningOptions options, RoutingActivityEntry[] turns,
        Dictionary<long, HashSet<FeedbackKind>> feedback, DateTimeOffset now)
    {
        RouteDefinition? route = Find(configuration, trial);
        if (route is null || !configuration.Models.Any(m => m.Enabled && string.Equals(m.Name, trial.NewModel, StringComparison.OrdinalIgnoreCase)))
        {
            return Save(trial with { State = TuningChangeState.Expired, UndoneAt = now, Why = $"{trial.Why} Ended: the route or the model is gone." });
        }

        Outcome tried = Tally(turns.Where(e => e.Trial == trial.Id && Same(e.ServedBy, trial.NewModel)), feedback);
        if (tried.Turns < options.ModelTrialTurns)
        {
            // A model that cannot get its turns (unreliable, unavailable, a quiet route) does not stay on trial forever.
            return now - trial.Timestamp < TimeSpan.FromDays(options.FeedbackDays)
                ? null
                : Save(trial with { State = TuningChangeState.Expired, UndoneAt = now, Why = $"{trial.Why} Ended: only {tried.Turns} of {options.ModelTrialTurns} trial turns in {options.FeedbackDays} days." });
        }

        Outcome current = Tally(Incumbent(turns, route.Name, trial.Incumbent), feedback);
        string evidence = $"{trial.NewModel}: {tried.Describe("trial turns")}; {trial.Incumbent}: {current.Describe("turns")}.";
        if (tried.Rate > current.Rate + options.ModelTrialMargin)
        {
            return Save(trial with { State = TuningChangeState.Dropped, UndoneAt = now, Actor = Actor, Why = $"{trial.Why} Dropped: {evidence}" });
        }

        RouterConfiguration next = configuration.Clone();
        RouteDefinition target = Find(next, trial)!;
        if (!Same(target.PreferredModel, trial.OldModel))
        {
            return Save(trial with { State = TuningChangeState.Expired, UndoneAt = now, Why = $"{trial.Why} Ended: the route's model was changed meanwhile." });
        }

        target.PreferredModel = trial.NewModel;
        string why = $"{trial.Why} It did as well: {evidence} {trial.NewModel} now serves {route.Name}'s plain matches.";
        RouterRuntime runtime = _provider.Apply(next, persist: true, Source, Actor, $"change {trial.Id}: {trial.Summary}. {why}");
        return Save(trial with { State = TuningChangeState.Applied, Version = runtime.HistoryVersion, Actor = Actor, Why = why });
    }

    /// <summary>
    /// Starts a trial on a route that has enough turns from one model, when a cheaper model can do its work and was not
    /// tried (and dropped, stopped or undone) there within <see cref="TuningOptions.HoldDays"/>.
    /// </summary>
    private TuningChange? StartTrial(
        RouteDefinition route, RouterConfiguration configuration, TuningOptions options, IReadOnlyList<TuningChange> history,
        RoutingActivityEntry[] turns, Dictionary<long, HashSet<FeedbackKind>> feedback, DateTimeOffset now)
    {
        if (history.Any(c => c.Kind == TuningChangeKind.RouteModel && c.State == TuningChangeState.Trying && Same(c.Route, route.Name)))
        {
            return null;
        }

        // The route's current model: its learned one, or the one that served most of its plain matches.
        int minimum = configuration.Middleware.Reliability.MinimumTurns;
        RoutingActivityEntry[] plain = [.. turns.Where(e => Same(e.Route, route.Name) && e.Reason == RoutingReason.SemanticMatch && e.Trial is null)];
        string? current = route.PreferredModel
            ?? plain.GroupBy(e => e.ServedBy!, StringComparer.OrdinalIgnoreCase).MaxBy(g => g.Count())?.Key;
        RoutingActivityEntry[] served = [.. plain.Where(e => Same(e.ServedBy, current))];
        if (served.Length < minimum
            || configuration.Models.FirstOrDefault(m => m.Enabled && Same(m.Name, current)) is not { } incumbent)
        {
            return null;
        }

        // The next cheaper model that can do the route's work: one step down, not straight to the cheapest.
        ModelDefinition? challenger = configuration.Models
            .Where(m => m.Enabled && m != incumbent && m.HasCapabilities(route.RequiredCapabilities) && (!route.LocalOnly || m.IsLocal) && Cost(m) < Cost(incumbent))
            .OrderByDescending(Cost).ThenByDescending(m => m.Tier).ThenBy(m => m.Priority)
            .FirstOrDefault();
        if (challenger is null
            || history.Any(c => c.Kind == TuningChangeKind.RouteModel && Same(c.Route, route.Name) && Same(c.NewModel, challenger.Name)
                && c.State is TuningChangeState.Undone or TuningChangeState.Dropped or TuningChangeState.Dismissed
                && now - (c.UndoneAt ?? c.Timestamp) < TimeSpan.FromDays(options.HoldDays)))
        {
            return null;
        }

        Outcome incumbentTurns = Tally(served, feedback);
        string share = options.ModelTrialRate.ToString("0%", CultureInfo.InvariantCulture);
        string margin = options.ModelTrialMargin > 0 ? $" (or at most {options.ModelTrialMargin.ToString("0%", CultureInfo.InvariantCulture)} more)" : string.Empty;
        return Save(new TuningChange
        {
            Id = NextId(),
            Timestamp = now,
            Kind = TuningChangeKind.RouteModel,
            Route = route.Name,
            OldModel = route.PreferredModel,
            NewModel = challenger.Name,
            Incumbent = incumbent.Name,
            State = TuningChangeState.Trying,
            Actor = Actor,
            Why = $"{challenger.Name} (tier {challenger.Tier}) costs less than {incumbent.Name} (tier {incumbent.Tier}), which served {route.Name}: {incumbentTurns.Describe("turns")}. " +
                $"{share} of the route's plain matches try it, up to {options.ModelTrialTurns} turns; it takes the route over if its turns draw no more feedback{margin}.",
        });
    }

    /// <summary>Turns on <paramref name="route"/> its current model served, not trials.</summary>
    private static IEnumerable<RoutingActivityEntry> Incumbent(IEnumerable<RoutingActivityEntry> turns, string route, string? model) =>
        turns.Where(e => Same(e.Route, route) && Same(e.ServedBy, model) && e.Trial is null && e.Reason == RoutingReason.SemanticMatch);

    private static Outcome Tally(IEnumerable<RoutingActivityEntry> turns, Dictionary<long, HashSet<FeedbackKind>> feedback)
    {
        var kinds = new Dictionary<FeedbackKind, int>();
        int count = 0, failed = 0;
        foreach (RoutingActivityEntry turn in turns)
        {
            count++;
            if (feedback.TryGetValue(turn.Id, out HashSet<FeedbackKind>? seen))
            {
                failed++;
                foreach (FeedbackKind kind in seen)
                {
                    kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                }
            }
        }

        return new Outcome(count, failed, kinds);
    }

    private static decimal Cost(ModelDefinition model) => model.EstimateCost(1000, 500);

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Applied and stopped route-model changes, undone like the other kinds (see <see cref="Undo"/>).</summary>
    private static bool MatchesModel(RouterConfiguration configuration, TuningChange change, string? model) =>
        Find(configuration, change) is { } route && Same(route.PreferredModel, model);

    private static void SetModel(RouterConfiguration configuration, TuningChange change, string? model)
    {
        if (Find(configuration, change) is { } route)
        {
            route.PreferredModel = model;
        }
    }

    /// <summary>How a model's turns went: how many, how many drew feedback, of which kinds.</summary>
    private sealed record Outcome(int Turns, int Failed, Dictionary<FeedbackKind, int> Kinds)
    {
        public double Rate => Turns == 0 ? 0 : (double)Failed / Turns;

        /// <summary>"2 of 20 trial turns drew feedback (1 corrected, 1 retried)".</summary>
        public string Describe(string noun) =>
            $"{Failed} of {Turns} {noun} drew feedback" +
            (Kinds.Count == 0 ? string.Empty : " (" + string.Join(", ", Kinds.OrderByDescending(k => k.Value).Select(k => $"{k.Value} {RouterTuner.Describe(k.Key)}")) + ")");
    }
}
