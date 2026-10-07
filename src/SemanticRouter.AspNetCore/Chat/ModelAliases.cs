namespace SemanticRouter.AspNetCore;

/// <summary>
/// Model names clients can send to let the router choose: <c>auto</c> (configured strategy), <c>auto:cheapest</c>,
/// <c>auto:best</c> and <c>auto:priority</c>. Any other name that matches a catalog model forces that model; unknown
/// names are treated as <c>auto</c>.
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

    /// <summary>All aliases with descriptions, for <c>/v1/models</c>.</summary>
    public static IReadOnlyList<(string Id, string Description)> All { get; } =
    [
        (Auto, "Route automatically using the configured strategy."),
        (Cheapest, "Cheapest model that meets the prompt's required tier."),
        (Best, "Most capable available model."),
        (Priority, "Your configured model priority order."),
    ];

    /// <summary>Resolves a client-supplied model name into a strategy override and/or an explicit model.</summary>
    public static (SelectionStrategy? Strategy, string? RequestedModel) Resolve(string? model) => model?.Trim().ToLowerInvariant() switch
    {
        null or "" or Auto => (null, null),
        Cheapest => (SelectionStrategy.CheapestCapable, null),
        Best => (SelectionStrategy.MostCapable, null),
        Priority => (SelectionStrategy.Priority, null),
        _ => (null, model!.Trim()),
    };
}
