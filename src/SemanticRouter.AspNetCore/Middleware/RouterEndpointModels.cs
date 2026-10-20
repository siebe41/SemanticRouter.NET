namespace SemanticRouter.AspNetCore;

/// <summary>The <c>count_tokens</c> response.</summary>
/// <param name="InputTokens">Estimated input tokens.</param>
internal sealed record TokenCountResponse([property: System.Text.Json.Serialization.JsonPropertyName("input_tokens")] int InputTokens);

/// <summary>Request body for the classification endpoint.</summary>
/// <param name="Prompt">The prompt to route.</param>
/// <param name="Model">Optional model or alias, as a chat client would send it (<c>auto@profile</c> and virtual models included).</param>
/// <param name="Profile">Optional routing profile to route under.</param>
internal sealed record ClassifyPromptRequest(string Prompt, string? Model = null, string? Profile = null);

/// <summary>An entry in <c>GET /v1/models</c>: the OpenAI fields, plus the name and description a model picker shows.</summary>
internal sealed record RouterModelInfo(
    string Id,
    [property: System.Text.Json.Serialization.JsonPropertyName("object")] string ObjectType,
    [property: System.Text.Json.Serialization.JsonPropertyName("owned_by")] string OwnedBy,
    int? Tier,
    bool? Local,
    string? Description,
    DateTimeOffset? CoolingDownUntil,
    [property: System.Text.Json.Serialization.JsonPropertyName("display_name")] string? DisplayName = null);

/// <summary>The <c>GET /v1/models</c> response.</summary>
internal sealed record RouterModelList(
    [property: System.Text.Json.Serialization.JsonPropertyName("object")] string ObjectType,
    IReadOnlyList<RouterModelInfo> Data);

/// <summary>A serializable view of a <see cref="RoutingPlan"/>.</summary>
internal sealed record PromptClassificationResponse(
    int RequiredTier,
    RoutingReason Reason,
    string? Route,
    float? Similarity,
    bool LocalOnly,
    IReadOnlyList<string> RequiredCapabilities,
    double ElapsedMilliseconds,
    ComplexityAnalysis Complexity,
    IReadOnlyList<PromptClassificationResponse.RouteCandidate> Routes,
    IReadOnlyList<PromptClassificationResponse.ModelChoice> Models,
    string? Profile = null)
{
    /// <summary>
    /// The inline callout the prompt starts with (<c>@opus</c>, <c>@local</c>, <c>@tier2</c>), as the gateway records it:
    /// the prompt was routed as it asks and classified without it. <see langword="null"/> when there is none.
    /// </summary>
    public string? Callout { get; init; }

    /// <summary>A scored route.</summary>
    public sealed record RouteCandidate(string Route, int Tier, float Similarity);

    /// <summary>A ranked model, in the order it would be tried.</summary>
    /// <param name="Model">The catalog name.</param>
    /// <param name="Connector">The connector that serves it.</param>
    /// <param name="Tier">Its tier.</param>
    /// <param name="EstimatedCostUsd">The estimated cost of this request on it.</param>
    /// <param name="MeetsRequiredTier">Whether its tier is high enough.</param>
    /// <param name="IsLocal">Whether prompts sent to it stay on infrastructure you control.</param>
    /// <param name="Loaded">Whether a local model is loaded now (<see cref="IModelResidency"/>); <see langword="null"/>: unknown.</param>
    public sealed record ModelChoice(string Model, string Connector, int Tier, decimal EstimatedCostUsd, bool MeetsRequiredTier, bool IsLocal = false, bool? Loaded = null);

    /// <summary>Projects a <see cref="RoutingPlan"/>.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="residency">Which local models are loaded, to show with them.</param>
    public static PromptClassificationResponse From(RoutingPlan plan, IModelResidency? residency = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        PromptClassificationResult result = plan.Classification;
        return new(
            plan.Requirements.RequiredTier,
            result.Reason,
            result.RouteName,
            result.Similarity,
            plan.Requirements.LocalOnly,
            plan.Requirements.RequiredCapabilities,
            Math.Round(result.Elapsed.TotalMilliseconds, 3),
            result.Complexity,
            result.TopScores.Select(s => new RouteCandidate(s.Route.Name, s.Route.Tier, s.Similarity)).ToArray(),
            plan.Candidates.Select(c => new ModelChoice(
                c.Model.Name, c.Model.Connector, c.Model.Tier, c.EstimatedCost, c.MeetsRequiredTier, c.Model.IsLocal, residency?.IsLoaded(c.Model))).ToArray(),
            plan.Requirements.Profile);
    }
}
