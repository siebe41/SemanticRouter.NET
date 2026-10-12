namespace SemanticRouter;

/// <summary>
/// A semantic intent cluster: a named set of example utterances that, when matched, states how capable a
/// model must be to answer the prompt.
/// </summary>
/// <remarks>
/// The type is a mutable POCO so it can be bound directly from <c>appsettings.json</c>.
/// Treat instances as immutable once they have been handed to a classifier.
/// </remarks>
public sealed class RouteDefinition
{
    /// <summary>Unique, human-readable route name (emitted in headers and telemetry).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Minimum <see cref="ModelDefinition.Tier"/> a model needs to serve this intent. Tiers are ordinal; the
    /// suggested scale is 1 = small local model, 2 = mid-size, 3 = strong general model, 4 = frontier,
    /// 5 = frontier with extended reasoning. Default 1.
    /// </summary>
    public int Tier { get; set; } = 1;

    /// <summary>
    /// Capability tags a model must declare to serve this intent (e.g. <c>code</c>, <c>vision</c>), matched
    /// case-insensitively against <see cref="ModelDefinition.Capabilities"/>.
    /// </summary>
    public IList<string> RequiredCapabilities { get; } = new List<string>();

    /// <summary>
    /// Representative example prompts for this intent. More diverse examples produce a more robust cluster;
    /// 5–20 short utterances per route is a good starting point.
    /// </summary>
    public IList<string> Utterances { get; } = new List<string>();

    /// <summary>
    /// Minimum cosine similarity (−1..1) required to match this route. When <see langword="null"/>,
    /// <see cref="SemanticRouterOptions.DefaultSimilarityThreshold"/> is used.
    /// </summary>
    public float? SimilarityThreshold { get; set; }

    /// <summary>
    /// When <see langword="true"/>, prompts matching this route may only be served by models marked
    /// <see cref="ModelDefinition.IsLocal"/>: they never leave the machine, not even on failover. Use this for
    /// data-sensitive intents (PII, internal documents). Complexity can still raise the tier, but only among
    /// local models. Default <see langword="false"/>.
    /// </summary>
    public bool LocalOnly { get; set; }

    /// <summary>
    /// Prompts that match this route say nothing about difficulty ("go ahead", "continue"): in a conversation, they
    /// take the conversation's tier, down as well as up (<see cref="PromptRoutingRequest.ConversationFloor"/>), and
    /// <see cref="Tier"/> applies only when there is none. Default <see langword="false"/>.
    /// </summary>
    public bool FollowsConversation { get; set; }

    /// <summary>
    /// A catalog model that serves this route's plain matches first, even below <see cref="Tier"/>: one that has shown
    /// it does this work as well as the tier's usual choice, for less. Set by automatic tuning when a trial of a cheaper
    /// model goes well; <see langword="null"/> (the default) leaves the choice to the tier. Escalated, explicit and
    /// conversation-held turns are routed as usual.
    /// </summary>
    public string? PreferredModel { get; set; }

    /// <summary>Optional free-form description for documentation and diagnostics.</summary>
    public string? Description { get; set; }

    /// <inheritdoc />
    public override string ToString() => $"{Name} (tier {Tier}{(LocalOnly ? ", local only" : string.Empty)})";
}
