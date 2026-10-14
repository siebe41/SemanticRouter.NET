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

    /// <summary>
    /// A floor for the classified tier (e.g. a retry of a prompt that a lower tier answered). When it raises the tier,
    /// the reason becomes <see cref="RoutingReason.RetryEscalation"/>.
    /// </summary>
    public int? MinimumTier { get; init; }

    /// <summary>
    /// The tier this conversation needed on recent turns. The classified tier is raised to it, so a short follow-up
    /// does not drop a task to a weaker model; the reason then becomes <see cref="RoutingReason.ConversationFloor"/>.
    /// </summary>
    public int? ConversationFloor { get; init; }

    /// <summary>
    /// A tier the person asked for (an inline <c>@tier3</c>): it replaces the classified tier, up or down, over every
    /// floor; the reason becomes <see cref="RoutingReason.ExplicitCallout"/>.
    /// </summary>
    public int? ExplicitTier { get; init; }

    /// <summary>
    /// The model that answered this conversation's last turn. It is tried first while it is available and exactly on
    /// the required tier, so a conversation stays on one model (coherent answers, prompt-cache hits) until it needs a
    /// stronger one or can make do with a cheaper one (<see cref="RoutingPlan.Sticky"/>).
    /// </summary>
    public string? PreferredModel { get; init; }

    /// <summary>
    /// The <see cref="SemanticRouterOptions.Profiles"/> entry to route under: it narrows the models, clamps the tier and can
    /// keep the request local. An explicit model it does not allow is ignored (the request is routed as <c>auto</c>).
    /// </summary>
    /// <exception cref="ArgumentException">Thrown by <see cref="IPromptRouter.RouteAsync"/> when no such profile exists.</exception>
    public string? Profile { get; init; }

    /// <summary>Only local models may serve this request, whatever the classification says (e.g. it contains a secret).</summary>
    public bool LocalOnly { get; init; }

    /// <summary>
    /// A cheaper model to try first on a plain route match (a trial of whether it does the route's work as well), when it
    /// can serve the request; the usual candidates stay behind it (<see cref="RoutingPlan.Trial"/>).
    /// </summary>
    public string? TrialModel { get; init; }

    /// <summary>
    /// A decision made before routing (a recognised client side request): the classifier and the conversation floor
    /// are skipped and the models are ranked for it.
    /// </summary>
    public PromptClassificationResult? Classification { get; init; }
}

/// <summary>The routing outcome: requirements, and the models to try in order.</summary>
/// <param name="Classification">Why the requirements are what they are.</param>
/// <param name="Requirements">What a model must satisfy.</param>
/// <param name="Candidates">Models to try, best first. Empty when nothing can serve the request.</param>
public sealed record RoutingPlan(
    PromptClassificationResult Classification,
    ModelRequirements Requirements,
    IReadOnlyList<ModelCandidate> Candidates)
{
    /// <summary>The first candidate is the conversation's previous model (<see cref="PromptRoutingRequest.PreferredModel"/>), moved ahead of the ranking.</summary>
    public bool Sticky { get; init; }

    /// <summary>Models left out because <see cref="IModelReliability"/> says they are unreliable for this kind of work.</summary>
    public IReadOnlyList<string> Unreliable { get; init; } = [];

    /// <summary>The first candidate is the route's learned model (<see cref="RouteDefinition.PreferredModel"/>).</summary>
    public string? RouteModel { get; init; }

    /// <summary>The first candidate is a model on trial (<see cref="PromptRoutingRequest.TrialModel"/>).</summary>
    public string? Trial { get; init; }

    /// <summary>
    /// Why a latency-aware strategy (<see cref="SelectionStrategy.Fastest"/>, <see cref="SelectionStrategy.Balanced"/>)
    /// chose the first candidate, e.g. <c>fastest: qwen-small (310 ms to first byte) over cheaper llama (1,420 ms)</c>.
    /// </summary>
    public string? SelectionNote { get; init; }
}

/// <summary>Routes a prompt to an ordered list of catalog models.</summary>
public interface IPromptRouter
{
    /// <summary>The configured model catalog.</summary>
    IReadOnlyList<ModelDefinition> Models { get; }

    /// <summary>The configured routing profiles, by name (case-insensitive).</summary>
    IReadOnlyDictionary<string, RoutingProfile> Profiles => new Dictionary<string, RoutingProfile>();

    /// <summary>Classifies the prompt and ranks the models that can serve it.</summary>
    ValueTask<RoutingPlan> RouteAsync(PromptRoutingRequest request, CancellationToken cancellationToken = default);

    /// <summary>Ranks models for an existing classification (e.g. a fail-open fallback).</summary>
    RoutingPlan Plan(PromptClassificationResult classification, PromptRoutingRequest request);
}

