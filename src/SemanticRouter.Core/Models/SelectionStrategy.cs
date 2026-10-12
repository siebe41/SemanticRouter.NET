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

    /// <summary>
    /// The model with the lowest recent median time to first byte among those that meet the tier (<see cref="IModelLatency"/>),
    /// then the cheapest. A model without enough recent turns counts as typical (the median of the others); with no
    /// latency known at all, this is <see cref="CheapestCapable"/>.
    /// </summary>
    Fastest = 3,

    /// <summary>
    /// The cheapest model among those that meet the tier and are nearly as fast as the fastest (time to first byte within
    /// <see cref="ModelSelectionOptions.BalancedLatencyFactor"/> of it), then the rest by cost.
    /// </summary>
    Balanced = 4,
}
