namespace SemanticRouter;

/// <summary>One similarity threshold the tuner would move: a bounded step toward the value calibration recommends.</summary>
public sealed record ThresholdStep
{
    /// <summary>The route whose own threshold moves, or <see langword="null"/> for <see cref="SemanticRouterOptions.DefaultSimilarityThreshold"/>.</summary>
    public required string? Route { get; init; }

    /// <summary>The value now (for a route, <see langword="null"/>: it uses the default).</summary>
    public float? Current { get; init; }

    /// <summary>The value after the step (for a route, <see langword="null"/>: use the default).</summary>
    public float? Proposed { get; init; }

    /// <summary>Where calibration says it should end up (for a route, <see langword="null"/>: the default).</summary>
    public float? Target { get; init; }

    /// <summary>Prompts routed to their tier at <see cref="Target"/> that are not now.</summary>
    public required IReadOnlyList<EvaluationCase> Fixed { get; init; }

    /// <summary>Prompts routed to their tier now that are not at <see cref="Target"/>.</summary>
    public required IReadOnlyList<EvaluationCase> Broken { get; init; }

    /// <summary>Correct prompts gained at <see cref="Target"/>, automatic labels counted at their weight.</summary>
    public required double Gain { get; init; }
}

/// <summary>How one set of labelled prompts routes.</summary>
public sealed record TuningScore
{
    /// <summary>Prompts in the set.</summary>
    public required int Total { get; init; }

    /// <summary>Prompts routed to their tier (and local when labelled local-only).</summary>
    public required int Correct { get; init; }

    /// <summary>Prompts routed below their tier.</summary>
    public required int TooLow { get; init; }

    /// <summary>Prompts routed above their tier.</summary>
    public required int TooHigh { get; init; }

    /// <summary>Prompts labelled local-only that would leave the machine.</summary>
    public required int LocalOnlyMissed { get; init; }
}

/// <summary>The steps the tuner found, and how the two sets route before and after all of them.</summary>
public sealed record ThresholdTuning
{
    /// <summary>The accepted steps, in the order they were tried (the default threshold first).</summary>
    public required IReadOnlyList<ThresholdStep> Steps { get; init; }

    /// <summary>The confirmed (hand-labelled) set with the thresholds as they are.</summary>
    public required TuningScore ConfirmedBefore { get; init; }

    /// <summary>The confirmed set after every step.</summary>
    public required TuningScore ConfirmedAfter { get; init; }

    /// <summary>The automatically labelled set with the thresholds as they are.</summary>
    public required TuningScore AutomaticBefore { get; init; }

    /// <summary>The automatically labelled set after every step.</summary>
    public required TuningScore AutomaticAfter { get; init; }
}

/// <summary>Limits for <see cref="ThresholdTuner"/>.</summary>
public sealed class ThresholdTuningOptions
{
    /// <summary>The most a threshold moves in one run. Calibration's value is reached over several runs.</summary>
    public float MaxStep { get; set; } = 0.02f;

    /// <summary>How much an automatically labelled prompt counts next to a confirmed one (1).</summary>
    public double AutomaticWeight { get; set; } = 0.5;

    /// <summary>
    /// Each automatically labelled prompt's own weight, in the order of the automatic cases; overrides
    /// <see cref="AutomaticWeight"/> where given.
    /// </summary>
    public IReadOnlyList<double>? AutomaticWeights { get; set; }

    /// <summary>The weighted number of prompts the calibrated thresholds must gain before anything moves.</summary>
    public double MinimumGain { get; set; } = 1;

    /// <summary>
    /// Which thresholds may move: called with a route name, or <see langword="null"/> for the default threshold.
    /// <see langword="null"/>: all of them.
    /// </summary>
    public Func<string?, bool>? CanChange { get; set; }
}

/// <summary>
/// Moves similarity thresholds toward the values calibration recommends, in bounded steps, and only when that is safe:
/// a step must not route fewer confirmed prompts correctly, route more prompts (confirmed or automatic) below the
/// tier they need, or let a local-only prompt leave the machine.
/// </summary>
/// <remarks>
/// Confirmed prompts are the hand-labelled evaluation set. Automatic ones come from feedback (a retry, a correction)
/// and count at <see cref="ThresholdTuningOptions.AutomaticWeight"/> when choosing the target; the safety checks hold
/// on their own for the confirmed set. The prompts are embedded once, so a run costs one embedding batch.
/// </remarks>
public sealed class ThresholdTuner
{
    private const double Epsilon = 1e-9;
    private static readonly float[] Grid = Enumerable.Range(0, 101).Select(i => i / 100f).ToArray();

    private readonly RoutingEvaluator _evaluator;
    private readonly SemanticRouterOptions _options;

    /// <summary>Creates a tuner for the configuration <paramref name="evaluator"/> evaluates.</summary>
    public ThresholdTuner(RoutingEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        _evaluator = evaluator;
        _options = evaluator.Options;
    }