/// <summary>
/// Default <see cref="IPromptRouter"/>: <see cref="IPromptClassifier"/> decides the requirements,
/// <see cref="IModelSelector"/> ranks the catalog, <see cref="IModelHealth"/> skips models in cooldown, and an optional
/// <see cref="IModelReliability"/> skips models for the kinds of work they keep failing at.
/// </summary>
public sealed class PromptRouter : IPromptRouter
{
    private readonly IPromptClassifier _classifier;
    private readonly IModelSelector _selector;
    private readonly IModelHealth _health;
    private readonly IModelReliability? _reliability;
    private readonly ModelDefinition[] _models;
    private readonly Dictionary<string, RoutingProfile> _profiles;
    private readonly HashSet<string> _followConversation;
    private readonly bool _localOnlyRoutes;
    private readonly Dictionary<string, string> _routeModels;
    private readonly IModelLatency? _latency;
    private readonly SelectionStrategy _strategy;
    private readonly TimeZoneInfo _zone;
    private readonly TimeProvider _time;

    /// <summary>Creates a router.</summary>
    public PromptRouter(
        IPromptClassifier classifier,
        SemanticRouterOptions options,
        IModelSelector? selector = null,
        IModelHealth? health = null,
        TimeProvider? time = null,
        IModelReliability? reliability = null,
        IModelLatency? latency = null)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(options);

        _classifier = classifier;
        _selector = selector ?? new DefaultModelSelector(options.Selection);
        _health = health ?? new ModelHealthTracker(options.Selection);
        _reliability = reliability;
        _latency = latency;
        _strategy = options.Selection.Strategy;
        _models = options.Models.ToArray();
        _profiles = new Dictionary<string, RoutingProfile>(options.Profiles, StringComparer.OrdinalIgnoreCase);
        _followConversation = new HashSet<string>(options.Routes.Where(r => r.FollowsConversation).Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        _localOnlyRoutes = options.Routes.Any(r => r.LocalOnly);
        _routeModels = options.Routes.Where(r => !string.IsNullOrWhiteSpace(r.PreferredModel))
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().PreferredModel!, StringComparer.OrdinalIgnoreCase);
        _zone = options.Selection.EffectiveTimeZone;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public IReadOnlyList<ModelDefinition> Models => _models;

    /// <inheritdoc />
    public IReadOnlyDictionary<string, RoutingProfile> Profiles => _profiles;

    /// <inheritdoc />
    public async ValueTask<RoutingPlan> RouteAsync(PromptRoutingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RoutingProfile? profile = ProfileOf(request);

        // Naming a cloud model, or a client's own request decided by its wording, skips classification, but not the
        // local-only routes: a prompt one of them covers stays on this machine whichever way it arrives.
        if (_localOnlyRoutes && !request.LocalOnly && profile?.LocalOnly != true
            && (request.Classification is not null || Named(request.RequestedModel) is { IsLocal: false })
            && (await _classifier.ClassifyAsync(request.Prompt, cancellationToken).ConfigureAwait(false)).LocalOnly)
        {
            request = request with { LocalOnly = true };
        }

        Func<ModelDefinition, bool> usable = Usable(request, profile);

        if (request.RequestedModel is { } requested
            && _models.FirstOrDefault(m => m.Enabled && string.Equals(m.Name, requested, StringComparison.OrdinalIgnoreCase)) is { } forced
            && usable(forced))
        {
            int tokens = request.EstimatedInputTokens ?? TokenEstimator.Estimate(request.Prompt);
            var requirements = new ModelRequirements { RequiredTier = forced.Tier, EstimatedInputTokens = tokens, Profile = request.Profile, LocalOnly = request.LocalOnly || profile?.LocalOnly == true };
            return new RoutingPlan(
                PromptClassificationResult.Bypass(forced.Tier, RoutingReason.ModelOverride),
                requirements,
                [new ModelCandidate(forced, forced.EstimateCost(tokens, 0), MeetsRequiredTier: true)]);
        }

        if (request.AffinityModel is { } affinity
            && _models.FirstOrDefault(m => m.Enabled && string.Equals(m.Name, affinity, StringComparison.OrdinalIgnoreCase)) is { } pinned
            && _health.IsAvailable(pinned)
            && usable(pinned))
        {
            // Continue the turn on the model that started it; the normally ranked models remain as fallbacks.
            var pinnedRequest = request with { EstimatedInputTokens = request.EstimatedInputTokens ?? TokenEstimator.Estimate(request.Prompt) };
            RoutingPlan plan = Plan(PromptClassificationResult.Bypass(pinned.Tier, RoutingReason.ConversationAffinity), pinnedRequest);
            ModelCandidate first = plan.Candidates.FirstOrDefault(c => c.Model == pinned)
                ?? new ModelCandidate(pinned, pinned.EstimateCost(plan.Requirements.EstimatedInputTokens, 0), MeetsRequiredTier: true);
            return plan with { Candidates = [first, .. plan.Candidates.Where(c => c.Model != pinned)] };
        }

        if (request.Classification is { } decided)
        {
            return Plan(decided, request with { EstimatedInputTokens = request.EstimatedInputTokens ?? TokenEstimator.Estimate(request.Prompt) });
        }

        PromptClassificationResult classification;
        using (System.Diagnostics.Activity? classify = RouterTelemetry.Source.StartActivity("semantic_router.classify"))
        {
            classification = await _classifier.ClassifyAsync(request.Prompt, cancellationToken).ConfigureAwait(false);
            if (request.ConversationFloor is { } conversation
                && (conversation > classification.RequiredTier || (classification.RouteName is { } route && _followConversation.Contains(route) && classification.Reason == RoutingReason.SemanticMatch)))
            {
                // Up to the floor; and down to it for a route that only carries the conversation on ("go ahead").
                classification = classification with { RequiredTier = conversation, Reason = RoutingReason.ConversationFloor };
            }

            if (request.MinimumTier is { } floor && floor > classification.RequiredTier)
            {
                classification = classification with { RequiredTier = floor, Reason = RoutingReason.RetryEscalation };
            }

            if (request.ExplicitTier is { } asked)
            {
                classification = classification with { RequiredTier = asked, Reason = RoutingReason.ExplicitCallout };
            }

            classify?.SetTag("semantic_router.tier", classification.RequiredTier);
            classify?.SetTag("semantic_router.reason", classification.Reason.ToString());
            classify?.SetTag("semantic_router.route", classification.RouteName);
            classify?.SetTag("semantic_router.similarity", classification.Similarity);
            classify?.SetTag("semantic_router.complexity", classification.Complexity.Score);
        }

        using System.Diagnostics.Activity? select = RouterTelemetry.Source.StartActivity("semantic_router.select");
        RoutingPlan routed = Plan(classification, request);
        select?.SetTag("semantic_router.candidates", routed.Candidates.Count);
        select?.SetTag("semantic_router.planned_model", routed.Candidates.Count > 0 ? routed.Candidates[0].Model.Name : null);
        select?.SetTag("semantic_router.local_only", routed.Requirements.LocalOnly);
        return routed;
    }

