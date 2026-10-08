namespace SemanticRouter;

/// <summary>A request to route: the prompt plus facts about the surrounding request.</summary>
public sealed record PromptRoutingRequest
{
    /// <summary>The prompt text to classify (typically the latest user message).</summary>
    public required string Prompt { get; init; }

    /// <summary>
    /// Estimated input size of the whole request (system prompt, history, tools). Used for context-window checks and
    /// cost. Defaults to the prompt's own estimate.
    /// </summary>
    public int? EstimatedInputTokens { get; init; }

    /// <summary>Capabilities implied by the request itself, e.g. <c>tools</c> when it defines tools or <c>vision</c> when it has images.</summary>
    public IReadOnlyList<string> AdditionalCapabilities { get; init; } = [];

    /// <summary>A catalog model name the caller asked for explicitly; bypasses classification when it exists.</summary>
    public string? RequestedModel { get; init; }

    /// <summary>Overrides the configured <see cref="ModelSelectionOptions.Strategy"/> for this request.</summary>
    public SelectionStrategy? Strategy { get; init; }

    /// <summary>
    /// A catalog model that served an earlier turn of this conversation (it issued the tool calls this request answers).
    /// When it is enabled and available it is tried first without re-classifying the prompt
    /// (<see cref="RoutingReason.ConversationAffinity"/>); otherwise the request is routed normally.
    /// </summary>
    public string? AffinityModel { get; init; }
}

/// <summary>The routing outcome: requirements, and the models to try in order.</summary>
/// <param name="Classification">Why the requirements are what they are.</param>
/// <param name="Requirements">What a model must satisfy.</param>
/// <param name="Candidates">Models to try, best first. Empty when nothing can serve the request.</param>
public sealed record RoutingPlan(
    PromptClassificationResult Classification,
    ModelRequirements Requirements,
    IReadOnlyList<ModelCandidate> Candidates);

/// <summary>Routes a prompt to an ordered list of catalog models.</summary>
public interface IPromptRouter
{
    /// <summary>The configured model catalog.</summary>
    IReadOnlyList<ModelDefinition> Models { get; }

    /// <summary>Classifies the prompt and ranks the models that can serve it.</summary>
    ValueTask<RoutingPlan> RouteAsync(PromptRoutingRequest request, CancellationToken cancellationToken = default);

    /// <summary>Ranks models for an existing classification (e.g. a fail-open fallback).</summary>
    RoutingPlan Plan(PromptClassificationResult classification, PromptRoutingRequest request);
}

/// <summary>
/// Default <see cref="IPromptRouter"/>: <see cref="IPromptClassifier"/> decides the requirements,
/// <see cref="IModelSelector"/> ranks the catalog, and <see cref="IModelHealth"/> skips models in cooldown.
/// </summary>
public sealed class PromptRouter : IPromptRouter
{
    private readonly IPromptClassifier _classifier;
    private readonly IModelSelector _selector;
    private readonly IModelHealth _health;
    private readonly ModelDefinition[] _models;

    /// <summary>Creates a router.</summary>
    public PromptRouter(
        IPromptClassifier classifier,
        SemanticRouterOptions options,
        IModelSelector? selector = null,
        IModelHealth? health = null)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(options);

        _classifier = classifier;
        _selector = selector ?? new DefaultModelSelector(options.Selection);
        _health = health ?? new ModelHealthTracker(options.Selection);
        _models = options.Models.ToArray();
    }

    /// <inheritdoc />
    public IReadOnlyList<ModelDefinition> Models => _models;

    /// <inheritdoc />
    public async ValueTask<RoutingPlan> RouteAsync(PromptRoutingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestedModel is { } requested
            && _models.FirstOrDefault(m => m.Enabled && string.Equals(m.Name, requested, StringComparison.OrdinalIgnoreCase)) is { } forced)
        {
            int tokens = request.EstimatedInputTokens ?? TokenEstimator.Estimate(request.Prompt);
            var requirements = new ModelRequirements { RequiredTier = forced.Tier, EstimatedInputTokens = tokens };
            return new RoutingPlan(
                PromptClassificationResult.Bypass(forced.Tier, RoutingReason.ModelOverride),
                requirements,
                [new ModelCandidate(forced, forced.EstimateCost(tokens, 0), MeetsRequiredTier: true)]);
        }

        if (request.AffinityModel is { } affinity
            && _models.FirstOrDefault(m => m.Enabled && string.Equals(m.Name, affinity, StringComparison.OrdinalIgnoreCase)) is { } pinned
            && _health.IsAvailable(pinned))
        {
            // Continue the turn on the model that started it; the normally ranked models remain as fallbacks.
            var pinnedRequest = request with { EstimatedInputTokens = request.EstimatedInputTokens ?? TokenEstimator.Estimate(request.Prompt) };
            RoutingPlan plan = Plan(PromptClassificationResult.Bypass(pinned.Tier, RoutingReason.ConversationAffinity), pinnedRequest);
            ModelCandidate first = plan.Candidates.FirstOrDefault(c => c.Model == pinned)
                ?? new ModelCandidate(pinned, pinned.EstimateCost(plan.Requirements.EstimatedInputTokens, 0), MeetsRequiredTier: true);
            return plan with { Candidates = [first, .. plan.Candidates.Where(c => c.Model != pinned)] };
        }

        PromptClassificationResult classification =
            await _classifier.ClassifyAsync(request.Prompt, cancellationToken).ConfigureAwait(false);
        return Plan(classification, request);
    }

    /// <inheritdoc />
    public RoutingPlan Plan(PromptClassificationResult classification, PromptRoutingRequest request)
    {
        ArgumentNullException.ThrowIfNull(classification);
        ArgumentNullException.ThrowIfNull(request);

        var requirements = new ModelRequirements
        {
            RequiredTier = classification.RequiredTier,
            RequiredCapabilities = classification.RequiredCapabilities
                .Concat(request.AdditionalCapabilities)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            LocalOnly = classification.LocalOnly,
            EstimatedInputTokens = request.EstimatedInputTokens ?? classification.Complexity.EstimatedTokens,
            Strategy = request.Strategy,
        };

        IReadOnlyList<ModelCandidate> candidates = _selector.RankCandidates(requirements, _models, _health.IsAvailable);
        if (candidates.Count == 0)
        {
            // Everything that qualifies is cooling down: trying a cooled-down model beats failing outright.
            candidates = _selector.RankCandidates(requirements, _models);
        }

        return new RoutingPlan(classification, requirements, candidates);
    }
}
