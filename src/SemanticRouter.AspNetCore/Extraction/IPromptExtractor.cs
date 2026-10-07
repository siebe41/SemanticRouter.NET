using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Pulls the text to classify out of an incoming JSON request. Replace the default
/// <see cref="JsonPromptExtractor"/> to support a custom payload shape.
/// </summary>
public interface IPromptExtractor
{
    /// <summary>Returns the prompt text, or <see langword="null"/> when the body carries none.</summary>
    /// <param name="context">The current request (headers, route values, …).</param>
    /// <param name="body">The parsed JSON request body.</param>
    string? ExtractPrompt(HttpContext context, JsonElement body);
}