    /// <inheritdoc />
    public RoutingPlan Plan(PromptClassificationResult classification, PromptRoutingRequest request)
    {
        ArgumentNullException.ThrowIfNull(classification);
        ArgumentNullException.ThrowIfNull(request);
        RoutingProfile? profile = ProfileOf(request);

        var requirements = new ModelRequirements
        {
            RequiredTier = profile?.ClampTier(classification.RequiredTier) ?? classification.RequiredTier,
            RequiredCapabilities = classification.RequiredCapabilities
                .Concat(request.AdditionalCapabilities)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            LocalOnly = classification.LocalOnly || request.LocalOnly || profile?.LocalOnly == true,
            EstimatedInputTokens = request.EstimatedInputTokens ?? classification.Complexity.EstimatedTokens,
            Strategy = request.Strategy ?? profile?.Strategy,
            Profile = request.Profile,
        };

        // The profile and active hours decide which models exist for this request; health only orders and skips them.
        Func<ModelDefinition, bool> usable = Usable(request, profile);
        ModelDefinition[] models = _models.Where(usable).ToArray();
        IReadOnlyList<ModelCandidate> candidates = _selector.RankCandidates(requirements, models, _health.IsAvailable);
        IReadOnlyList<string> unreliable = [];
        if (_reliability is not null && candidates.Count > 0)
        {
            // Models that keep failing at this kind of work are left out, unless nothing else could serve it.
            string? route = classification.RouteName;
            IReadOnlyList<string> capabilities = requirements.RequiredCapabilities;
            ModelCandidate[] reliable = [.. candidates.Where(c => _reliability.IsReliable(c.Model, route, capabilities))];
            if (reliable.Length > 0 && reliable.Length < candidates.Count)
            {
                unreliable = [.. candidates.Where(c => !reliable.Contains(c)).Select(c => c.Model.Name)];
                candidates = reliable;
            }
        }

        if (candidates.Count == 0)
        {
            // Everything that qualifies is cooling down: trying a cooled-down model beats failing outright.
            candidates = _selector.RankCandidates(requirements, models);
        }

        // A plain match of a route: a model on trial, or the route's learned model, goes first even below the route's
        // tier, as long as it can serve this request. Escalated, explicit and conversation-held turns keep the ranking.
        string? trial = null, routeModel = null;
        if (classification.Reason == RoutingReason.SemanticMatch && classification.RouteName is { } routeName)
        {
            if (request.TrialModel is { } trying && Promote(trying, candidates, models, requirements, routeName) is { } tried)
            {
                (candidates, trial) = (tried, tried[0].Model.Name);
            }
            else if (_routeModels.TryGetValue(routeName, out string? learned) && Promote(learned, candidates, models, requirements, routeName) is { } learnedFirst)
            {
                (candidates, routeModel) = (learnedFirst, learnedFirst[0].Model.Name);
            }
        }

        // Stay on the conversation's model while it is exactly what the turn needs: available, and on the required
        // tier (not above it, so a conversation that no longer needs it moves to a cheaper model).
        if (request.PreferredModel is { } preferred
            && candidates.FirstOrDefault(c => string.Equals(c.Model.Name, preferred, StringComparison.OrdinalIgnoreCase)) is { } previous
            && previous != candidates[0]
            && previous.MeetsRequiredTier
            && previous.Model.Tier <= requirements.RequiredTier
            && _health.IsAvailable(previous.Model))
        {
            return new RoutingPlan(classification, requirements, [previous, .. candidates.Where(c => c != previous)]) { Sticky = true, Unreliable = unreliable };
        }

        bool sticky = request.PreferredModel is { } same && candidates.Count > 0 && string.Equals(candidates[0].Model.Name, same, StringComparison.OrdinalIgnoreCase);
        return new RoutingPlan(classification, requirements, candidates)
        {
            Unreliable = unreliable,
            Sticky = sticky,
            RouteModel = routeModel,
            Trial = trial,
            SelectionNote = sticky || routeModel is not null || trial is not null ? null : SelectionNote(requirements.Strategy ?? _strategy, candidates),
        };
    }

