using System.Collections.Concurrent;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// What-if replay (#373): runs recent requests and the evaluation set through two configurations, the running one and a
/// candidate, and reports what would move. Each side gets a classifier and model selector of its own, built from its
/// configuration off to the side and never swapped into the running runtime. No model is called; only the embedding
/// provider is, and each text is embedded once even when both sides classify it.
/// </summary>
/// <remarks>
/// <para>
/// The decisions are per message: both sides route every prompt as the first turn of a conversation, without conversation
/// floors, sticky models, warm caches, retries, cooldowns or measured latency, so state that changed since the request
/// does not show up as a move. A configuration replayed against itself therefore moves nothing.
/// </para>
/// <para>
/// No prompt text is kept for it: a request can be replayed only when its activity entry's prompt preview
/// (<see cref="StateOptions.StorePrompts"/>) holds the whole prompt. Requests the router did not classify (a requested
/// model, a tool-result turn, a callout, a client side request) are counted and left out.
/// </para>
/// </remarks>
internal static class WhatIfReplay
{
    /// <summary>The most recent requests replayed; older ones in the window are counted as not replayed.</summary>
    public const int MaxRequests = 2000;

    /// <summary>The moves listed in a report (the totals count them all).</summary>
    public const int MaxMovesListed = 200;

    /// <summary>The decisions a request routed this way did not get from the classifier.</summary>
    private static readonly HashSet<RoutingReason> NotClassified =
    [
        RoutingReason.ModelOverride,
        RoutingReason.ConversationAffinity,
        RoutingReason.ExplicitCallout,
        RoutingReason.ClientHousekeeping,
        RoutingReason.ResumedSession,
        RoutingReason.RateLimited,
        RoutingReason.EmptyPrompt,
    ];

    /// <summary>Replays <paramref name="entries"/> (oldest first, as the activity log gives them) and <paramref name="cases"/>.</summary>
    public static async Task<ReplayReport> RunAsync(
        ReplaySide current,
        ReplaySide candidate,
        IReadOnlyList<RoutingActivityEntry> entries,
        IReadOnlyList<EvaluationCase> cases,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(cases);

        // Both sides embed through one cache when they share a provider, so an unchanged utterance or prompt costs one call.
        var currentEmbeddings = new MemoizingEmbeddingProvider(current.Embeddings);
        MemoizingEmbeddingProvider candidateEmbeddings = ReferenceEquals(current.Embeddings, candidate.Embeddings)
            ? currentEmbeddings
            : new MemoizingEmbeddingProvider(candidate.Embeddings);
        var before = new Replayer(current.Configuration, currentEmbeddings);
        var after = new Replayer(candidate.Configuration, candidateEmbeddings);

        int notClassified = 0, notKept = 0, truncated = 0, beyondLimit = 0;
        var replayable = new List<RoutingActivityEntry>();
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            RoutingActivityEntry entry = entries[i];
            if (entry.ToolContinuation || NotClassified.Contains(entry.Reason))
            {
                notClassified++;
            }
            else if (string.IsNullOrWhiteSpace(entry.PromptPreview))
            {
                notKept++;
            }
            else if (entry.PromptPreview.EndsWith('…'))
            {
                truncated++;
            }
            else if (replayable.Count >= MaxRequests)
            {
                beyondLimit++;
            }
            else
            {
                replayable.Add(entry);
            }
        }

        var tiers = new SortedDictionary<int, (int Before, int After)>();
        var connectors = new SortedDictionary<string, (bool Subscription, int Before, int After)>(StringComparer.OrdinalIgnoreCase);
        var moves = new List<ReplayMove>();
        int moved = 0, priced = 0;
        decimal costBefore = 0, costAfter = 0;
        foreach (RoutingActivityEntry entry in replayable)
        {
            var input = new ReplayInput(entry.PromptPreview, entry.EstimatedInputTokens, entry.Profile, entry.LocalOnly, entry.HasTools, entry.Timestamp);
            ReplayDecision was = await before.DecideAsync(input, cancellationToken).ConfigureAwait(false);
            ReplayDecision would = await after.DecideAsync(input, cancellationToken).ConfigureAwait(false);
            Count(tiers, was.Tier, after: false);
            Count(tiers, would.Tier, after: true);
            Count(connectors, was, after: false);
            Count(connectors, would, after: true);

            decimal? wasCost = null, wouldCost = null;
            if (entry.InputTokens is { } inputTokens)
            {
                long output = entry.OutputTokens ?? 0, cacheRead = entry.CacheReadTokens ?? 0, cacheWrite = entry.CacheCreationTokens ?? 0;
                wasCost = was.Definition?.Cost(inputTokens, output, cacheRead, cacheWrite) ?? 0;
                wouldCost = would.Definition?.Cost(inputTokens, output, cacheRead, cacheWrite) ?? 0;
                costBefore += wasCost.Value;
                costAfter += wouldCost.Value;
                priced++;
            }

            if (!was.SameAs(would))
            {
                moved++;
                if (moves.Count < MaxMovesListed)
                {
                    moves.Add(new ReplayMove
                    {
                        Id = entry.Id,
                        At = entry.Timestamp,
                        Prompt = entry.PromptPreview,
                        Client = entry.Client,
                        Before = was,
                        After = would,
                        CostBeforeUsd = wasCost,
                        CostAfterUsd = wouldCost,
                    });
                }
            }
        }

