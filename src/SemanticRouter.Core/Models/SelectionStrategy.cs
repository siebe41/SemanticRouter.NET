using System.Text.Json.Serialization;

namespace SemanticRouter;

/// <summary>How eligible models are ordered once a prompt's requirements are known.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SelectionStrategy>))]
public enum SelectionStrategy
{
    /// <summary>
    /// The cheapest model that meets the required tier. Free local models come first: one that is loaded now
    /// (<see cref="IModelResidency"/>) before one that would have to load, then the most capable. Then free models that
    /// are not local, such as subscription CLIs, whose quota each request uses: the <em>lowest</em> sufficient tier
    /// first, keeping premium quota for prompts that need it. Then paid models, cheapest first. Remaining ties go to
    /// <see cref="ModelDefinition.Priority"/>. Default.
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
