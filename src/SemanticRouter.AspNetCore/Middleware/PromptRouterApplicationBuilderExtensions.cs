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
                if (context.Features.Get<IPromptRoutingFeature>() is { } feature && feature.Prompt == request.Prompt)
                {
                    return Results.Ok(PromptClassificationResponse.From(feature.Plan));
                }

                // As the gateway routes a first turn: a leading callout ("@opus …") applies and is not classified.
                var routing = new PromptRoutingRequest { Prompt = request.Prompt, Strategy = strategy, RequestedModel = requestedModel, Profile = profile };
                (RoutingPlan plan, string? callout) = context.RequestServices.GetService<RouterRuntimeProvider>() is { } runtime
                    ? await Callouts.RouteAsync(runtime.Current, router, routing, context.RequestAborted).ConfigureAwait(false)
                    : (await router.RouteAsync(routing, context.RequestAborted).ConfigureAwait(false), null);

                return Results.Ok(PromptClassificationResponse.From(plan, context.RequestServices.GetService<IModelResidency>()) with { Callout = callout });
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
    /// <c>auto:best</c>, <c>auto:fastest</c>, <c>auto:balanced</c>, <c>auto:priority</c>), the router's own names
    /// (<c>semantic-router</c>, and <c>claude-semantic-router</c> with <c>-cheapest</c> and <c>-best</c> for Claude
    /// Code's gateway model discovery) and every enabled catalog model, so chat clients can offer them. Entries carry
    /// <c>display_name</c> and <c>description</c> besides the OpenAI fields.
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
                    .Select(a => new RouterModelInfo(a.Id, "model", "semantic-router", null, null, a.Description, null, a.Id))
                    .Concat(ModelAliases.RouterNames.Select(n => new RouterModelInfo(n.Id, "model", "semantic-router", null, null, n.Description, null, n.DisplayName)))
                    .Concat(router.Models.Where(m => m.Enabled).Select(m => new RouterModelInfo(
                        m.Name, "model", m.Connector, m.Tier, m.IsLocal, m.Description,
                        cooldowns.TryGetValue(m.Name, out DateTimeOffset until) ? until : null, m.Name)))
                    .ToArray();
                return Results.Ok(new RouterModelList("list", data));
            })
            .WithName("ListRouterModels")
            .WithSummary("Lists routing aliases and catalog models.")
            .RequireGatewayAccess(ChatProtocol.OpenAI);
    }
}
