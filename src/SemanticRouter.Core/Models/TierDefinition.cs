namespace SemanticRouter;

/// <summary>
/// A named rung on the capability ladder. Tiers are ordinal integers everywhere else in the router; definitions give
/// them human-readable names and descriptions for configuration tools and diagnostics.
/// </summary>
public sealed class TierDefinition
{
    /// <summary>The tier number, compared with <see cref="RouteDefinition.Tier"/> and <see cref="ModelDefinition.Tier"/>.</summary>
    public int Level { get; set; }

    /// <summary>Short name, e.g. <c>Local small</c> or <c>Frontier</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What kind of prompts and models belong here.</summary>
    public string? Description { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"{Level}: {Name}";
}
