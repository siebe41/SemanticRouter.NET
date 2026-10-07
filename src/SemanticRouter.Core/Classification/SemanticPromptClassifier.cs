using System.Diagnostics;

namespace SemanticRouter;

/// <summary>
/// The default <see cref="IPromptClassifier"/>: complexity heuristics, then embedding similarity
/// against the route index, then an <see cref="IRouteSelector"/> policy.
/// </summary>
/// <remarks>
/// The route index is built lazily on first use (or by <see cref="WarmUpAsync"/>) and cached for the
/// lifetime of the instance. A failed build is not cached, so a transient embedding outage at start-up
/// heals on the next request. Register as a singleton.
/// </remarks>
public sealed class SemanticPromptClassifier : IPromptClassifier
{
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly IComplexityAnalyzer _complexityAnalyzer;
    private readonly IRouteSelector _routeSelector;
    private readonly SemanticRouterOptions _options;
    private readonly RouteDefinition[] _routes;
    private readonly bool _hasPinnedRoutes;
    private readonly Lock _indexGate = new();
    private Task<SemanticRouteIndex>? _indexTask;

    /// <summary>Creates a classifier.</summary>
    /// <param name="embeddingProvider">Embeds utterances and prompts.</param>
    /// <param name="options">Routes and thresholds. Validated eagerly.</param>
    /// <param name="complexityAnalyzer">Defaults to <see cref="HeuristicComplexityAnalyzer"/>.</param>
    /// <param name="routeSelector">Defaults to <see cref="DefaultRouteSelector"/>.</param>
    /// <exception cref="InvalidOperationException"><paramref name="options"/> is invalid.</exception>
    public SemanticPromptClassifier(
        IEmbeddingProvider embeddingProvider,
        SemanticRouterOptions options,
        IComplexityAnalyzer? complexityAnalyzer = null,
        IRouteSelector? routeSelector = null)
    {
        ArgumentNullException.ThrowIfNull(embeddingProvider);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _embeddingProvider = embeddingProvider;
        _options = options;
        _complexityAnalyzer = complexityAnalyzer ?? new HeuristicComplexityAnalyzer(options.Complexity);
        _routeSelector = routeSelector ?? new DefaultRouteSelector(options);
        _routes = options.Routes.ToArray();
        _hasPinnedRoutes = _routes.Any(r => !r.AllowEscalation);
    }

    /// <inheritdoc />
    public async ValueTask WarmUpAsync(CancellationToken cancellationToken = default) =>
        await GetIndexAsync(cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async ValueTask<PromptClassificationResult> ClassifyAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        long started = Stopwatch.GetTimestamp();

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return PromptClassificationResult.Bypass(_options.FallbackTarget, RoutingReason.EmptyPrompt);
        }

        ComplexityAnalysis complexity = _complexityAnalyzer.Analyze(prompt);

        if (!_hasPinnedRoutes && complexity.Score >= _options.ComplexityShortCircuitThreshold)
        {
            return PromptClassificationResult.Bypass(
                RouteTarget.Cloud, RoutingReason.ComplexityShortCircuit, complexity, Stopwatch.GetElapsedTime(started));
        }

        RouteScore[] ranked = [];
        if (_routes.Length > 0)
        {
            SemanticRouteIndex index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
            ReadOnlyMemory<float> embedding =
                await _embeddingProvider.GenerateEmbeddingAsync(prompt, cancellationToken).ConfigureAwait(false);
            ranked = index.Score(embedding.Span);
        }

        RouteDecision decision = _routeSelector.SelectRoute(ranked, complexity);

        return new PromptClassificationResult
        {
            Target = decision.Target,
            Reason = decision.Reason,
            RouteName = decision.Route?.Name,
            Similarity = decision.Similarity,
            IsPinned = decision.Route is { AllowEscalation: false } && decision.Target == decision.Route.Target,
            Complexity = complexity,
            TopScores = ranked.Length <= _options.MaxReportedScores ? ranked : ranked[.._options.MaxReportedScores],
            Elapsed = Stopwatch.GetElapsedTime(started),
        };
    }

    private Task<SemanticRouteIndex> GetIndexAsync(CancellationToken cancellationToken)
    {
        Task<SemanticRouteIndex>? task = Volatile.Read(ref _indexTask);
        if (task is not { IsCompletedSuccessfully: true })
        {
            lock (_indexGate)
            {
                if (_indexTask is null || _indexTask.IsFaulted || _indexTask.IsCanceled)
                {
                    // Build without the caller's token: one aborted request must not poison the shared index.
                    _indexTask = SemanticRouteIndex.BuildAsync(
                        _routes, _embeddingProvider, _options.ScoringStrategy, CancellationToken.None);
                }

                task = _indexTask;
            }
        }

        return task.IsCompletedSuccessfully ? task : task.WaitAsync(cancellationToken);
    }
}
