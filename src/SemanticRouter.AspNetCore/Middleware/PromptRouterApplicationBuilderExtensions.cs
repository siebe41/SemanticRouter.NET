using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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
    /// Maps a diagnostics endpoint that classifies <c>{"prompt":"…"}</c> and returns the full decision,
    /// useful for tuning thresholds. Protect or omit it in production.
    /// </summary>
    public static RouteHandlerBuilder MapPromptClassification(
        this IEndpointRouteBuilder endpoints,
        [System.Diagnostics.CodeAnalysis.StringSyntax("Route")] string pattern = "/semantic-router/classify")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints.MapPost(pattern, async (ClassifyPromptRequest request, HttpContext context, IPromptClassifier classifier) =>
            {
                if (string.IsNullOrWhiteSpace(request.Prompt))
                {
                    return Results.Problem("'prompt' is required.", statusCode: StatusCodes.Status400BadRequest);
                }

                // Reuse the middleware's decision when it already classified this exact prompt.
                PromptClassificationResult result =
                    context.Features.Get<IPromptRoutingFeature>() is { } feature && feature.Prompt == request.Prompt
                        ? feature.Result
                        : await classifier.ClassifyAsync(request.Prompt, context.RequestAborted).ConfigureAwait(false);

                return Results.Ok(PromptClassificationResponse.From(result));
            })
            .WithName("ClassifyPrompt")
            .WithSummary("Classifies a prompt and explains the routing decision.");
    }
}

/// <summary>Request body for the classification endpoint.</summary>
/// <param name="Prompt">The prompt to classify.</param>
public sealed record ClassifyPromptRequest(string Prompt);

/// <summary>A serializable view of <see cref="PromptClassificationResult"/>.</summary>
public sealed record PromptClassificationResponse(
    RouteTarget Target,
    RoutingReason Reason,
    string? Route,
    float? Similarity,
    bool Pinned,
    double ElapsedMilliseconds,
    ComplexityAnalysis Complexity,
    IReadOnlyList<PromptClassificationResponse.Candidate> Candidates)
{
    /// <summary>A scored route candidate.</summary>
    public sealed record Candidate(string Route, RouteTarget Target, float Similarity);

    /// <summary>Projects a <see cref="PromptClassificationResult"/>.</summary>
    public static PromptClassificationResponse From(PromptClassificationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(
            result.Target,
            result.Reason,
            result.RouteName,
            result.Similarity,
            result.IsPinned,
            Math.Round(result.Elapsed.TotalMilliseconds, 3),
            result.Complexity,
            result.TopScores.Select(s => new Candidate(s.Route.Name, s.Route.Target, s.Similarity)).ToArray());
    }
}
