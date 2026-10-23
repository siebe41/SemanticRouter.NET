using System.Diagnostics;

namespace SemanticRouter;

/// <summary>
/// The default <see cref="IPromptClassifier"/>: complexity heuristics, then embedding similarity of the prompt's prose
/// (<see cref="SemanticRouterOptions.EmbedProseOnly"/>) against the route index, then an <see cref="IRouteSelector"/>
/// policy that decides the required model tier.
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
    private readonly bool _hasLocalOnlyRoutes;
    private readonly Lock _indexGate = new();
    private readonly ClassificationCache? _cache;
    private Task<SemanticRouteIndex>? _indexTask;
    private RouteIndexStatistics? _indexStatistics;

    /// <summary>Creates a classifier with the default collaborators.</summary>
    /// <param name="embeddingProvider">Embeds utterances and prompts.</param>
    /// <param name="options">Routes and thresholds. Validated eagerly.</param>
    /// <exception cref="InvalidOperationException"><paramref name="options"/> is invalid.</exception>
    public SemanticPromptClassifier(IEmbeddingProvider embeddingProvider, SemanticRouterOptions options)
        : this(embeddingProvider, options, new SemanticPromptClassifierDependencies())
    {
    }

    /// <summary>Creates a classifier.</summary>
    /// <param name="embeddingProvider">Embeds utterances and prompts.</param>
    /// <param name="options">Routes and thresholds. Validated eagerly.</param>
    /// <param name="dependencies">Collaborators that replace the defaults; <see langword="null"/> keeps them all.</param>
    /// <exception cref="InvalidOperationException"><paramref name="options"/> is invalid.</exception>
    public SemanticPromptClassifier(
        IEmbeddingProvider embeddingProvider,
        SemanticRouterOptions options,
        SemanticPromptClassifierDependencies? dependencies)
    {
        ArgumentNullException.ThrowIfNull(embeddingProvider);
        ArgumentNullException.ThrowIfNull(options);
        dependencies ??= new();
        options.Validate();

        _embeddingProvider = embeddingProvider;
        _options = options;
        _complexityAnalyzer = dependencies.ComplexityAnalyzer ?? new HeuristicComplexityAnalyzer(options.Complexity);
        _routeSelector = dependencies.RouteSelector ?? new DefaultRouteSelector(options);
        _routes = options.Routes.ToArray();
        _hasLocalOnlyRoutes = _routes.Any(r => r.LocalOnly);
        _cache = options.ClassificationCache.Enabled
            ? new ClassificationCache(options.ClassificationCache, dependencies.Time ?? TimeProvider.System)
            : null;
    }

    /// <summary>Hits and misses of the classification cache, or <see langword="null"/> when it is off.</summary>
    public ClassificationCacheStatistics? CacheStatistics => _cache?.Statistics;

    /// <summary>The route index once built: utterances embedded and how long it took. <see langword="null"/> before.</summary>
    public RouteIndexStatistics? IndexStatistics => Volatile.Read(ref _indexStatistics);

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
            return PromptClassificationResult.Bypass(_options.FallbackTier, RoutingReason.EmptyPrompt);
        }

        // Decisions depend only on the prompt and this configuration, so a recent one can be reused as it is.
        string? key = _cache is null ? null : ClassificationCache.KeyOf(prompt);
        if (key is not null && _cache!.TryGet(key, out PromptClassificationResult cached))
        {
            return cached with { Cached = true, Elapsed = Stopwatch.GetElapsedTime(started) };
        }

        ComplexityAnalysis complexity = _complexityAnalyzer.Analyze(prompt);

        if (!_hasLocalOnlyRoutes && complexity.Score >= _options.ComplexityShortCircuitThreshold)
        {
            int tier = _options.TierForComplexity(complexity.Score) is > 0 and var t ? t : _options.FallbackTier;
            PromptClassificationResult shortCircuit = PromptClassificationResult.Bypass(
                tier, RoutingReason.ComplexityShortCircuit, complexity, Stopwatch.GetElapsedTime(started));
            if (key is not null)
            {
                _cache!.Add(key, shortCircuit);
            }

            return shortCircuit;
        }

        RouteScore[] ranked = [];
        RouteScore[]? wholeRanked = null;
        if (_routes.Length > 0)
        {
            SemanticRouteIndex index = await GetIndexAsync(cancellationToken).ConfigureAwait(false);
            string text = _options.EmbedProseOnly ? PromptProse.Extract(prompt) : prompt;
            if (_hasLocalOnlyRoutes && !ReferenceEquals(text, prompt))
            {
                // Pasted data is where private content is: local-only routes are still checked against the whole
                // prompt, so leaving it out of the embedding never loses a local-only match.
                IReadOnlyList<ReadOnlyMemory<float>> both =
                    await _embeddingProvider.GenerateEmbeddingsAsync([text, prompt], cancellationToken).ConfigureAwait(false);
                ranked = index.Score(both[0].Span);
                wholeRanked = index.Score(both[1].Span);
            }
            else
            {
                ReadOnlyMemory<float> embedding = await _embeddingProvider.GenerateEmbeddingAsync(text, cancellationToken).ConfigureAwait(false);
                ranked = index.Score(embedding.Span);
            }
        }

        RouteDecision decision = _routeSelector.SelectRoute(ranked, complexity);
        bool localOnly = decision.LocalOnly || (wholeRanked is not null && _routeSelector.SelectRoute(wholeRanked, complexity).LocalOnly);

        var result = new PromptClassificationResult
        {
            RequiredTier = decision.RequiredTier,
            Reason = decision.Reason,
            RequiredCapabilities = decision.RequiredCapabilities,
            LocalOnly = localOnly,
            RouteName = decision.Route?.Name,
            Similarity = decision.Similarity,
            Complexity = complexity,
            TopScores = ranked.Length <= _options.MaxReportedScores ? ranked : ranked[.._options.MaxReportedScores],
            Elapsed = Stopwatch.GetElapsedTime(started),
        };
        if (key is not null)
        {
            _cache!.Add(key, result);
        }

        return result;
    }

    /// <inheritdoc />
    public ValueTask<PromptClassificationResult> ClassifyWithoutEmbeddingAsync(string prompt, CancellationToken cancellationToken = default)
    {
        long started = Stopwatch.GetTimestamp();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return ValueTask.FromResult(PromptClassificationResult.Bypass(_options.FallbackTier, RoutingReason.EmptyPrompt));
        }

        ComplexityAnalysis complexity = _complexityAnalyzer.Analyze(prompt);
        RouteDecision decision = _routeSelector.SelectRoute([], complexity);
        return ValueTask.FromResult(new PromptClassificationResult
        {
            RequiredTier = decision.RequiredTier,
            Reason = decision.Reason,
            RequiredCapabilities = decision.RequiredCapabilities,
            Complexity = complexity,
            Elapsed = Stopwatch.GetElapsedTime(started),
        });
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
                    // Build without the caller's token: one aborted request must not poison the shared index. Off the
                    // lock: a provider that embeds on the calling thread (ONNX, Hashing) would otherwise build the whole
                    // index while holding it, and every request arriving meanwhile would block a pool thread on it.
                    _indexTask = Task.Run(BuildIndexAsync);
                }

                task = _indexTask;
            }
        }

        return task.IsCompletedSuccessfully ? task : task.WaitAsync(cancellationToken);
    }

    private async Task<SemanticRouteIndex> BuildIndexAsync()
    {
        long started = Stopwatch.GetTimestamp();
        SemanticRouteIndex index = await SemanticRouteIndex.BuildAsync(
            _routes, _embeddingProvider, _options.ScoringStrategy, CancellationToken.None).ConfigureAwait(false);
        Volatile.Write(ref _indexStatistics, new RouteIndexStatistics
        {
            Utterances = _routes.Sum(r => r.Utterances.Count(u => !string.IsNullOrWhiteSpace(u))),
            Dimensions = index.Dimensions,
            BuildTime = Stopwatch.GetElapsedTime(started),
        });
        return index;
    }
}

