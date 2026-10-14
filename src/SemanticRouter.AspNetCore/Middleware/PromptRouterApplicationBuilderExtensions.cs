using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter;
using SemanticRouter.AspNetCore;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Adds prompt routing to the request pipeline.</summary>
public static class PromptRouterApplicationBuilderExtensions
{
    /// <summary>
    /// Adds the prompt routing middleware. Place it after authentication/authorization and rate limiting,
    /// and before the endpoints that consume the decision (or instead of them, in Dispatch mode).
    /// </summary>
    public static IApplicationBuilder UsePromptRouter(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<PromptRoutingMiddleware>();
    }

    /// <summary>
    /// Maps a diagnostics endpoint that routes <c>{"prompt":"…"}</c> and returns the full decision (requirements,
    /// route candidates and the ranked models) without calling any model. It needs a gateway key where the chat
    /// endpoints do, and refuses web pages on other sites.
    /// </summary>
    public static RouteHandlerBuilder MapPromptClassification(
        this IEndpointRouteBuilder endpoints,
        [System.Diagnostics.CodeAnalysis.StringSyntax("Route")] string pattern = "/semantic-router/classify")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints.MapPost(pattern, async (ClassifyPromptRequest request, HttpContext context, IPromptRouter router) =>
            {
                if (string.IsNullOrWhiteSpace(request.Prompt))
                {
                    return Results.Problem("'prompt' is required.", statusCode: StatusCodes.Status400BadRequest);
                }

                (SelectionStrategy? strategy, string? requestedModel) = ModelAliases.Resolve(request.Model);
                string? profile = request.Profile is { Length: > 0 } named ? named : null;
                if (profile is not null && !router.Profiles.ContainsKey(profile))
                {
                    return Results.Problem($"Unknown routing profile '{profile}'.", statusCode: StatusCodes.Status400BadRequest);
                }

                // Reuse the middleware's plan when it already routed this exact prompt.
                RoutingPlan plan =
                    context.Features.Get<IPromptRoutingFeature>() is { } feature && feature.Prompt == request.Prompt
                        ? feature.Plan
                        : await router.RouteAsync(
                            new PromptRoutingRequest { Prompt = request.Prompt, Strategy = strategy, RequestedModel = requestedModel, Profile = profile },
                            context.RequestAborted).ConfigureAwait(false);

                return Results.Ok(PromptClassificationResponse.From(plan));
            })
            .WithName("ClassifyPrompt")
            .WithSummary("Routes a prompt and explains the decision without calling a model.")
            .RequireGatewayAccess(ChatProtocol.OpenAI);
    }

    /// <summary>
    /// Maps the Anthropic <c>POST /v1/messages/count_tokens</c> endpoint, which agentic clients call to track context
    /// size. Returns the router's own estimate; no model is called.
    /// </summary>
    public static RouteHandlerBuilder MapAnthropicTokenCount(
        this IEndpointRouteBuilder endpoints,
        [System.Diagnostics.CodeAnalysis.StringSyntax("Route")] string pattern = "/v1/messages/count_tokens")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints.MapPost(pattern, async (HttpContext context) =>
            {
                using System.Text.Json.JsonDocument document = await System.Text.Json.JsonDocument
                    .ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted).ConfigureAwait(false);
                ChatCompletionRequest request = ChatCompletionRequest.ParseAnthropic(document.RootElement);
                return Results.Ok(new TokenCountResponse(request.EstimatedInputTokens));
            })
            .WithName("CountAnthropicTokens")
            .WithSummary("Estimates the input tokens of an Anthropic Messages request.")
            .RequireGatewayAccess(ChatProtocol.Anthropic);
    }

    /// <summary>
    /// Maps an OpenAI-compatible <c>GET /v1/models</c> listing the routing aliases (<c>auto</c>, <c>auto:cheapest</c>,
    /// <c>auto:best</c>, <c>auto:fastest</c>, <c>auto:balanced</c>, <c>auto:priority</c>) and every enabled catalog model, so chat clients can offer them.
    /// </summary>
    public static RouteHandlerBuilder MapRouterModels(
        this IEndpointRouteBuilder endpoints,
        [System.Diagnostics.CodeAnalysis.StringSyntax("Route")] string pattern = "/v1/models")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints.MapGet(pattern, (IPromptRouter router, IModelHealth health, IServiceProvider services) =>
            {
                IReadOnlyDictionary<string, DateTimeOffset> cooldowns = health.GetCooldowns();
                RouterConfiguration? configuration = services.GetService<RouterRuntimeProvider>()?.Current.Configuration;
                IEnumerable<(string Id, string Description)> virtualModels = configuration is null ? [] :
                    configuration.Middleware.VirtualModels
                        .Select(v => (v.Key, v.Value.Description ?? $"Routes{(v.Value.Profile is { } p ? $" under profile '{p}'" : string.Empty)}{(v.Value.Strategy is { } s ? $" with {s}" : string.Empty)}."))
                        .Concat(configuration.Profiles.Select(p => ($"{ModelAliases.Auto}@{p.Key}", p.Value.Description ?? $"Routes automatically under profile '{p.Key}'.")));
                var data = ModelAliases.All.Concat(virtualModels)
                    .Select(a => new RouterModelInfo(a.Id, "model", "semantic-router", null, null, a.Description, null))
                    .Concat(router.Models.Where(m => m.Enabled).Select(m => new RouterModelInfo(
                        m.Name, "model", m.Connector, m.Tier, m.IsLocal, m.Description,
                        cooldowns.TryGetValue(m.Name, out DateTimeOffset until) ? until : null)))
                    .ToArray();
                return Results.Ok(new RouterModelList("list", data));
            })
            .WithName("ListRouterModels")
            .WithSummary("Lists routing aliases and catalog models.")
            .RequireGatewayAccess(ChatProtocol.OpenAI);
    }
}

