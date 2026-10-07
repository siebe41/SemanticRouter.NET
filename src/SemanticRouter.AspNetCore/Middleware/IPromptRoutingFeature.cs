using Microsoft.AspNetCore.Http;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Request feature set by the prompt routing middleware once a request has been classified.
/// </summary>
public interface IPromptRoutingFeature
{
    /// <summary>The extracted prompt text that was classified.</summary>
    string Prompt { get; }

    /// <summary>The routing decision.</summary>
    PromptClassificationResult Result { get; }
}

internal sealed record PromptRoutingFeature(string Prompt, PromptClassificationResult Result) : IPromptRoutingFeature;

/// <summary>Convenience accessors for <see cref="IPromptRoutingFeature"/>.</summary>
public static class PromptRoutingHttpContextExtensions
{
    /// <summary>
    /// Returns the routing decision for this request, or <see langword="null"/> when the middleware did not
    /// classify it (not a matching JSON POST, or no prompt found).
    /// </summary>
    public static PromptClassificationResult? GetPromptRouting(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Features.Get<IPromptRoutingFeature>()?.Result;
    }
}
