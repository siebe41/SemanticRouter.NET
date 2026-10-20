using System.Diagnostics;

namespace SemanticRouter;

/// <summary>What one labelled prompt was routed to.</summary>
public sealed record EvaluationResult
{
    /// <summary>The labelled prompt.</summary>
    public required EvaluationCase Case { get; init; }

    /// <summary>The tier the configuration gave it.</summary>
    public required int ActualTier { get; init; }

    /// <summary>How that tier was decided.</summary>
    public required RoutingReason Reason { get; init; }

    /// <summary>The route it matched, if any.</summary>
    public string? ActualRoute { get; init; }

    /// <summary>The matched (or best) route's similarity.</summary>
    public float? Similarity { get; init; }

    /// <summary>The prompt's complexity score.</summary>
    public required double Complexity { get; init; }

    /// <summary>Whether the decision keeps the prompt on local models.</summary>
    public required bool LocalOnly { get; init; }

    /// <summary>The best route scores, best first.</summary>
    public required IReadOnlyList<RouteScore> TopScores { get; init; }

    /// <summary>The tier is right, and a local-only case stayed local.</summary>
    public bool Correct => ActualTier == Case.ExpectedTier && (!Case.LocalOnly || LocalOnly);

    /// <summary>The case names a route and the prompt matched it.</summary>
    public bool? RouteCorrect => Case.ExpectedRoute is { } expected
        ? string.Equals(expected, ActualRoute, StringComparison.OrdinalIgnoreCase)
        : null;
}

/// <summary>Accuracy for the prompts labelled with one tier.</summary>
public sealed record TierAccuracy
{
    /// <summary>The expected tier.</summary>
    public required int Tier { get; init; }

    /// <summary>Prompts labelled with it.</summary>
    public required int Total { get; init; }

    /// <summary>Prompts routed to it.</summary>
    public required int Correct { get; init; }

    /// <summary><see cref="Correct"/> / <see cref="Total"/>.</summary>
    public double Accuracy => Total == 0 ? 0 : (double)Correct / Total;
}

/// <summary>How well a configuration routes an evaluation set.</summary>
public sealed record EvaluationReport
{
    /// <summary>Labelled prompts.</summary>
    public required int Total { get; init; }

    /// <summary>Prompts routed to their expected tier (local-only ones also kept local).</summary>
    public required int Correct { get; init; }

    /// <summary><see cref="Correct"/> / <see cref="Total"/>.</summary>
    public double Accuracy => Total == 0 ? 0 : (double)Correct / Total;

    /// <summary>Prompts routed below their tier: answered by a weaker model than they need (costs quality).</summary>
    public required int TooLow { get; init; }

    /// <summary>Prompts routed above their tier: answered by a stronger model than they need (costs money).</summary>
    public required int TooHigh { get; init; }

    /// <summary>Local-only prompts whose decision would let them leave the machine.</summary>
    public required int LocalOnlyMissed { get; init; }

    /// <summary>Prompts that name an expected route.</summary>
    public required int RouteLabelled { get; init; }

    /// <summary>Of those, prompts that matched it.</summary>
    public required int RouteCorrect { get; init; }

    /// <summary>The default similarity threshold the set was evaluated with.</summary>
    public required float DefaultThreshold { get; init; }

    /// <summary>The tiers on both axes of <see cref="Confusion"/>, ascending.</summary>
    public required IReadOnlyList<int> Tiers { get; init; }

    /// <summary><c>Confusion[expected][actual]</c>, indexed like <see cref="Tiers"/>.</summary>
    public required IReadOnlyList<IReadOnlyList<int>> Confusion { get; init; }

    /// <summary>Accuracy per expected tier.</summary>
    public required IReadOnlyList<TierAccuracy> PerTier { get; init; }

    /// <summary>Every prompt's outcome, in set order.</summary>
    public required IReadOnlyList<EvaluationResult> Results { get; init; }

    /// <summary>The prompts that were routed wrongly.</summary>
    public IEnumerable<EvaluationResult> Misroutes => Results.Where(r => !r.Correct);
}

