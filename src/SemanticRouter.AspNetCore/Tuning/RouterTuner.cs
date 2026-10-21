using System.Globalization;
using Microsoft.Extensions.Logging;

namespace SemanticRouter.AspNetCore;

/// <summary>What one tuning run did.</summary>
public sealed record TuningRun
{
    /// <summary>When it ran.</summary>
    public required DateTimeOffset At { get; init; }

    /// <summary>What it did, in a sentence.</summary>
    public required string Result { get; init; }

    /// <summary>The changes it applied or suggested.</summary>
    public required IReadOnlyList<TuningChange> Changes { get; init; }

    /// <summary>The evaluation set before the run (<see langword="null"/> when it did not get that far).</summary>
    public TuningScore? Before { get; init; }

    /// <summary>The evaluation set after the threshold changes.</summary>
    public TuningScore? After { get; init; }
}

/// <summary>Why a request about a learned change could not be carried out.</summary>
internal sealed class TuningConflictException(string message, bool notFound = false) : InvalidOperationException(message)
{
    public bool NotFound { get; } = notFound;
}

/// <summary>
/// Automatic tuning (<see cref="TuningOptions"/>): learns similarity thresholds and route utterances from the
/// evaluation set and from feedback, applies them as configuration versions (source <see cref="Source"/>), and
/// undoes them one at a time.
/// </summary>
/// <remarks>
/// Every change is its own configuration version, so the configuration history shows it with a diff. An undone change
/// is not made again for <see cref="TuningOptions.HoldDays"/>; after that, or with much stronger evidence, it comes back
/// only as a suggestion.
/// </remarks>
internal sealed partial class RouterTuner : IDisposable
{
    /// <summary>The configuration history source of changes tuning applies.</summary>
    public const string Source = "auto-tune";

    /// <summary>The configuration history source of undoing them.</summary>
    public const string UndoSource = "auto-tune undo";

    /// <summary>Who applies changes when no person does.</summary>
    public const string Actor = "automatic tuning";

    private const int MaxUtteranceLength = 500;
    private const int MaxUtteranceCandidates = 5;
    private const int MaxExamples = 3;

    private readonly RouterRuntimeProvider _provider;
    private readonly IEvaluationSetStore _labels;
    private readonly RoutingActivityLog _activity;
    private readonly RoutingFeedbackLog _feedback;
    private readonly ITuningStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly Lock _gate = new();
    private long _lastId;
    private IReadOnlyList<TuningChange> _inForce = [];
    private IReadOnlyList<TuningChange> _models = [];
    private readonly ModelTrials? _trials;
    private readonly IModelCacheShare? _cache;

    public RouterTuner(
        RouterRuntimeProvider provider,
        IEvaluationSetStore labels,
        RoutingActivityLog activity,
        RoutingFeedbackLog feedback,
        ITuningStore store,
        ILogger<RouterTuner> logger,
        TimeProvider? time = null,
        ModelTrials? trials = null,
        IModelCacheShare? cache = null)
    {
        _provider = provider;
        _labels = labels;
        _activity = activity;
        _feedback = feedback;
        _store = store;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _trials = trials;
        _cache = cache;
        IReadOnlyList<TuningChange> changes = store.Changes();
        _lastId = changes.Count > 0 ? changes.Max(c => c.Id) : 0;
        Refresh(changes);
    }

    public void Dispose() => _busy.Dispose();

    /// <summary>Every change, newest first.</summary>
    public IReadOnlyList<TuningChange> Changes() => _store.Changes().OrderByDescending(c => c.Id).ToArray();

    /// <summary>Paused or not, and the last run.</summary>
    public TuningState State => _store.LoadState();

    /// <summary>When tuning runs next by the clock (it can run earlier after new labels), or <see langword="null"/> when it will not.</summary>
    public DateTimeOffset? NextRun
    {
        get
        {
            TuningOptions options = _provider.Current.Configuration.Tuning;
            TuningState state = State;
            return options.Mode == TuningMode.Off || state.Paused ? null : (state.LastRun ?? _time.GetUtcNow()) + options.Interval;
        }
    }

    /// <summary>Whether a run is due: the interval passed, or enough prompts were labelled since the last run.</summary>
    public bool Due()
    {
        TuningOptions options = _provider.Current.Configuration.Tuning;
        TuningState state = State;
        if (options.Mode == TuningMode.Off || state.Paused)
        {
            return false;
        }

        if (state.LastRun is null)
        {
            // The first start: count from now rather than tune before anything was seen.
            _store.SaveState(state with { LastRun = _time.GetUtcNow(), LastLabelCount = _labels.Load().Count });
            return false;
        }

        return _time.GetUtcNow() - state.LastRun.Value >= options.Interval
            || (options.NewLabels > 0 && _labels.Load().Count - state.LastLabelCount >= options.NewLabels);
    }