/// <summary>The <c>count_tokens</c> response.</summary>
/// <param name="InputTokens">Estimated input tokens.</param>
public sealed record TokenCountResponse([property: System.Text.Json.Serialization.JsonPropertyName("input_tokens")] int InputTokens);

/// <summary>Request body for the classification endpoint.</summary>
/// <param name="Prompt">The prompt to route.</param>
/// <param name="Model">Optional model or alias, as a chat client would send it (<c>auto@profile</c> and virtual models included).</param>
/// <param name="Profile">Optional routing profile to route under.</param>
public sealed record ClassifyPromptRequest(string Prompt, string? Model = null, string? Profile = null);

/// <summary>An entry in <c>GET /v1/models</c>.</summary>
public sealed record RouterModelInfo(
    string Id,
    [property: System.Text.Json.Serialization.JsonPropertyName("object")] string ObjectType,
    [property: System.Text.Json.Serialization.JsonPropertyName("owned_by")] string OwnedBy,
    int? Tier,
    bool? Local,
    string? Description,
    DateTimeOffset? CoolingDownUntil);

/// <summary>The <c>GET /v1/models</c> response.</summary>
public sealed record RouterModelList(
    [property: System.Text.Json.Serialization.JsonPropertyName("object")] string ObjectType,
    IReadOnlyList<RouterModelInfo> Data);

/// <summary>A serializable view of a <see cref="RoutingPlan"/>.</summary>
public sealed record PromptClassificationResponse(
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
    /// <summary>A scored route.</summary>
    public sealed record RouteCandidate(string Route, int Tier, float Similarity);

    /// <summary>A ranked model, in the order it would be tried.</summary>
    public sealed record ModelChoice(string Model, string Connector, int Tier, decimal EstimatedCostUsd, bool MeetsRequiredTier);

    /// <summary>Projects a <see cref="RoutingPlan"/>.</summary>
    public static PromptClassificationResponse From(RoutingPlan plan)
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
            plan.Candidates.Select(c => new ModelChoice(c.Model.Name, c.Model.Connector, c.Model.Tier, c.EstimatedCost, c.MeetsRequiredTier)).ToArray(),
            plan.Requirements.Profile);
    }
}