/// <summary>An evaluation set embedded once, ready to be scored under many thresholds without new embedding calls.</summary>
public sealed class PreparedEvaluation
{
    internal PreparedEvaluation(
        IReadOnlyList<EvaluationCase> cases, RouteScore[][] ranked, RouteScore[]?[] wholeRanked, ComplexityAnalysis[] complexity, TimeSpan elapsed)
    {
        Cases = cases;
        Ranked = ranked;
        WholeRanked = wholeRanked;
        Complexity = complexity;
        EmbeddingTime = elapsed;
    }

    /// <summary>The labelled prompts.</summary>
    public IReadOnlyList<EvaluationCase> Cases { get; }

    /// <summary>Time spent building the route index and embedding the prompts.</summary>
    public TimeSpan EmbeddingTime { get; }

    internal RouteScore[][] Ranked { get; }

    /// <summary>The whole prompt's scores, where only its prose was ranked and a route is local-only; otherwise <see langword="null"/>.</summary>
    internal RouteScore[]?[] WholeRanked { get; }

    internal ComplexityAnalysis[] Complexity { get; }
}

/// <summary>One default threshold tried during calibration.</summary>
public sealed record CalibrationPoint
{
    /// <summary>The default similarity threshold.</summary>
    public required float Threshold { get; init; }

    /// <summary>Prompts routed to their tier.</summary>
    public required int Correct { get; init; }

    /// <summary>Prompts routed below their tier.</summary>
    public required int TooLow { get; init; }

    /// <summary>Prompts routed above their tier.</summary>
    public required int TooHigh { get; init; }

    /// <summary>Labelled prompts.</summary>
    public required int Total { get; init; }

    /// <summary><see cref="Correct"/> / <see cref="Total"/>.</summary>
    public double Accuracy => Total == 0 ? 0 : (double)Correct / Total;
}

/// <summary>A per-route threshold calibration suggests.</summary>
public sealed record RouteThresholdRecommendation
{
    /// <summary>The route.</summary>
    public required string Route { get; init; }

    /// <summary>Its configured threshold (<see langword="null"/>: the default).</summary>
    public float? Current { get; init; }

    /// <summary>The suggested threshold (<see langword="null"/>: use the default).</summary>
    public float? Recommended { get; init; }

    /// <summary>Correct prompts with the route's current setting (and the recommended default).</summary>
    public required int CorrectBefore { get; init; }

    /// <summary>Correct prompts with the suggestion.</summary>
    public required int CorrectAfter { get; init; }
}

/// <summary>The result of sweeping thresholds over an evaluation set.</summary>
public sealed record CalibrationReport
{
    /// <summary>The configuration as it is.</summary>
    public required CalibrationPoint Current { get; init; }

    /// <summary>The best default threshold (never worse than <see cref="Current"/> on the set).</summary>
    public required CalibrationPoint Recommended { get; init; }

    /// <summary>Every default threshold tried, ascending.</summary>
    public required IReadOnlyList<CalibrationPoint> Sweep { get; init; }

    /// <summary>Per-route thresholds that improve on the recommended default, and current overrides that should go.</summary>
    public required IReadOnlyList<RouteThresholdRecommendation> Routes { get; init; }

    /// <summary>Correct prompts with the recommended default and route thresholds together.</summary>
    public required int CorrectWithRoutes { get; init; }
}

/// <summary>
/// Runs labelled prompts through the classification pipeline (complexity, route index, route selection) without
/// calling any model, and sweeps similarity thresholds to recommend better ones.
/// </summary>
/// <remarks>
/// Decisions are made the way <see cref="SemanticPromptClassifier"/> with <see cref="DefaultRouteSelector"/> makes them.
/// The prompts are embedded once (<see cref="PrepareAsync"/>), so evaluating under other thresholds is cheap.
/// </remarks>
public sealed class RoutingEvaluator
{
    private static readonly float[] Grid = Enumerable.Range(0, 101).Select(i => i / 100f).ToArray();

    private readonly IEmbeddingProvider _embeddings;
    private readonly SemanticRouterOptions _options;

    /// <summary>The configuration being evaluated.</summary>
    internal SemanticRouterOptions Options => _options;
    private readonly IComplexityAnalyzer _complexity;
    private readonly RouteDefinition[] _routes;
    private readonly bool _hasLocalOnlyRoutes;