    /// <summary>Runs tuning once: thresholds first, then utterances.</summary>
    public async Task<TuningRun> RunAsync(CancellationToken cancellationToken = default)
    {
        await _busy.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _busy.Release();
        }
    }

    private async Task<TuningRun> RunCoreAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        RouterRuntime runtime = _provider.Current;
        TuningOptions options = runtime.Configuration.Tuning;
        IReadOnlyList<EvaluationCase> confirmed = _labels.Load();
        if (options.Mode == TuningMode.Off)
        {
            return Finish(now, confirmed.Count, "Tuning is off (Tuning:Mode).", [], null, null);
        }

        if (State.Paused)
        {
            return Finish(now, confirmed.Count, "Learning is paused.", [], null, null);
        }

        ExpireStale(runtime.Configuration);

        // Cheaper models per route learn from feedback on real turns, not from labels.
        var models = new List<TuningChange>();
        LearnModels(runtime, options, now, models);
        runtime = _provider.Current;
        string modelResult = models.Count == 0 ? string.Empty : $" Models: {string.Join("; ", models.Select(c => $"{c.Summary} ({c.State.ToString().ToLowerInvariant()})"))}.";

        // The judge's labels count for as much as its grades agree with the hand labels, once there are enough to tell.
        JudgeAgreementReport agreement = JudgeAgreement.Measure(
            _activity.Between(now - TimeSpan.FromDays(options.FeedbackDays), now.AddSeconds(1)), confirmed, runtime.Router.Models,
            runtime.Configuration.Middleware.Judge.FailingGrade, options);
        double judgeWeight = agreement.EffectiveWeight;
        (IReadOnlyList<EvaluationCase> automatic, Dictionary<string, HashSet<FeedbackKind>> kinds) = AutomaticLabels(confirmed, options, judgeWeight, now);
        if (!runtime.Configuration.Embeddings.KeepsPromptsLocal)
        {
            // Tuning embeds every prompt it learns from; local-only ones do not go to an endpoint off this machine.
            confirmed = [.. confirmed.Where(c => !c.LocalOnly)];
            automatic = [.. automatic.Where(c => !c.LocalOnly)];
        }

        int total = confirmed.Count + automatic.Count;
        if (total < options.MinimumPrompts)
        {
            return Finish(
                now,
                confirmed.Count,
                $"Waiting for labelled prompts: {total} of {options.MinimumPrompts} ({confirmed.Count} in the evaluation set, {automatic.Count} from feedback).{modelResult}",
                models,
                null,
                null);
        }

        var made = new List<TuningChange>();
        string? judgeNote = agreement.Applied
            ? string.Create(CultureInfo.InvariantCulture, $"counted at {judgeWeight:0.00}: {agreement.Summary}")
            : null;
        var evidence = new Evidence(new HashSet<EvaluationCase>(confirmed), kinds, judgeNote);
        IReadOnlyList<TuningChange> history = _store.Changes();

        // Thresholds: find the steps, then apply those that may be applied (re-planned without the others).
        var evaluator = new RoutingEvaluator(runtime.EmbeddingProvider, runtime.Configuration);
        PreparedEvaluation prepared = await evaluator.PrepareAsync([.. confirmed, .. automatic], cancellationToken).ConfigureAwait(false);
        var tuner = new ThresholdTuner(evaluator);
        var limits = new ThresholdTuningOptions
        {
            MaxStep = options.MaxStep,
            AutomaticWeight = options.FeedbackWeight,
            AutomaticWeights = [.. automatic.Select(c => Weight(kinds.TryGetValue(c.Prompt, out HashSet<FeedbackKind>? seen) ? seen : [], options, judgeWeight))],
        };
        ThresholdTuning planned = tuner.Tune(prepared, confirmed.Count, limits);

        var dispositions = planned.Steps.ToDictionary(
            s => s.Route ?? string.Empty,
            s => Decide(ThresholdChange(s, options, evidence, now), options, history, now),
            StringComparer.OrdinalIgnoreCase);
        foreach (ThresholdStep step in planned.Steps.Where(s => dispositions[s.Route ?? string.Empty] == Disposition.Suggest))
        {
            Suggest(ThresholdChange(step, options, evidence, now), made);
        }

        ThresholdTuning applicable = planned;
        if (dispositions.Values.Any(d => d != Disposition.Apply))
        {
            // A held default also holds the routes that follow it, so the same effect does not come back by another setting.
            bool defaultHeld = dispositions.TryGetValue(string.Empty, out Disposition held) && held != Disposition.Apply;
            limits.CanChange = route =>
                (!dispositions.TryGetValue(route ?? string.Empty, out Disposition d) || d == Disposition.Apply)
                && !(defaultHeld && route is not null && runtime.Configuration.Routes.Any(r => string.Equals(r.Name, route, StringComparison.OrdinalIgnoreCase) && r.SimilarityThreshold is null));
            applicable = tuner.Tune(prepared, confirmed.Count, limits);
        }

        foreach (TuningChange change in applicable.Steps.Select(s => ThresholdChange(s, options, evidence, now)))
        {
            if (Decide(change, options, history, now) == Disposition.Apply && ApplyThreshold(change, Actor) is { } applied)
            {
                made.Add(applied);
            }
        }

        if (options.Utterances && options.MaxUtterancesPerRoute > 0)
        {
            await LearnUtterancesAsync(confirmed, automatic, options, evidence, history, now, made, cancellationToken).ConfigureAwait(false);
        }

        int appliedCount = made.Count(c => c.State == TuningChangeState.Applied);
        int suggestedCount = made.Count(c => c.State == TuningChangeState.Suggested);
        string result = (made.Count == 0
            ? $"Nothing to change: {planned.ConfirmedBefore.Correct} of {planned.ConfirmedBefore.Total} prompts in the evaluation set route to their tier."
            : $"{Plural(appliedCount, "change")} applied, {Plural(suggestedCount, "suggestion")}: {string.Join("; ", made.Select(c => c.Summary))}.") + modelResult;
        return Finish(now, confirmed.Count, result, [.. models, .. made], planned.ConfirmedBefore, applicable.ConfirmedAfter);
    }

    private TuningRun Finish(DateTimeOffset now, int labelCount, string result, IReadOnlyList<TuningChange> made, TuningScore? before, TuningScore? after)
    {
        _store.SaveState(State with { LastRun = now, LastLabelCount = labelCount, LastResult = result });
        LogRun(_logger, result);
        return new TuningRun { At = now, Result = result, Changes = made, Before = before, After = after };
    }

    /// <summary>
    /// Prompts labelled from feedback that says which tier a decision needed (retried, corrected, overridden …), joined
    /// to the activity entry it is about. Only previews that hold the whole prompt are used, and prompts in the
    /// evaluation set keep their hand label.
    /// </summary>
    private (IReadOnlyList<EvaluationCase> Cases, Dictionary<string, HashSet<FeedbackKind>> Kinds) AutomaticLabels(
        IReadOnlyList<EvaluationCase> confirmed, TuningOptions options, double judgeWeight, DateTimeOffset now)
    {
        var kinds = new Dictionary<string, HashSet<FeedbackKind>>(StringComparer.Ordinal);
        if (options.FeedbackWeight <= 0 && judgeWeight <= 0)
        {
            return ([], kinds);
        }

        DateTimeOffset since = now - TimeSpan.FromDays(options.FeedbackDays);
        IReadOnlyList<RoutingFeedback> feedback = _feedback.Between(since, now.AddSeconds(1));
        if (feedback.Count == 0)
        {
            return ([], kinds);
        }

        var entries = _activity.Between(since - TimeSpan.FromHours(1), now.AddSeconds(1)).ToDictionary(e => e.Id);
        var labelled = new HashSet<string>(confirmed.Select(c => c.Prompt), StringComparer.Ordinal);
        var needed = new Dictionary<string, (int Tier, bool LocalOnly)>(StringComparer.Ordinal);
        foreach (RoutingFeedback item in feedback)
        {
            if (item.NeededTier is not { } tier
                || (item.Kind == FeedbackKind.Judged ? judgeWeight : options.FeedbackWeight) <= 0
                || !entries.TryGetValue(item.EntryId, out RoutingActivityEntry? entry)
                || entry.ToolContinuation
                || string.IsNullOrWhiteSpace(entry.PromptPreview)
                || entry.PromptPreview.EndsWith('…')
                || labelled.Contains(entry.PromptPreview))
            {
                continue;
            }

            string prompt = entry.PromptPreview;
            needed[prompt] = needed.TryGetValue(prompt, out (int Tier, bool LocalOnly) seen)
                ? (Math.Max(seen.Tier, tier), seen.LocalOnly || entry.LocalOnly)
                : (tier, entry.LocalOnly);
            (kinds.TryGetValue(prompt, out HashSet<FeedbackKind>? set) ? set : kinds[prompt] = []).Add(item.Kind);
        }

        return (needed.Select(n => new EvaluationCase { Prompt = n.Key, ExpectedTier = n.Value.Tier, LocalOnly = n.Value.LocalOnly, Note = "from feedback" }).ToArray(), kinds);
    }

    /// <summary>
    /// A prompt labelled only by the judge counts at <paramref name="judgeWeight"/> (<see cref="TuningOptions.JudgeWeight"/>,
    /// scaled by its agreement with the hand labels), one with other feedback at <see cref="TuningOptions.FeedbackWeight"/>,
    /// one with both at the higher of the two.
    /// </summary>
    private static double Weight(HashSet<FeedbackKind> kinds, TuningOptions options, double judgeWeight)
    {
        bool judged = kinds.Contains(FeedbackKind.Judged);
        bool other = kinds.Count > (judged ? 1 : 0);
        return judged && other ? Math.Max(judgeWeight, options.FeedbackWeight) : judged ? judgeWeight : options.FeedbackWeight;
    }

    private static TuningChange ThresholdChange(ThresholdStep step, TuningOptions options, Evidence evidence, DateTimeOffset now)
    {
        int fromSet = step.Fixed.Count(evidence.Confirmed.Contains);
        int fromFeedback = step.Fixed.Count - fromSet;
        string where = step.Route is null ? "the default threshold" : $"the threshold of {step.Route}";
        string at = TuningChange.Format(step.Target);
        var why = new List<string>
        {
            $"At {at}, {(step.Fixed.Count == 1 ? "1 labelled prompt routes to the tier it needs that does not" : $"{step.Fixed.Count} labelled prompts route to the tier they need that do not")} now " +
            $"({Sources(fromSet, fromFeedback, step.Fixed.Where(c => !evidence.Confirmed.Contains(c)), evidence)})" +
            (step.Fixed.Count > 0 ? $", e.g. \"{TuningChange.Preview(step.Fixed[0].Prompt, 60)}\"" : string.Empty),
            step.Broken.Count == 0
                ? "none would route lower than they need"
                : $"{Plural(step.Broken.Count, "prompt")} would no longer match their label, none of them confirmed prompts routed lower",
        };
        string text = string.Join("; ", why) + ".";
        if (step.Proposed != step.Target)
        {
            text += $" {Capitalize(where)} moves {options.MaxStep.ToString("0.00", CultureInfo.InvariantCulture)} per run toward it.";
        }

        return new TuningChange
        {
            Timestamp = now,
            Kind = step.Route is null ? TuningChangeKind.DefaultThreshold : TuningChangeKind.RouteThreshold,
            Route = step.Route,
            OldValue = step.Current,
            NewValue = step.Proposed,
            Target = step.Target,
            Why = text,
            Fixed = step.Fixed.Count,
            Broken = step.Broken.Count,
            Gain = step.Gain,
            Examples = step.Fixed.Take(MaxExamples).Select(c => TuningChange.Preview(c.Prompt, 120)).ToArray(),
        };
    }

    /// <summary>"3 from the evaluation set, 6 from feedback: retried, corrected".</summary>
    private static string Sources(int fromSet, int fromFeedback, IEnumerable<EvaluationCase> automatic, Evidence evidence)
    {
        var parts = new List<string>();
        if (fromSet > 0)
        {
            parts.Add($"{fromSet} from the evaluation set");
        }

        if (fromFeedback > 0)
        {
            string[] seen = automatic
                .SelectMany(c => evidence.Kinds.TryGetValue(c.Prompt, out HashSet<FeedbackKind>? k) ? k : [])
                .Distinct().Order().Select(k => k == FeedbackKind.Judged && evidence.JudgeNote is { } note ? $"{Describe(k)} ({note})" : Describe(k)).ToArray();
            parts.Add($"{fromFeedback} from feedback" + (seen.Length > 0 ? ": " + string.Join(", ", seen) : string.Empty));
        }

        return string.Join(", ", parts);
    }

    private static string Describe(FeedbackKind kind) => kind switch
    {
        FeedbackKind.Retried => "retried",
        FeedbackKind.Corrected => "corrected",
        FeedbackKind.Overridden => "overridden with a callout",
        FeedbackKind.ToolErrors => "tool errors",
        FeedbackKind.Judged => "graded low by the judge",
        _ => "empty replies",
    };

    /// <summary>Apply, suggest or leave alone a change, given what happened to the same setting before.</summary>
    private static Disposition Decide(TuningChange change, TuningOptions options, IReadOnlyList<TuningChange> history, DateTimeOffset now)
    {
        TuningChange? undone = history
            .Where(c => c.Setting == change.Setting && c.State is TuningChangeState.Undone or TuningChangeState.Dismissed)
            .MaxBy(c => c.UndoneAt);
        if (undone is not null)
        {
            bool held = now - (undone.UndoneAt ?? undone.Timestamp) < TimeSpan.FromDays(options.HoldDays);
            bool stronger = change.Gain >= undone.Gain * options.StrongerEvidence;
            return held && !stronger ? Disposition.Hold : Disposition.Suggest;
        }

        return options.Mode == TuningMode.Suggest ? Disposition.Suggest : Disposition.Apply;
    }

    private void Suggest(TuningChange change, List<TuningChange> made)
    {
        TuningChange[] pending = _store.Changes().Where(c => c.State == TuningChangeState.Suggested && c.Setting == change.Setting).ToArray();
        if (pending.Any(p => p.NewValue == change.NewValue && p.OldValue == change.OldValue))
        {
            return;
        }

        foreach (TuningChange old in pending)
        {
            Save(old with { State = TuningChangeState.Expired });
        }

        made.Add(Save(change with { Id = NextId(), State = TuningChangeState.Suggested }));
    }

    /// <summary>Applies a threshold change as its own configuration version, unless the setting changed meanwhile.</summary>
    private TuningChange? ApplyThreshold(TuningChange change, string actor, string? note = null)
    {
        if (!Matches(_provider.Current.Configuration, change, change.OldValue))
        {
            return null;
        }

        long id = change.Id != 0 ? change.Id : NextId();
        bool Make(RouterConfiguration next)
        {
            if (!Matches(next, change, change.OldValue))
            {
                return false;
            }

            Set(next, change, change.NewValue);
            return true;
        }

        return _provider.ApplyKeepingSaveState(Make, Source, actor, note ?? $"change {id}: {change.Summary}. {change.Why}") is { } applied
            ? Save(change with { Id = id, State = TuningChangeState.Applied, Version = applied.Runtime.HistoryVersion, Actor = actor })
            : null;
    }

    private async Task LearnUtterancesAsync(
        IReadOnlyList<EvaluationCase> confirmed,
        IReadOnlyList<EvaluationCase> automatic,
        TuningOptions options,
        Evidence evidence,
        IReadOnlyList<TuningChange> history,
        DateTimeOffset now,
        List<TuningChange> made,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<EvaluationCase> cases = [.. confirmed, .. automatic];
        RouterRuntime runtime = _provider.Current;
        EvaluationReport before = await new RoutingEvaluator(runtime.EmbeddingProvider, runtime.Configuration).EvaluateAsync(cases, cancellationToken).ConfigureAwait(false);

        // Prompts in the evaluation set that their route misses, at most a few per route.
        TuningChange[] candidates = before.Results.Take(confirmed.Count)
            .Where(r => !r.Correct && r.RouteCorrect == false && (!r.Case.LocalOnly || options.LocalOnlyUtterances) && r.Case.Prompt.Length <= MaxUtteranceLength)
            .Select(r => (Result: r, Route: runtime.Configuration.Routes.FirstOrDefault(x => string.Equals(x.Name, r.Case.ExpectedRoute, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Route is not null && !x.Route.Utterances.Contains(x.Result.Case.Prompt.Trim(), StringComparer.OrdinalIgnoreCase))
            .GroupBy(x => x.Route!.Name, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.Take(options.MaxUtterancesPerRoute))
            .Take(MaxUtteranceCandidates)
            .Select(x => new TuningChange
            {
                Timestamp = now,
                Kind = TuningChangeKind.Utterance,
                Route = x.Route!.Name,
                Utterance = x.Result.Case.Prompt.Trim(),
            })
            .Where(c => Decide(c, options, history, now) != Disposition.Hold)
            .ToArray();

        foreach (TuningChange candidate in candidates)
        {
            runtime = _provider.Current;
            RouterConfiguration next = runtime.Configuration.Clone();
            if (!Matches(next, candidate, null))
            {
                continue;
            }

            Set(next, candidate, null);
            EvaluationReport baseline = await new RoutingEvaluator(runtime.EmbeddingProvider, runtime.Configuration).EvaluateAsync(cases, cancellationToken).ConfigureAwait(false);
            EvaluationReport after = await new RoutingEvaluator(runtime.EmbeddingProvider, next).EvaluateAsync(cases, cancellationToken).ConfigureAwait(false);
            if (Assess(candidate, baseline, after, confirmed.Count, evidence) is not { } change)
            {
                continue;
            }

            if (Decide(change, options, history, now) == Disposition.Apply)
            {
                // Made on the configuration running by now, which may have changed during the evaluation.
                long id = NextId();
                bool Make(RouterConfiguration latest)
                {
                    if (!Matches(latest, candidate, null))
                    {
                        return false;
                    }

                    Set(latest, candidate, null);
                    return true;
                }

                if (_provider.ApplyKeepingSaveState(Make, Source, Actor, $"change {id}: {change.Summary}. {change.Why}") is { } applied)
                {
                    made.Add(Save(change with { Id = id, State = TuningChangeState.Applied, Version = applied.Runtime.HistoryVersion, Actor = Actor }));
                }
            }
            else
            {
                Suggest(change, made);
            }
        }
    }

    /// <summary>The change with its evidence when adding the utterance is safe and helps; otherwise <see langword="null"/>.</summary>
    private static TuningChange? Assess(TuningChange candidate, EvaluationReport before, EvaluationReport after, int confirmedCount, Evidence evidence)
    {
        static (int Correct, int TooLow) Tally(IEnumerable<EvaluationResult> results) => (results.Count(r => r.Correct), results.Count(r => r.ActualTier < r.Case.ExpectedTier));

        // Route health: every other confirmed prompt routes at least as well, nothing more goes too low or leaves the machine.
        IEnumerable<EvaluationResult> Others(EvaluationReport report) =>
            report.Results.Take(confirmedCount).Where(r => !string.Equals(r.Case.Prompt.Trim(), candidate.Utterance, StringComparison.OrdinalIgnoreCase));
        (int Correct, int TooLow) othersBefore = Tally(Others(before)), othersAfter = Tally(Others(after));
        (int Correct, int TooLow) allBefore = Tally(before.Results), allAfter = Tally(after.Results);
        bool itself = after.Results.Take(confirmedCount).Any(r => string.Equals(r.Case.Prompt.Trim(), candidate.Utterance, StringComparison.OrdinalIgnoreCase) && r.Correct);
        if (!itself
            || othersAfter.Correct < othersBefore.Correct
            || othersAfter.TooLow > othersBefore.TooLow
            || allAfter.TooLow > allBefore.TooLow
            || after.LocalOnlyMissed > before.LocalOnlyMissed)
        {
            return null;
        }

        EvaluationCase[] fixedCases = Enumerable.Range(0, after.Results.Count).Where(i => after.Results[i].Correct && !before.Results[i].Correct).Select(i => after.Results[i].Case).ToArray();
        int broken = Enumerable.Range(0, after.Results.Count).Count(i => !after.Results[i].Correct && before.Results[i].Correct);
        int fromSet = fixedCases.Count(evidence.Confirmed.Contains);
        EvaluationResult missed = before.Results.First(r => string.Equals(r.Case.Prompt.Trim(), candidate.Utterance, StringComparison.OrdinalIgnoreCase));
        string why = $"\"{TuningChange.Preview(candidate.Utterance, 60)}\" is labelled {candidate.Route} (tier {missed.Case.ExpectedTier}) " +
            $"but routes {(missed.ActualRoute is { } actual ? "to " + actual : "to no route")} (tier {missed.ActualTier}). " +
            $"As an example of {candidate.Route} it routes {Plural(fixedCases.Length, "labelled prompt")} to {(fixedCases.Length == 1 ? "its" : "their")} tier " +
            $"({Sources(fromSet, fixedCases.Length - fromSet, fixedCases.Where(c => !evidence.Confirmed.Contains(c)), evidence)})" +
            (broken == 0 ? " and no other prompt worse." : $"; {Plural(broken, "prompt")} would no longer match their label, none routed lower.");
        return candidate with
        {
            Why = why,
            Fixed = fixedCases.Length,
            Broken = broken,
            Gain = fixedCases.Length - broken,
            Examples = fixedCases.Take(MaxExamples).Select(c => TuningChange.Preview(c.Prompt, 120)).ToArray(),
        };
    }

    /// <summary>Undoes one applied change: only its setting goes back (with later steps of the same setting).</summary>
    public TuningChange Undo(long id, string actor)
    {
        _busy.Wait();
        try
        {
            TuningChange change = Find(id);
            if (change.Kind == TuningChangeKind.RouteModel && change.State == TuningChangeState.Trying)
            {
                // A trial changed no configuration: stopping it is enough, and it is not tried again for a while.
                return Save(change with { State = TuningChangeState.Undone, UndoneAt = _time.GetUtcNow(), Actor = actor });
            }

            if (change.State != TuningChangeState.Applied)
            {
                throw new TuningConflictException($"Change {id} is {change.State.ToString().ToLowerInvariant()}, not applied.");
            }

            // Later steps of the same threshold built on this one, so they go too; other settings stay as they are.
            TuningChange[] chain = _store.Changes()
                .Where(c => c.State == TuningChangeState.Applied && c.Setting == change.Setting && c.Id >= id)
                .OrderBy(c => c.Id).ToArray();
            bool Revert(RouterConfiguration next)
            {
                if (change.Kind == TuningChangeKind.RouteModel)
                {
                    if (!MatchesModel(next, change, chain[^1].NewModel))
                    {
                        throw new TuningConflictException(
                            $"The {Setting(change)} was changed since (it is {Find(next, change)?.PreferredModel ?? "the tier's choice"}), so undoing change {id} would overwrite that. Change it on the Routes page instead.");
                    }

                    SetModel(next, change, change.OldModel);
                }
                else if (change.Kind != TuningChangeKind.Utterance && !Matches(next, change, chain[^1].NewValue))
                {
                    throw new TuningConflictException(
                        $"The {Setting(change)} was changed since (it is {TuningChange.Format(Current(next, change))}), so undoing change {id} would overwrite that. Change it on the Routes page instead.");
                }
                else
                {
                    Set(next, change, change.OldValue, remove: true);
                }

                return true;
            }

            DateTimeOffset now = _time.GetUtcNow();
            RouterRuntime runtime = _provider.ApplyKeepingSaveState(Revert, UndoSource, actor, $"undo of auto-tune change {id}: {change.Summary}")!.Value.Runtime;
            TuningChange undone = change;
            foreach (TuningChange step in chain)
            {
                TuningChange saved = Save(step with { State = TuningChangeState.Undone, UndoneAt = now, Actor = actor, UndoVersion = runtime.HistoryVersion });
                if (step.Id == id)
                {
                    undone = saved;
                }
            }

            return undone;
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>Undoes every applied change in one configuration version; settings someone changed since are left alone.</summary>
    public (int Undone, IReadOnlyList<string> Skipped) UndoAll(string actor)
    {
        _busy.Wait();
        try
        {
            DateTimeOffset stopped = _time.GetUtcNow();
            TuningChange[] trials = _store.Changes().Where(c => c.Kind == TuningChangeKind.RouteModel && c.State == TuningChangeState.Trying).ToArray();
            foreach (TuningChange trial in trials)
            {
                Save(trial with { State = TuningChangeState.Undone, UndoneAt = stopped, Actor = actor });
            }

            TuningChange[] applied = _store.Changes().Where(c => c.State == TuningChangeState.Applied).ToArray();
            if (applied.Length == 0)
            {
                return (trials.Length, []);
            }

            var undone = new List<TuningChange>();
            var skipped = new List<string>();
            bool Revert(RouterConfiguration next)
            {
                // Decided again on each copy: another version may have changed a setting since the last one.
                undone.Clear();
                skipped.Clear();
                foreach (IGrouping<string, TuningChange> setting in applied.GroupBy(c => c.Setting))
                {
                    TuningChange first = setting.MinBy(c => c.Id)!, last = setting.MaxBy(c => c.Id)!;
                    if (first.Kind == TuningChangeKind.RouteModel
                        ? !MatchesModel(next, first, last.NewModel)
                        : first.Kind != TuningChangeKind.Utterance && !Matches(next, first, last.NewValue))
                    {
                        skipped.Add($"the {Setting(first)} (changed since)");
                        continue;
                    }

                    if (first.Kind == TuningChangeKind.RouteModel)
                    {
                        SetModel(next, first, first.OldModel);
                    }
                    else
                    {
                        Set(next, first, first.OldValue, remove: true);
                    }

                    undone.AddRange(setting);
                }

                return undone.Count > 0;
            }

            // The note counts what would be undone now; a retry on a newer version rarely changes that.
            int count = Revert(_provider.Current.Configuration.Clone()) ? undone.Count : 0;
            DateTimeOffset now = _time.GetUtcNow();
            if (count > 0 && _provider.ApplyKeepingSaveState(Revert, UndoSource, actor, $"undo of all automatic changes ({count})") is { } reverted)
            {
                foreach (TuningChange change in undone)
                {
                    Save(change with { State = TuningChangeState.Undone, UndoneAt = now, Actor = actor, UndoVersion = reverted.Runtime.HistoryVersion });
                }
            }
            else
            {
                undone.Clear();
            }

            return (undone.Count + trials.Length, skipped);
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>Applies a suggestion.</summary>
    public TuningChange ApplySuggestion(long id, string actor)
    {
        _busy.Wait();
        try
        {
            TuningChange change = Find(id);
            if (change.State != TuningChangeState.Suggested)
            {
                throw new TuningConflictException($"Change {id} is {change.State.ToString().ToLowerInvariant()}, not a suggestion.");
            }

            bool Make(RouterConfiguration next)
            {
                if (!Matches(next, change, change.OldValue))
                {
                    Save(change with { State = TuningChangeState.Expired });
                    throw new TuningConflictException($"The {Setting(change)} changed since this was suggested; it no longer applies.");
                }

                Set(next, change, change.NewValue);
                return true;
            }

            RouterRuntime runtime = _provider.ApplyKeepingSaveState(Make, Source, actor, $"change {id} (suggested): {change.Summary}. {change.Why}")!.Value.Runtime;
            return Save(change with { State = TuningChangeState.Applied, Version = runtime.HistoryVersion, Actor = actor });
        }
        finally
        {
            _busy.Release();
        }
    }

    /// <summary>Turns a suggestion down; the same change is held like an undone one.</summary>
    public TuningChange Dismiss(long id, string actor)
    {
        TuningChange change = Find(id);
        return change.State != TuningChangeState.Suggested
            ? throw new TuningConflictException($"Change {id} is {change.State.ToString().ToLowerInvariant()}, not a suggestion.")
            : Save(change with { State = TuningChangeState.Dismissed, UndoneAt = _time.GetUtcNow(), Actor = actor });
    }

    /// <summary>Pauses or resumes learning.</summary>
    public TuningState SetPaused(bool paused)
    {
        TuningState state = State with { Paused = paused };
        _store.SaveState(state);
        _trials?.Refresh();
        return state;
    }

    /// <summary>
    /// The applied change that routed a decision differently, when there is one: a threshold change in force whose old
    /// value would have decided the other way (matched now but not before, or the reverse).
    /// </summary>
    public TuningChange? Explain(PromptClassificationResult result)
    {
        IReadOnlyList<TuningChange> inForce = Volatile.Read(ref _inForce);
        if (inForce.Count == 0
            || result.Similarity is not { } similarity
            || result.Reason is not (RoutingReason.SemanticMatch or RoutingReason.AmbiguousMatchEscalation or RoutingReason.NoMatchFallback or RoutingReason.ComplexityEscalation)
            || (result.RouteName ?? (result.TopScores.Count > 0 ? result.TopScores[0].Route.Name : null)) is not { } routeName)
        {
            return null;
        }

        RouterConfiguration configuration = _provider.Current.Configuration;
        RouteDefinition? route = configuration.Routes.FirstOrDefault(r => string.Equals(r.Name, routeName, StringComparison.OrdinalIgnoreCase));
        if (route is null)
        {
            return null;
        }

        TuningChange? change = route.SimilarityThreshold is not null
            ? inForce.LastOrDefault(c => c.Kind == TuningChangeKind.RouteThreshold && string.Equals(c.Route, route.Name, StringComparison.OrdinalIgnoreCase))
            : inForce.LastOrDefault(c => c.Kind == TuningChangeKind.DefaultThreshold);
        float threshold = route.SimilarityThreshold ?? configuration.DefaultSimilarityThreshold;
        if (change is null || (change.NewValue ?? configuration.DefaultSimilarityThreshold) != threshold)
        {
            return null;
        }

        float old = change.OldValue ?? configuration.DefaultSimilarityThreshold;
        bool matched = result.RouteName is not null;
        return (matched && similarity < old) || (!matched && similarity >= old) ? change : null;
    }

    /// <summary>Suggestions whose setting no longer has the value they start from are expired.</summary>
    private void ExpireStale(RouterConfiguration configuration)
    {
        foreach (TuningChange suggestion in _store.Changes().Where(c => c.State == TuningChangeState.Suggested))
        {
            if (!Matches(configuration, suggestion, suggestion.Kind == TuningChangeKind.Utterance ? null : suggestion.OldValue))
            {
                Save(suggestion with { State = TuningChangeState.Expired });
            }
        }
    }

    /// <summary>
    /// Whether the change's setting has <paramref name="value"/> in <paramref name="configuration"/>; for an utterance,
    /// whether the route exists and does not have it yet.
    /// </summary>
    private static bool Matches(RouterConfiguration configuration, TuningChange change, float? value) => change.Kind switch
    {
        TuningChangeKind.DefaultThreshold => configuration.DefaultSimilarityThreshold == value,
        TuningChangeKind.RouteThreshold => Find(configuration, change) is { } route && route.SimilarityThreshold == value,
        TuningChangeKind.RouteModel => MatchesModel(configuration, change, change.OldModel),
        _ => Find(configuration, change) is { } route && !route.Utterances.Contains(change.Utterance!, StringComparer.OrdinalIgnoreCase),
    };

    private static float? Current(RouterConfiguration configuration, TuningChange change) => change.Kind == TuningChangeKind.DefaultThreshold
        ? configuration.DefaultSimilarityThreshold
        : Find(configuration, change)?.SimilarityThreshold;

    /// <summary>Sets the change's threshold to <paramref name="value"/>, or adds its utterance (removes it with <paramref name="remove"/>).</summary>
    private static void Set(RouterConfiguration configuration, TuningChange change, float? value, bool remove = false)
    {
        switch (change.Kind)
        {
            case TuningChangeKind.DefaultThreshold:
                configuration.DefaultSimilarityThreshold = value ?? configuration.DefaultSimilarityThreshold;
                break;
            case TuningChangeKind.RouteThreshold when Find(configuration, change) is { } route:
                route.SimilarityThreshold = value;
                break;
            case TuningChangeKind.Utterance when Find(configuration, change) is { } route:
                if (remove)
                {
                    string? existing = route.Utterances.FirstOrDefault(u => string.Equals(u, change.Utterance, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null && route.Utterances.Count > 1)
                    {
                        route.Utterances.Remove(existing);
                    }
                }
                else
                {
                    route.Utterances.Add(change.Utterance!);
                }

                break;
        }
    }

    private static RouteDefinition? Find(RouterConfiguration configuration, TuningChange change) =>
        configuration.Routes.FirstOrDefault(r => string.Equals(r.Name, change.Route, StringComparison.OrdinalIgnoreCase));

    private static string Setting(TuningChange change) => change.Kind switch
    {
        TuningChangeKind.DefaultThreshold => "default threshold",
        TuningChangeKind.RouteThreshold => $"threshold of {change.Route}",
        TuningChangeKind.RouteModel => $"model of {change.Route}",
        _ => $"utterance of {change.Route}",
    };

    private TuningChange Find(long id) =>
        _store.Changes().FirstOrDefault(c => c.Id == id) ?? throw new TuningConflictException($"There is no learned change {id}.", notFound: true);

    private long NextId() => Interlocked.Increment(ref _lastId);

    private TuningChange Save(TuningChange change)
    {
        lock (_gate)
        {
            _store.Save(change);
            Refresh(_store.Changes());
        }

        if (change.Kind == TuningChangeKind.RouteModel)
        {
            _trials?.Refresh();
        }

        return change;
    }

    private void Refresh(IReadOnlyList<TuningChange> changes)
    {
        Volatile.Write(ref _inForce, changes.Where(c => c.State == TuningChangeState.Applied && c.Kind is TuningChangeKind.DefaultThreshold or TuningChangeKind.RouteThreshold).OrderBy(c => c.Id).ToArray());
        Volatile.Write(ref _models, changes.Where(c => c.State == TuningChangeState.Applied && c.Kind == TuningChangeKind.RouteModel).OrderBy(c => c.Id).ToArray());
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    [LoggerMessage(Level = LogLevel.Information, Message = "Automatic tuning: {Result}")]
    private static partial void LogRun(ILogger logger, string result);

    private enum Disposition
    {
        Apply,
        Suggest,
        Hold,
    }

    private sealed record Evidence(HashSet<EvaluationCase> Confirmed, Dictionary<string, HashSet<FeedbackKind>> Kinds, string? JudgeNote);
}
