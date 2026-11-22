using Microsoft.Extensions.Logging;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Mcp;

/// <summary>
/// Set by <c>AddRouterMcpServer</c>, so <c>MapRouterMcp</c> can tell it ran; holds the access check and audit log the
/// mapped endpoint uses, for the tools to record their actions in.
/// </summary>
internal sealed class RouterMcpServerMarker
{
    private DashboardGate? _gate;

    /// <summary>The mapped endpoint's access check and audit log; the first mapped wins.</summary>
    public DashboardGate? Gate
    {
        get => Volatile.Read(ref _gate);
        set => Interlocked.CompareExchange(ref _gate, value, null);
    }
}

/// <summary>The <c>classify</c> tool's answer.</summary>
internal sealed record ClassifyResult
{
    /// <summary>What <c>POST api/classify</c> answers for the same prompt, model and profile.</summary>
    public required PromptClassificationResponse Classification { get; init; }

    /// <summary>The decision, a field per line.</summary>
    public required string Decision { get; init; }
}

/// <summary>The <c>explain_request</c> tool's answer: what the Activity page shows for one request.</summary>
internal sealed record ExplainResult
{
    /// <summary>The activity entry, as <c>GET api/activity</c> returns it.</summary>
    public required RoutingActivityEntry Entry { get; init; }

    /// <summary>The decision line the dashboard's Report form fills in (never the prompt, the project or error details).</summary>
    public required string Decision { get; init; }

    /// <summary>Why a latency-aware strategy chose the model, when it did.</summary>
    public string? Selection { get; init; }

    /// <summary>The learned change that routed this request differently, when there is one.</summary>
    public string? Learned { get; init; }

    /// <summary>The feedback the request drew (retried, corrected, …).</summary>
    public required IReadOnlyList<FeedbackKind> Feedback { get; init; }
}

/// <summary>The <c>usage</c> tool's answer: <c>GET api/usage</c> for every grouping at once.</summary>
internal sealed record UsageResult
{
    public required DateTimeOffset Since { get; init; }

    public required DateTimeOffset Until { get; init; }

    /// <summary>The model "saved by routing" is measured against.</summary>
    public string? ReferenceModel { get; init; }

    public required UsageTotals Totals { get; init; }

    public required IReadOnlyList<UsageGroup> ByModel { get; init; }

    public required IReadOnlyList<UsageGroup> ByConnector { get; init; }

    public required IReadOnlyList<UsageGroup> ByRoute { get; init; }

    public required IReadOnlyList<UsageGroup> ByClient { get; init; }

    public required IReadOnlyList<BudgetStatus> Budgets { get; init; }

    /// <summary>When each subscription or budget runs out at the current pace (#374); <see langword="null"/> until then.</summary>
    public object? Forecast { get; init; }
}

/// <summary>The <c>health</c> tool's answer.</summary>
internal sealed record HealthResult
{
    /// <summary>The running configuration's version.</summary>
    public required int Version { get; init; }

    /// <summary>Whether the route index is built.</summary>
    public required bool WarmedUp { get; init; }

    /// <summary>Each connector, as the dashboard's Endpoints card shows it.</summary>
    public required IReadOnlyList<EndpointStatus> Endpoints { get; init; }

    /// <summary>The models cooling down, soonest available first.</summary>
    public required IReadOnlyList<ModelCooldown> CoolingDown { get; init; }
}

/// <summary>A model that is not tried until <see cref="Until"/>.</summary>
internal sealed record ModelCooldown
{
    public required string Model { get; init; }

    public required string Connector { get; init; }

    /// <summary>When it is tried again: a provider's usage-limit reset when it named one, otherwise the cooldown's end.</summary>
    public required DateTimeOffset Until { get; init; }
}

/// <summary>The <c>propose_tuning</c> tool's answer.</summary>
internal sealed record ProposalResult
{
    /// <summary>The suggestion recorded.</summary>
    public required ProposedChange Proposal { get; init; }

    /// <summary>What the change would do to recent requests.</summary>
    public required ReplaySummary Replay { get; init; }

    /// <summary>What happens next.</summary>
    public required string Note { get; init; }
}

/// <summary>A suggestion on the Router learned page.</summary>
internal sealed record ProposedChange
{
    public required long Id { get; init; }

    public required TuningChangeKind Kind { get; init; }

    public string? Route { get; init; }

    public required string Summary { get; init; }

    public required string Why { get; init; }

    public required TuningChangeState State { get; init; }
}

/// <summary>A what-if replay's report without the prompt previews of the requests it moves (the What-if page shows them).</summary>
internal sealed record ReplaySummary
{
    public required DateTimeOffset Since { get; init; }

    public required DateTimeOffset Until { get; init; }

    public required ReplayCounts Requests { get; init; }

    public required int Moved { get; init; }

    /// <summary>The first moves, by activity id: before and after.</summary>
    public required IReadOnlyList<MovedRequest> Moves { get; init; }

    public required IReadOnlyList<ReplayTierCount> Tiers { get; init; }

    public required IReadOnlyList<ReplayConnectorCount> Connectors { get; init; }

    public required ReplayCost Cost { get; init; }

    /// <summary>The evaluation set: labelled prompts routed to their tier before and after.</summary>
    public LabelledSummary? Evaluation { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];

    public static ReplaySummary From(ReplayReport report, int moves) => new()
    {
        Since = report.Since,
        Until = report.Until,
        Requests = report.Requests,
        Moved = report.Moved,
        Moves = [.. report.Moves.Take(moves).Select(m => new MovedRequest { Id = m.Id, At = m.At, Before = m.Before, After = m.After })],
        Tiers = report.Tiers,
        Connectors = report.Connectors,
        Cost = report.Cost,
        Evaluation = report.Evaluation is { } e ? new LabelledSummary { Cases = e.Cases, CorrectBefore = e.CorrectBefore, CorrectAfter = e.CorrectAfter, Moved = e.Moved } : null,
        Notes = report.Notes,
    };
}

/// <summary>A request the change would route differently.</summary>
internal sealed record MovedRequest
{
    /// <summary>Its activity id, for <c>explain_request</c>.</summary>
    public long? Id { get; init; }

    public DateTimeOffset? At { get; init; }

    public required ReplayDecision Before { get; init; }

    public required ReplayDecision After { get; init; }
}

/// <summary>The evaluation set through both sides, counts only.</summary>
internal sealed record LabelledSummary
{
    public required int Cases { get; init; }

    public required int CorrectBefore { get; init; }

    public required int CorrectAfter { get; init; }

    public required int Moved { get; init; }
}

internal static partial class McpLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Router MCP server at {Path} (proposals {Proposals}). Callers sign in as on the dashboard.")]
    private static partial void ServingCore(ILogger logger, string path, string proposals);

    public static void Serving(ILogger logger, string path, bool proposals) => ServingCore(logger, path, proposals ? "on" : "off");
}