    /// <summary>Creates an evaluator for a configuration, with <see cref="HeuristicComplexityAnalyzer"/>.</summary>
    /// <param name="embeddings">The embedding provider the configuration uses (or one to compare against).</param>
    /// <param name="options">Routes, thresholds and complexity rules.</param>
    public RoutingEvaluator(IEmbeddingProvider embeddings, SemanticRouterOptions options)
        : this(embeddings, options, null)
    {
    }

    /// <summary>Creates an evaluator for a configuration.</summary>
    /// <param name="embeddings">The embedding provider the configuration uses (or one to compare against).</param>
    /// <param name="options">Routes, thresholds and complexity rules.</param>
    /// <param name="complexity">Defaults to <see cref="HeuristicComplexityAnalyzer"/>.</param>
    public RoutingEvaluator(IEmbeddingProvider embeddings, SemanticRouterOptions options, IComplexityAnalyzer? complexity)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(options);
        _embeddings = embeddings;
        _options = options;
        _complexity = complexity ?? new HeuristicComplexityAnalyzer(options.Complexity);
        _routes = options.Routes.ToArray();
        _hasLocalOnlyRoutes = _routes.Any(r => r.LocalOnly);
    }

    /// <summary>Embeds the routes and the prompts (one batch each) and analyses the prompts' complexity.</summary>
    public async Task<PreparedEvaluation> PrepareAsync(IReadOnlyList<EvaluationCase> cases, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cases);
        long started = Stopwatch.GetTimestamp();
        var complexity = cases.Select(c => _complexity.Analyze(c.Prompt)).ToArray();
        var ranked = new RouteScore[cases.Count][];
        var wholeRanked = new RouteScore[]?[cases.Count];
        if (_routes.Length > 0 && cases.Count > 0)
        {
            SemanticRouteIndex index = await SemanticRouteIndex.BuildAsync(_routes, _embeddings, _options.ScoringStrategy, cancellationToken).ConfigureAwait(false);

            // As the classifier does: the prose of each prompt, and the whole prompt too where that left something out
            // and a route is local-only.
            string[] texts = cases.Select(c => _options.EmbedProseOnly ? PromptProse.Extract(c.Prompt) : c.Prompt).ToArray();
            int[] whole = _hasLocalOnlyRoutes ? Enumerable.Range(0, cases.Count).Where(i => !ReferenceEquals(texts[i], cases[i].Prompt)).ToArray() : [];
            IReadOnlyList<ReadOnlyMemory<float>> vectors =
                await _embeddings.GenerateEmbeddingsAsync([.. texts, .. whole.Select(i => cases[i].Prompt)], cancellationToken).ConfigureAwait(false);
            if (vectors.Count != texts.Length + whole.Length)
            {
                throw new InvalidOperationException($"Embedding provider returned {vectors.Count} vectors for {texts.Length + whole.Length} texts.");
            }

            for (int i = 0; i < cases.Count; i++)
            {
                ranked[i] = index.Score(vectors[i].Span);
            }

            for (int j = 0; j < whole.Length; j++)
            {
                wholeRanked[whole[j]] = index.Score(vectors[texts.Length + j].Span);
            }
        }
        else
        {
            ranked.AsSpan().Fill([]);
        }

        return new PreparedEvaluation(cases, ranked, wholeRanked, complexity, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>Embeds and evaluates a set with the configured thresholds.</summary>
    public async Task<EvaluationReport> EvaluateAsync(IReadOnlyList<EvaluationCase> cases, CancellationToken cancellationToken = default) =>
        Evaluate(await PrepareAsync(cases, cancellationToken).ConfigureAwait(false));

    /// <summary>Evaluates a prepared set, optionally with other thresholds than the configured ones.</summary>
    /// <param name="prepared">The embedded set.</param>
    /// <param name="defaultThreshold">Replaces <see cref="SemanticRouterOptions.DefaultSimilarityThreshold"/>.</param>
    /// <param name="routeThresholds">Replaces routes' own thresholds (a <see langword="null"/> value: use the default).</param>
    public EvaluationReport Evaluate(
        PreparedEvaluation prepared,
        float? defaultThreshold = null,
        IReadOnlyDictionary<string, float?>? routeThresholds = null)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        float fallback = defaultThreshold ?? _options.DefaultSimilarityThreshold;
        Func<RouteDefinition, float> threshold = ThresholdFunction(fallback, routeThresholds);
        var selector = new DefaultRouteSelector(_options, threshold);

        var results = new EvaluationResult[prepared.Cases.Count];
        for (int i = 0; i < results.Length; i++)
        {
            results[i] = Decide(prepared, i, selector);
        }

        int[] tiers = results.Select(r => r.Case.ExpectedTier).Concat(results.Select(r => r.ActualTier)).Distinct().Order().ToArray();
        var confusion = tiers.Select(_ => new int[tiers.Length]).ToArray();
        foreach (EvaluationResult result in results)
        {
            confusion[Array.IndexOf(tiers, result.Case.ExpectedTier)][Array.IndexOf(tiers, result.ActualTier)]++;
        }

        return new EvaluationReport
        {
            Total = results.Length,
            Correct = results.Count(r => r.Correct),
            TooLow = results.Count(r => r.ActualTier < r.Case.ExpectedTier),
            TooHigh = results.Count(r => r.ActualTier > r.Case.ExpectedTier),
            LocalOnlyMissed = results.Count(r => r.Case.LocalOnly && !r.LocalOnly),
            RouteLabelled = results.Count(r => r.RouteCorrect is not null),
            RouteCorrect = results.Count(r => r.RouteCorrect == true),
            DefaultThreshold = fallback,
            Tiers = tiers,
            Confusion = confusion,
            PerTier = results.GroupBy(r => r.Case.ExpectedTier).OrderBy(g => g.Key)
                .Select(g => new TierAccuracy { Tier = g.Key, Total = g.Count(), Correct = g.Count(r => r.Correct) }).ToArray(),
            Results = results,
        };
    }

    /// <summary>
    /// Sweeps the default threshold from 0 to 1 (and the current value), then each route's own threshold, and recommends
    /// the values that route the most prompts correctly. Ties go to fewer prompts routed too low (quality over cost),
    /// then to the value closest to the current one, so the recommendation is never worse than the configuration.
    /// </summary>
    public CalibrationReport Calibrate(PreparedEvaluation prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        Dictionary<string, float?> configured = _routes.ToDictionary(r => r.Name, r => r.SimilarityThreshold, StringComparer.OrdinalIgnoreCase);
        float current = _options.DefaultSimilarityThreshold;

        // The default threshold, with routes' own thresholds as configured.
        CalibrationPoint currentPoint = Point(prepared, current, configured);
        var sweep = Grid.Append(current).Distinct().Order().Select(t => Point(prepared, t, configured)).ToArray();
        CalibrationPoint best = Best(sweep, current);

        // Then each route on its own, the others held at the best values found so far.
        var thresholds = new Dictionary<string, float?>(configured, StringComparer.OrdinalIgnoreCase);
        var recommendations = new List<RouteThresholdRecommendation>();
        foreach (RouteDefinition route in _routes)
        {
            // Start from the route's current setting and move only for a strict improvement, as little as possible.
            CalibrationPoint before = Point(prepared, best.Threshold, thresholds);
            float effective = route.SimilarityThreshold ?? best.Threshold;
            var options = Grid.Select(g => (float?)g).Prepend(null)
                .Select(candidate =>
                {
                    thresholds[route.Name] = candidate;
                    return (Candidate: candidate, Point: Point(prepared, best.Threshold, thresholds));
                })
                .ToArray();
            (float? Candidate, CalibrationPoint Point) top = options
                .OrderByDescending(o => o.Point.Correct)
                .ThenBy(o => o.Point.TooLow)
                .ThenBy(o => Math.Abs((o.Candidate ?? best.Threshold) - effective))
                .First();
            bool better = top.Point.Correct > before.Correct || (top.Point.Correct == before.Correct && top.Point.TooLow < before.TooLow);
            float? chosen = better ? top.Candidate : route.SimilarityThreshold;
            CalibrationPoint chosenPoint = better ? top.Point : before;

            thresholds[route.Name] = chosen;
            if (chosen != route.SimilarityThreshold)
            {
                recommendations.Add(new RouteThresholdRecommendation
                {
                    Route = route.Name,
                    Current = route.SimilarityThreshold,
                    Recommended = chosen,
                    CorrectBefore = before.Correct,
                    CorrectAfter = chosenPoint.Correct,
                });
            }
        }

        int withRoutes = Point(prepared, best.Threshold, thresholds).Correct;
        if (withRoutes < best.Correct)
        {
            // Never recommend route thresholds that undo the default's gain (they are chosen greedily).
            recommendations.Clear();
            withRoutes = best.Correct;
        }

        return new CalibrationReport
        {
            Current = currentPoint,
            Recommended = best,
            Sweep = sweep,
            Routes = recommendations,
            CorrectWithRoutes = withRoutes,
        };
    }

    private static CalibrationPoint Best(IEnumerable<CalibrationPoint> sweep, float current) => sweep
        .OrderByDescending(p => p.Correct)
        .ThenBy(p => p.TooLow)
        .ThenBy(p => Math.Abs(p.Threshold - current))
        .First();

    private CalibrationPoint Point(PreparedEvaluation prepared, float threshold, IReadOnlyDictionary<string, float?> routes)
    {
        var selector = new DefaultRouteSelector(_options, ThresholdFunction(threshold, routes));
        int correct = 0, tooLow = 0, tooHigh = 0;
        for (int i = 0; i < prepared.Cases.Count; i++)
        {
            EvaluationResult result = Decide(prepared, i, selector);
            correct += result.Correct ? 1 : 0;
            tooLow += result.ActualTier < result.Case.ExpectedTier ? 1 : 0;
            tooHigh += result.ActualTier > result.Case.ExpectedTier ? 1 : 0;
        }

        return new CalibrationPoint { Threshold = threshold, Correct = correct, TooLow = tooLow, TooHigh = tooHigh, Total = prepared.Cases.Count };
    }

    internal static Func<RouteDefinition, float> ThresholdFunction(float fallback, IReadOnlyDictionary<string, float?>? routes) =>
        routes is null
            ? route => route.SimilarityThreshold ?? fallback
            : route => routes.TryGetValue(route.Name, out float? own) ? own ?? fallback : route.SimilarityThreshold ?? fallback;

    /// <summary>Decides one prompt the way <see cref="SemanticPromptClassifier"/> does.</summary>
    internal EvaluationResult Decide(PreparedEvaluation prepared, int index, DefaultRouteSelector selector)
    {
        EvaluationCase item = prepared.Cases[index];
        ComplexityAnalysis complexity = prepared.Complexity[index];
        RouteScore[] ranked = prepared.Ranked[index];
        RouteScore[] top = ranked.Length <= 3 ? ranked : ranked[..3];

        if (string.IsNullOrWhiteSpace(item.Prompt))
        {
            return new EvaluationResult
            {
                Case = item,
                ActualTier = _options.FallbackTier,
                Reason = RoutingReason.EmptyPrompt,
                Complexity = 0,
                LocalOnly = false,
                TopScores = [],
            };
        }

        if (!_hasLocalOnlyRoutes && complexity.Score >= _options.ComplexityShortCircuitThreshold)
        {
            int tier = _options.TierForComplexity(complexity.Score) is > 0 and var t ? t : _options.FallbackTier;
            return new EvaluationResult
            {
                Case = item,
                ActualTier = tier,
                Reason = RoutingReason.ComplexityShortCircuit,
                Complexity = complexity.Score,
                LocalOnly = false,
                TopScores = [],
            };
        }

        RouteDecision decision = selector.SelectRoute(ranked, complexity);
        bool localOnly = decision.LocalOnly || (prepared.WholeRanked[index] is { } whole && selector.SelectRoute(whole, complexity).LocalOnly);
        return new EvaluationResult
        {
            Case = item,
            ActualTier = decision.RequiredTier,
            Reason = decision.Reason,
            ActualRoute = decision.Route?.Name,
            Similarity = decision.Similarity,
            Complexity = complexity.Score,
            LocalOnly = localOnly,
            TopScores = top,
        };
    }
}