        ReplayEvaluation? evaluation = cases.Count == 0 ? null : await EvaluateAsync(before, after, cases, cancellationToken).ConfigureAwait(false);
        return new ReplayReport
        {
            Requests = new ReplayCounts
            {
                InWindow = entries.Count,
                Replayed = replayable.Count,
                NotClassified = notClassified,
                PromptNotKept = notKept,
                PromptTruncated = truncated,
                BeyondLimit = beyondLimit,
            },
            Moved = moved,
            Moves = moves,
            Tiers = [.. tiers.Select(t => new ReplayTierCount(t.Key, t.Value.Before, t.Value.After))],
            Connectors = [.. connectors.Select(c => new ReplayConnectorCount(c.Key, c.Value.Subscription, c.Value.Before, c.Value.After))],
            Cost = new ReplayCost { BeforeUsd = costBefore, AfterUsd = costAfter, Priced = priced, Unpriced = replayable.Count - priced },
            Evaluation = evaluation,
            Embedded = currentEmbeddings.Embedded + (ReferenceEquals(currentEmbeddings, candidateEmbeddings) ? 0 : candidateEmbeddings.Embedded),
        };
    }

    private static async Task<ReplayEvaluation> EvaluateAsync(Replayer before, Replayer after, IReadOnlyList<EvaluationCase> cases, CancellationToken cancellationToken)
    {
        int correctBefore = 0, correctAfter = 0, moved = 0;
        var moves = new List<ReplayMove>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (EvaluationCase item in cases)
        {
            var input = new ReplayInput(item.Prompt, 0, null, item.LocalOnly, false, now);
            ReplayDecision was = await before.DecideAsync(input, cancellationToken).ConfigureAwait(false);
            ReplayDecision would = await after.DecideAsync(input, cancellationToken).ConfigureAwait(false);
            correctBefore += was.Tier == item.ExpectedTier ? 1 : 0;
            correctAfter += would.Tier == item.ExpectedTier ? 1 : 0;
            if (!was.SameAs(would))
            {
                moved++;
                if (moves.Count < MaxMovesListed)
                {
                    moves.Add(new ReplayMove { Prompt = item.Prompt, ExpectedTier = item.ExpectedTier, LocalOnly = item.LocalOnly, Before = was, After = would });
                }
            }
        }

        return new ReplayEvaluation { Cases = cases.Count, CorrectBefore = correctBefore, CorrectAfter = correctAfter, Moved = moved, Moves = moves };
    }

    private static void Count(SortedDictionary<int, (int Before, int After)> tiers, int tier, bool after)
    {
        (int b, int a) = tiers.TryGetValue(tier, out (int, int) seen) ? seen : (0, 0);
        tiers[tier] = after ? (b, a + 1) : (b + 1, a);
    }

    private static void Count(SortedDictionary<string, (bool Subscription, int Before, int After)> connectors, ReplayDecision decision, bool after)
    {
        if (decision.Connector is not { } name)
        {
            return;
        }

        (bool s, int b, int a) = connectors.TryGetValue(name, out (bool, int, int) seen) ? seen : (decision.Subscription, 0, 0);
        connectors[name] = (s || decision.Subscription, after ? b : b + 1, after ? a + 1 : a);
    }

    private readonly record struct ReplayInput(string Prompt, int EstimatedInputTokens, string? Profile, bool LocalOnly, bool HasTools, DateTimeOffset At);

    /// <summary>One side: a router built from a configuration, stateless apart from the clock it is given.</summary>
    private sealed class Replayer
    {
        private static readonly string[] Tools = ["tools"];
        private readonly RouterConfiguration _configuration;
        private readonly PromptRouter _router;
        private readonly ReplayClock _clock = new();

        public Replayer(RouterConfiguration configuration, IEmbeddingProvider embeddings)
        {
            // Its own copy: the classification cache would only remember this run's prompts.
            _configuration = configuration.Clone();
            _configuration.ClassificationCache.Enabled = false;
            var classifier = new SemanticPromptClassifier(embeddings, _configuration, new SemanticPromptClassifierDependencies
            {
                ComplexityAnalyzer = new HeuristicComplexityAnalyzer(_configuration.Complexity),
                RouteSelector = new DefaultRouteSelector(_configuration),
            });

            // No health, latency, cache share or residency: the live trackers describe now, not when the request came in.
            _router = new PromptRouter(classifier, _configuration, new PromptRouterDependencies
            {
                Selector = new DefaultModelSelector(_configuration.Selection),
                Time = _clock,
            });
        }

        public async Task<ReplayDecision> DecideAsync(ReplayInput input, CancellationToken cancellationToken)
        {
            // Active hours apply as they did when the request came in.
            _clock.Now = input.At;
            RoutingPlan plan = await _router.RouteAsync(
                new PromptRoutingRequest
                {
                    Prompt = input.Prompt,
                    EstimatedInputTokens = input.EstimatedInputTokens,
                    Profile = input.Profile is { } profile && _configuration.Profiles.ContainsKey(profile) ? profile : null,

                    // A request that was local-only stays so: its prompt is not sent to embeddings that leave the machine.
                    LocalOnly = input.LocalOnly,
                    EmbeddingsAllowed = !input.LocalOnly || _configuration.Embeddings.KeepsPromptsLocal,
                    AdditionalCapabilities = input.HasTools ? Tools : [],
                },
                cancellationToken).ConfigureAwait(false);

            ModelDefinition? model = plan.Candidates.Count > 0 ? plan.Candidates[0].Model : null;
            bool subscription = model is not null
                && _configuration.Connectors.TryGetValue(model.Connector, out ConnectorOptions? connector)
                && connector.RunsProgram;
            return new ReplayDecision
            {
                Tier = plan.Classification.RequiredTier,
                Route = plan.Classification.RouteName,
                Reason = plan.Classification.Reason,
                Model = model?.Name,
                Connector = model?.Connector,
                Subscription = subscription,
                Definition = model,
            };
        }
    }

    private sealed class ReplayClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