    /// <summary>Embeds both sets and finds the steps.</summary>
    /// <param name="confirmed">Hand-labelled prompts.</param>
    /// <param name="automatic">Prompts labelled from feedback.</param>
    /// <param name="options">Limits.</param>
    /// <param name="cancellationToken">Cancels the embedding call.</param>
    public async Task<ThresholdTuning> TuneAsync(
        IReadOnlyList<EvaluationCase> confirmed,
        IReadOnlyList<EvaluationCase> automatic,
        ThresholdTuningOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(confirmed);
        ArgumentNullException.ThrowIfNull(automatic);
        PreparedEvaluation prepared = await _evaluator.PrepareAsync([.. confirmed, .. automatic], cancellationToken).ConfigureAwait(false);
        return Tune(prepared, confirmed.Count, options);
    }

    /// <summary>Finds the steps on a prepared set whose first <paramref name="confirmedCount"/> cases are confirmed.</summary>
    public ThresholdTuning Tune(PreparedEvaluation prepared, int confirmedCount, ThresholdTuningOptions options)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(confirmedCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(confirmedCount, prepared.Cases.Count);

        var weights = new double[prepared.Cases.Count];
        for (int i = 0; i < weights.Length; i++)
        {
            weights[i] = i < confirmedCount ? 1
                : options.AutomaticWeights is { } each && i - confirmedCount < each.Count ? each[i - confirmedCount]
                : options.AutomaticWeight;
        }

        Func<string?, bool> canChange = options.CanChange ?? (_ => true);
        var current = new Settings(_options.DefaultSimilarityThreshold, _options.Routes.ToDictionary(r => r.Name, r => r.SimilarityThreshold, StringComparer.OrdinalIgnoreCase));
        Outcome baseline = Score(prepared, current, weights, confirmedCount);
        Settings target = Calibrate(prepared, current, baseline, weights, confirmedCount, canChange);
        Outcome best = Score(prepared, target, weights, confirmedCount);

        var steps = new List<ThresholdStep>();
        Settings state = current;
        Outcome reached = baseline;
        if (best.Weighted - baseline.Weighted >= options.MinimumGain - Epsilon)
        {
            // The default first: routes without their own threshold follow it.
            foreach (string? route in _options.Routes.Select(r => (string?)r.Name).Prepend(null))
            {
                if (Step(state, target, route, options.MaxStep) is not { } next)
                {
                    continue;
                }

                // Justified by what this setting alone gains at its target; safe if the step keeps every guarantee.
                Settings alone = route is null ? state with { Default = target.Default } : state.With(route, target.Routes[route]);
                Outcome atTarget = Score(prepared, alone, weights, confirmedCount);
                Outcome before = Score(prepared, state, weights, confirmedCount);
                double gain = atTarget.Weighted - before.Weighted;
                if (gain <= Epsilon)
                {
                    continue;
                }

                Outcome after = Score(prepared, next, weights, confirmedCount);
                if (!Safe(after, baseline))
                {
                    continue;
                }

                steps.Add(new ThresholdStep
                {
                    Route = route,
                    Current = route is null ? state.Default : state.Routes[route],
                    Proposed = route is null ? next.Default : next.Routes[route],
                    Target = route is null ? target.Default : target.Routes[route],
                    Fixed = Changed(prepared, before, atTarget),
                    Broken = Changed(prepared, atTarget, before),
                    Gain = gain,
                });
                state = next;
                reached = after;
            }
        }

        return new ThresholdTuning
        {
            Steps = steps,
            ConfirmedBefore = baseline.Confirmed,
            ConfirmedAfter = reached.Confirmed,
            AutomaticBefore = baseline.Automatic,
            AutomaticAfter = reached.Automatic,
        };
    }

    /// <summary>Nothing gets worse than <paramref name="baseline"/> where it matters.</summary>
    private static bool Safe(Outcome candidate, Outcome baseline) =>
        candidate.Confirmed.Correct >= baseline.Confirmed.Correct
        && candidate.Confirmed.TooLow <= baseline.Confirmed.TooLow
        && candidate.Confirmed.LocalOnlyMissed + candidate.Automatic.LocalOnlyMissed <= baseline.Confirmed.LocalOnlyMissed + baseline.Automatic.LocalOnlyMissed
        && candidate.WeightedTooLow <= baseline.WeightedTooLow + Epsilon
        && candidate.Weighted >= baseline.Weighted - Epsilon;

    /// <summary>Cases correct in <paramref name="to"/> and not in <paramref name="from"/>.</summary>
    private static EvaluationCase[] Changed(PreparedEvaluation prepared, Outcome from, Outcome to) =>
        Enumerable.Range(0, prepared.Cases.Count).Where(i => to.Correct[i] && !from.Correct[i]).Select(i => prepared.Cases[i]).ToArray();

    /// <summary>The settings after one bounded step of <paramref name="route"/> (<see langword="null"/>: the default), or <see langword="null"/> when it is already there.</summary>
    private static Settings? Step(Settings state, Settings target, string? route, float maxStep)
    {
        if (route is null)
        {
            float next = Toward(state.Default, target.Default, maxStep);
            return next == state.Default ? null : state with { Default = next };
        }

        float? own = state.Routes[route];
        float? wanted = target.Routes[route];
        if (own == wanted)
        {
            return null;
        }

        float effective = own ?? state.Default;
        float? moved = wanted is { } value
            ? Toward(effective, value, maxStep)
            : Math.Abs(effective - state.Default) <= maxStep + 1e-6f ? null : Toward(effective, state.Default, maxStep);
        return moved == own ? null : state.With(route, moved);
    }

