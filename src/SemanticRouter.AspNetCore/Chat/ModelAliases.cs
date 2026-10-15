namespace SemanticRouter.AspNetCore;

/// <summary>
/// Model names clients can send to let the router choose: <c>auto</c> (configured strategy), <c>auto:cheapest</c>,
/// <c>auto:best</c>, <c>auto:priority</c>, <c>auto:fastest</c> and <c>auto:balanced</c>, and the names for clients that
/// only offer models named after a vendor (<see cref="RouterNames"/>). Any other name that matches a catalog model forces
/// that model; unknown names are treated as <c>auto</c>.
/// </summary>
public static class ModelAliases
{
    /// <summary>Route with the configured strategy.</summary>
    public const string Auto = "auto";

    /// <summary>Route with <see cref="SelectionStrategy.CheapestCapable"/>.</summary>
    public const string Cheapest = "auto:cheapest";

    /// <summary>Route with <see cref="SelectionStrategy.MostCapable"/>.</summary>
    public const string Best = "auto:best";

    /// <summary>Route with <see cref="SelectionStrategy.Priority"/>.</summary>
    public const string Priority = "auto:priority";

    /// <summary>Route with <see cref="SelectionStrategy.Fastest"/>.</summary>
    public const string Fastest = "auto:fastest";

    /// <summary>Route with <see cref="SelectionStrategy.Balanced"/>.</summary>
    public const string Balanced = "auto:balanced";

    /// <summary>Route with the configured strategy: the name to give Claude Code (<c>claude --model semantic-router</c>).</summary>
    public const string Router = "semantic-router";

    /// <summary>
    /// <see cref="Router"/> for model pickers that only list names containing <c>claude</c> (Claude Code's gateway model
    /// discovery).
    /// </summary>
    public const string ClaudeRouter = "claude-semantic-router";

    /// <summary><see cref="Cheapest"/> under a name Claude Code's model picker lists.</summary>
    public const string ClaudeCheapest = "claude-semantic-router-cheapest";

    /// <summary><see cref="Best"/> under a name Claude Code's model picker lists.</summary>
    public const string ClaudeBest = "claude-semantic-router-best";

    /// <summary>All aliases with descriptions, for <c>/v1/models</c>.</summary>
    public static IReadOnlyList<(string Id, string Description)> All { get; } =
    [
        (Auto, "Route automatically using the configured strategy."),
        (Cheapest, "Cheapest model that meets the prompt's required tier."),
        (Best, "Most capable available model."),
        (Priority, "Your configured model priority order."),
        (Fastest, "Model that starts answering soonest lately, among those that meet the prompt's tier."),
        (Balanced, "Cheapest model that is nearly as fast as the fastest."),
    ];

    /// <summary>
    /// The router's own names, with the names and descriptions a model picker shows, for <c>/v1/models</c>. They route
    /// like <c>auto</c>, <c>auto:cheapest</c> and <c>auto:best</c>, and model aliases never remap them.
    /// </summary>
    public static IReadOnlyList<(string Id, string DisplayName, string Description)> RouterNames { get; } =
    [
        (Router, "auto (SemanticRouter.NET)", "The router picks a model for each turn"),
        (ClaudeRouter, "auto (SemanticRouter.NET)", "The router picks a model for each turn"),
        (ClaudeCheapest, "auto:cheapest (SemanticRouter.NET)", "The router picks the cheapest model that can handle each turn"),
        (ClaudeBest, "auto:best (SemanticRouter.NET)", "The router picks the most capable model for each turn"),
    ];

    /// <summary>Whether <paramref name="model"/> is one of <see cref="RouterNames"/>.</summary>
    public static bool IsRouterName(string? model) =>
        model is not null && RouterNames.Any(n => string.Equals(n.Id, model.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Resolves a client-supplied model name into a strategy override and/or an explicit model.</summary>
    public static (SelectionStrategy? Strategy, string? RequestedModel) Resolve(string? model) => model?.Trim().ToLowerInvariant() switch
    {
        null or "" or Auto or Router or ClaudeRouter => (null, null),
        Cheapest or ClaudeCheapest => (SelectionStrategy.CheapestCapable, null),
        Best or ClaudeBest => (SelectionStrategy.MostCapable, null),
        Priority => (SelectionStrategy.Priority, null),
        Fastest => (SelectionStrategy.Fastest, null),
        Balanced => (SelectionStrategy.Balanced, null),
        _ => (null, model!.Trim()),
    };
}