/// <summary>A configuration to replay and the embedding provider its settings give.</summary>
internal sealed record ReplaySide(RouterConfiguration Configuration, IEmbeddingProvider Embeddings);

/// <summary>Embeds each distinct text once and remembers it, for the length of one replay.</summary>
internal sealed class MemoizingEmbeddingProvider(IEmbeddingProvider inner) : IEmbeddingProvider
{
    private readonly ConcurrentDictionary<string, ReadOnlyMemory<float>> _seen = new(StringComparer.Ordinal);

    /// <summary>The texts sent to the provider.</summary>
    public int Embedded => _seen.Count;

    public async ValueTask<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_seen.TryGetValue(text, out ReadOnlyMemory<float> vector))
        {
            return vector;
        }

        vector = await inner.GenerateEmbeddingAsync(text, cancellationToken).ConfigureAwait(false);
        _seen[text] = vector;
        return vector;
    }

    public async ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        string[] missing = [.. texts.Where(t => !_seen.ContainsKey(t)).Distinct(StringComparer.Ordinal)];
        if (missing.Length > 0)
        {
            IReadOnlyList<ReadOnlyMemory<float>> vectors = await inner.GenerateEmbeddingsAsync(missing, cancellationToken).ConfigureAwait(false);
            for (int i = 0; i < missing.Length; i++)
            {
                _seen[missing[i]] = vectors[i];
            }
        }

        return [.. texts.Select(t => _seen[t])];
    }
}

/// <summary>What a replay found.</summary>
internal sealed record ReplayReport
{
    /// <summary>Always <c>per-message</c>: conversation floors, sticky models and retries are not simulated.</summary>
    public string Scope { get; init; } = "per-message";

    public DateTimeOffset Since { get; init; }

    public DateTimeOffset Until { get; init; }

    public required ReplayCounts Requests { get; init; }