    /// <summary>For a latency-aware strategy: the chosen model's time to first byte, and the cheaper model it was chosen over.</summary>
    private string? SelectionNote(SelectionStrategy strategy, IReadOnlyList<ModelCandidate> candidates)
    {
        if (_latency is null || strategy is not (SelectionStrategy.Fastest or SelectionStrategy.Balanced)
            || candidates.Count == 0 || !candidates[0].MeetsRequiredTier)
        {
            return null;
        }

        ModelDefinition first = candidates[0].Model;
        ModelDefinition cheapest = candidates.Where(c => c.MeetsRequiredTier).OrderBy(c => c.EstimatedCost).ThenBy(c => c.Model.Tier).First().Model;
        string name = strategy == SelectionStrategy.Fastest ? "fastest" : "balanced";
        return cheapest == first
            ? $"{name}: {first.Name} ({Latency(first)}), also the cheapest"
            : $"{name}: {first.Name} ({Latency(first)}) over cheaper {cheapest.Name} ({Latency(cheapest)})";

        string Latency(ModelDefinition model) => _latency.TypicalTimeToFirstByte(model) is { } typical
            ? typical.TotalMilliseconds.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " ms to first byte"
            : "no recent latency";
    }

    /// <summary>
    /// <paramref name="candidates"/> with the model named <paramref name="name"/> first, when it can serve the request
    /// (allowed, enabled, capable, big enough, available and reliable for the route) whatever its tier; otherwise <see langword="null"/>.
    /// </summary>
    private IReadOnlyList<ModelCandidate>? Promote(
        string name, IReadOnlyList<ModelCandidate> candidates, ModelDefinition[] models, ModelRequirements requirements, string route)
    {
        if (models.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } model
            || _selector.RankCandidates(requirements with { RequiredTier = Math.Min(model.Tier, requirements.RequiredTier) }, [model], _health.IsAvailable) is not [var ranked]
            || _reliability?.IsReliable(model, route, requirements.RequiredCapabilities) == false)
        {
            return null;
        }

        ModelCandidate first = ranked with { MeetsRequiredTier = model.Tier >= requirements.RequiredTier };
        return [first, .. candidates.Where(c => c.Model != model)];
    }

    private RoutingProfile? ProfileOf(PromptRoutingRequest request) =>
        request.Profile is not { Length: > 0 } name ? null
        : _profiles.TryGetValue(name, out RoutingProfile? profile) ? profile
        : throw new ArgumentException($"Unknown routing profile '{name}'.", nameof(request));

    /// <summary>The enabled catalog model called <paramref name="name"/>, or <see langword="null"/>.</summary>
    private ModelDefinition? Named(string? name) =>
        name is null ? null : _models.FirstOrDefault(m => m.Enabled && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Models the profile allows, within their active hours, and local when the request must stay local.</summary>
    private Func<ModelDefinition, bool> Usable(PromptRoutingRequest request, RoutingProfile? profile)
    {
        DateTimeOffset now = _time.GetUtcNow();
        return model => (profile is null || profile.Allows(model)) && (!request.LocalOnly || model.IsLocal) && model.IsActiveAt(now, _zone);
    }
}
