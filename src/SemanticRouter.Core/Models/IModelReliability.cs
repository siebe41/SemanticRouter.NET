namespace SemanticRouter;

/// <summary>
/// Whether a model should be offered for a kind of work: a route, or work that needs a capability such as tools.
/// A model that keeps failing at one kind of work can be left out of it while it still serves others.
/// </summary>
/// <remarks>
/// <see cref="PromptRouter"/> asks once per ranked candidate, so implementations must be cheap and thread-safe. It never
/// asks for a model the request named explicitly, and keeps an unreliable model when no other model could serve.
/// </remarks>
public interface IModelReliability
{
    /// <summary>Whether <paramref name="model"/> should be offered.</summary>
    /// <param name="model">The candidate.</param>
    /// <param name="route">The route the prompt matched, or <see langword="null"/>.</param>
    /// <param name="capabilities">The capabilities the request needs (<c>tools</c>, <c>vision</c>, …).</param>
    bool IsReliable(ModelDefinition model, string? route, IReadOnlyList<string> capabilities);
}