    /// <summary>Replayed requests whose tier, route or first model differs.</summary>
    public required int Moved { get; init; }

    /// <summary>The most recent moves, at most <see cref="WhatIfReplay.MaxMovesListed"/>.</summary>
    public required IReadOnlyList<ReplayMove> Moves { get; init; }

    public required IReadOnlyList<ReplayTierCount> Tiers { get; init; }

    /// <summary>Replayed requests by the connector of their first model; <c>Subscription</c> marks one that runs a program (a CLI).</summary>
    public required IReadOnlyList<ReplayConnectorCount> Connectors { get; init; }

    public required ReplayCost Cost { get; init; }

    /// <summary>The evaluation set through both sides, when it was asked for and has cases.</summary>
    public ReplayEvaluation? Evaluation { get; init; }

    /// <summary>Texts sent to the embedding provider (route utterances and prompts, each once).</summary>
    public int Embedded { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>The requests in the window and why some were not replayed.</summary>
internal sealed record ReplayCounts
{
    public required int InWindow { get; init; }

    public required int Replayed { get; init; }

    /// <summary>Routed without the classifier: a requested model, a tool-result turn, a callout, a client side request.</summary>
    public required int NotClassified { get; init; }

    /// <summary>No prompt preview kept (<c>State:StorePrompts</c> is <c>Off</c>, or it was off then).</summary>
    public required int PromptNotKept { get; init; }

    /// <summary>The prompt was longer than its kept preview, so it cannot be classified as it was.</summary>
    public required int PromptTruncated { get; init; }

    /// <summary>Older than the <see cref="WhatIfReplay.MaxRequests"/> most recent replayable requests.</summary>
    public required int BeyondLimit { get; init; }
}

/// <summary>A decision: what the classifier required and the model first in line.</summary>
internal sealed record ReplayDecision
{
    public required int Tier { get; init; }

    public string? Route { get; init; }

    public required RoutingReason Reason { get; init; }

    /// <summary>The first candidate, or <see langword="null"/> when no model could serve the request.</summary>
    public string? Model { get; init; }

    public string? Connector { get; init; }

    public bool Subscription { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public ModelDefinition? Definition { get; init; }

    public bool SameAs(ReplayDecision other) =>
        Tier == other.Tier
        && string.Equals(Route, other.Route, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Model, other.Model, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A request or evaluation prompt that would land elsewhere.</summary>
internal sealed record ReplayMove
{
    /// <summary>The activity entry; <see langword="null"/> for an evaluation prompt.</summary>
    public long? Id { get; init; }

    public DateTimeOffset? At { get; init; }

    public required string Prompt { get; init; }

    public string? Client { get; init; }

    /// <summary>The tier an evaluation prompt is labelled with.</summary>
    public int? ExpectedTier { get; init; }

    public bool LocalOnly { get; init; }

    public required ReplayDecision Before { get; init; }

    public required ReplayDecision After { get; init; }

    /// <summary>The request's reported tokens at the current model's prices; <see langword="null"/> without token counts.</summary>
    public decimal? CostBeforeUsd { get; init; }

    /// <summary>The same tokens at the candidate model's prices in the candidate configuration.</summary>
    public decimal? CostAfterUsd { get; init; }
}

internal sealed record ReplayTierCount(int Tier, int Before, int After);

internal sealed record ReplayConnectorCount(string Connector, bool Subscription, int Before, int After);

/// <summary>
/// The replayed requests' reported tokens priced at each side's first model. An estimate: another model would answer
/// at another length.
/// </summary>
internal sealed record ReplayCost
{
    public required decimal BeforeUsd { get; init; }

    public required decimal AfterUsd { get; init; }

    public decimal DeltaUsd => AfterUsd - BeforeUsd;

    /// <summary>Replayed requests with reported token counts.</summary>
    public required int Priced { get; init; }

    /// <summary>Replayed requests without them (failed, or the backend reported none): left out of the cost.</summary>
    public required int Unpriced { get; init; }
}

/// <summary>The evaluation set through both sides.</summary>
internal sealed record ReplayEvaluation
{
    public required int Cases { get; init; }

    /// <summary>Prompts routed to the tier they are labelled with, by the running configuration.</summary>
    public required int CorrectBefore { get; init; }

    /// <summary>The same, by the candidate.</summary>
    public required int CorrectAfter { get; init; }

    public required int Moved { get; init; }

    public required IReadOnlyList<ReplayMove> Moves { get; init; }
}
