using Microsoft.AspNetCore.Http;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Request feature set by the prompt routing middleware once a request has been routed.
/// </summary>
public interface IPromptRoutingFeature
{
    /// <summary>The extracted prompt text that was classified.</summary>
    string Prompt { get; }

    /// <summary>The routing plan: requirements and the ranked candidate models.</summary>
    RoutingPlan Plan { get; }

    /// <summary>The classification behind <see cref="Plan"/>.</summary>
    PromptClassificationResult Result => Plan.Classification;
}

internal sealed record PromptRoutingFeature(string Prompt, RoutingPlan Plan) : IPromptRoutingFeature;

/// <summary>Convenience accessors for <see cref="IPromptRoutingFeature"/>.</summary>
public static class PromptRoutingHttpContextExtensions
{
    /// <summary>
    /// Returns the routing plan for this request, or <see langword="null"/> when the middleware did not route it (not a
    /// matching JSON POST, or no prompt found). In Annotate mode, <c>plan.Candidates[0]</c> is the model to call.
    /// </summary>
    public static RoutingPlan? GetPromptRouting(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<IPromptRoutingFeature>()?.Plan;
    }
}