/// <summary>
/// The collaborators of a <see cref="SemanticPromptClassifier"/> that have a default. One left <see langword="null"/>
/// gets its default.
/// </summary>
/// <remarks>A new collaborator becomes a property here, so code that creates a classifier keeps working.</remarks>
public sealed class SemanticPromptClassifierDependencies
{
    /// <summary>Scores how hard a prompt is. Defaults to a <see cref="HeuristicComplexityAnalyzer"/> with <see cref="SemanticRouterOptions.Complexity"/>.</summary>
    public IComplexityAnalyzer? ComplexityAnalyzer { get; init; }

    /// <summary>Turns route scores into a decision. Defaults to <see cref="DefaultRouteSelector"/>.</summary>
    public IRouteSelector? RouteSelector { get; init; }

    /// <summary>The clock for the classification cache. Defaults to the system clock.</summary>
    public TimeProvider? Time { get; init; }
}

/// <summary>The route index of a <see cref="SemanticPromptClassifier"/>.</summary>
public sealed record RouteIndexStatistics
{
    /// <summary>Utterances embedded.</summary>
    public required int Utterances { get; init; }

    /// <summary>Size of the vectors.</summary>
    public required int Dimensions { get; init; }

    /// <summary>How long embedding the utterances took.</summary>
    public required TimeSpan BuildTime { get; init; }
}