    private static float Toward(float from, float to, float maxStep)
    {
        float distance = to - from;
        return Math.Abs(distance) <= maxStep + 1e-6f ? to : MathF.Round((from + (Math.Sign(distance) * maxStep)) * 100f) / 100f;
    }

    /// <summary>
    /// The thresholds that route the most prompts correctly (weighted), the way <see cref="RoutingEvaluator.Calibrate"/>
    /// chooses them: the default first, then each route, moving only for a strict improvement. Only safe values are
    /// candidates, so the steps never creep toward a target they could not reach.
    /// </summary>
    private Settings Calibrate(PreparedEvaluation prepared, Settings current, Outcome baseline, double[] weights, int confirmedCount, Func<string?, bool> canChange)
    {
        Settings best = current;
        if (canChange(null))
        {
            best = Grid.Append(current.Default).Distinct()
                .Select(t => current with { Default = t })
                .Select(s => (Settings: s, Outcome: Score(prepared, s, weights, confirmedCount)))
                .Where(o => Safe(o.Outcome, baseline))
                .OrderByDescending(o => o.Outcome.Weighted)
                .ThenBy(o => o.Outcome.WeightedTooLow)
                .ThenBy(o => Math.Abs(o.Settings.Default - current.Default))
                .First().Settings;
        }

        Settings defaultOnly = best;
        foreach (RouteDefinition route in _options.Routes.Where(r => canChange(r.Name)))
        {
            Outcome before = Score(prepared, best, weights, confirmedCount);
            float effective = best.Routes[route.Name] ?? best.Default;
            (Settings Settings, Outcome Outcome) top = Grid.Select(g => (float?)g).Prepend(null)
                .Select(candidate => best.With(route.Name, candidate))
                .Select(s => (Settings: s, Outcome: Score(prepared, s, weights, confirmedCount)))
                .Where(o => Safe(o.Outcome, baseline))
                .DefaultIfEmpty((Settings: best, Outcome: before))
                .OrderByDescending(o => o.Outcome.Weighted)
                .ThenBy(o => o.Outcome.WeightedTooLow)
                .ThenBy(o => Math.Abs((o.Settings.Routes[route.Name] ?? best.Default) - effective))
                .First();
            bool better = top.Outcome.Weighted > before.Weighted + Epsilon
                || (Math.Abs(top.Outcome.Weighted - before.Weighted) <= Epsilon && top.Outcome.WeightedTooLow < before.WeightedTooLow - Epsilon);
            if (better)
            {
                best = top.Settings;
            }
        }

        // Route thresholds chosen greedily must not undo what the default gained.
        return Score(prepared, best, weights, confirmedCount).Weighted + Epsilon < Score(prepared, defaultOnly, weights, confirmedCount).Weighted ? defaultOnly : best;
    }

    private Outcome Score(PreparedEvaluation prepared, Settings settings, double[] weights, int confirmedCount)
    {
        var selector = new DefaultRouteSelector(_options, RoutingEvaluator.ThresholdFunction(settings.Default, settings.Routes));
        int count = prepared.Cases.Count;
        var correct = new bool[count];
        int[] confirmed = new int[4], automatic = new int[4];
        double weighted = 0, weightedTooLow = 0;
        for (int i = 0; i < count; i++)
        {
            EvaluationResult result = _evaluator.Decide(prepared, i, selector);
            int[] tally = i < confirmedCount ? confirmed : automatic;
            correct[i] = result.Correct;
            bool tooLow = result.ActualTier < result.Case.ExpectedTier;
            tally[0] += result.Correct ? 1 : 0;
            tally[1] += tooLow ? 1 : 0;
            tally[2] += result.ActualTier > result.Case.ExpectedTier ? 1 : 0;
            tally[3] += result.Case.LocalOnly && !result.LocalOnly ? 1 : 0;
            weighted += result.Correct ? weights[i] : 0;
            weightedTooLow += tooLow ? weights[i] : 0;
        }

        return new Outcome(
            correct,
            new TuningScore
            {
                Total = confirmedCount,
                Correct = confirmed[0],
                TooLow = confirmed[1],
                TooHigh = confirmed[2],
                LocalOnlyMissed = confirmed[3],
            },
            new TuningScore
            {
                Total = count - confirmedCount,
                Correct = automatic[0],
                TooLow = automatic[1],
                TooHigh = automatic[2],
                LocalOnlyMissed = automatic[3],
            },
            weighted,
            weightedTooLow);
    }

    private sealed record Settings(float Default, Dictionary<string, float?> Routes)
    {
        public Settings With(string route, float? value) =>
            this with { Routes = new Dictionary<string, float?>(Routes, StringComparer.OrdinalIgnoreCase) { [route] = value } };
    }

    private sealed record Outcome(bool[] Correct, TuningScore Confirmed, TuningScore Automatic, double Weighted, double WeightedTooLow);
}
