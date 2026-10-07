using System.Text.Json.Serialization;

namespace SemanticRouter;

/// <summary>How eligible models are ordered once a prompt's requirements are known.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SelectionStrategy>))]
public enum SelectionStrategy
{
    /// <summary>
    /// The cheapest model that meets the required tier. Ties go to the <em>lowest</em> sufficient tier (keeping
    /// premium subscription quota for prompts that need it), then to <see cref="ModelDefinition.Priority"/>. Default.
    /// </summary>
    CheapestCapable = 0,

    /// <summary>The most capable model available, then the cheapest among equals.</summary>
    MostCapable = 1,

    /// <summary>Your <see cref="ModelDefinition.Priority"/> order among models that meet the tier, then the cheapest.</summary>
    Priority = 2,
}
