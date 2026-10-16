using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// The digest (<c>GET api/digest</c>): what the router did over a period (by default the last 7 days, like the Usage
/// page) and what needs a decision. The Overview and the desktop app's menu show it.
/// </summary>
internal static class DigestApi
{
    private static readonly RoutingReason[] Escalations = [RoutingReason.ComplexityEscalation, RoutingReason.AmbiguousMatchEscalation, RoutingReason.RetryEscalation];

    public static void Map(RouteGroupBuilder api, JsonSerializerOptions json) =>
        api.MapGet("/digest", (HttpContext context, RouterRuntimeProvider provider, IUsageLedger ledger, RoutingActivityLog activity, RoutingFeedbackLog feedback, RouterTuner tuner, RouterNotices notices, string? window) =>
            {
                DigestResponse digest = Build(provider.Current.Configuration, ledger, activity, feedback, tuner, notices, window, DateTimeOffset.UtcNow);

                // The metrics role gets what webhooks get without IncludeDetails: no learned utterance, no connector's error.
                return Results.Json(DashboardAccess.RoleOf(context) == RouterDashboardRole.Metrics ? WebhookNotifier.WithoutDetails(digest) : digest, json);
            })
            .WithMetadata(DashboardMetricsMetadata.Instance);

    /// <summary>The digest for <paramref name="window"/> (as on the Usage page) at <paramref name="now"/>.</summary>
    internal static DigestResponse Build(
        RouterConfiguration configuration, IUsageLedger ledger, RoutingActivityLog activity, RoutingFeedbackLog feedback, RouterTuner tuner, RouterNotices notices,
        string? window, DateTimeOffset now)
    {
        TimeZoneInfo zone = BudgetGate.ZoneOf(configuration.Usage);
        (DateTimeOffset since, DateTimeOffset until) = RouterDashboardEndpoints.UsageWindow(window, zone, now);
        UsageTotals totals = ledger.Total(since, until);
        long local = ledger.Summarize(since, until, UsageGrouping.Location, zone).FirstOrDefault(g => g.Key == "local")?.Totals.Requests ?? 0;
        RoutingActivityEntry[] decisions = [.. activity.Between(since, until)];
        IReadOnlyList<RoutingFeedback> signals = feedback.Between(since, until);
        DigestChange[] learned = [.. tuner.Changes()
            .Where(c => c.Timestamp >= since && c.Timestamp < until && c.State is TuningChangeState.Applied or TuningChangeState.Undone)
            .Select(c => new DigestChange(c.Id, c.Timestamp, c.Summary, c.State, c.Version) { PlainSummary = c.PlainSummary })];

        return new DigestResponse(
            since,
            until,
            decisions.Length,
            totals.Requests,
            local,
            totals.Requests > 0 ? Math.Round((double)local / totals.Requests, 4) : 0,
            totals.CostUsd,
            totals.ReferenceCostUsd,
            totals.SavedUsd,
            configuration.Usage.ReferenceModel,
            decisions.Count(e => Escalations.Contains(e.Reason)),
            decisions.Count(e => e.Reason == RoutingReason.ConversationFloor),
            decisions.Count(e => e.Degraded),
            decisions.Count(e => e.Outcome == "failed"),
            signals.Count,
            signals.GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => g.Count()),
            learned,
            tuner.Changes().Count(c => c.State == TuningChangeState.Suggested),
            notices.Current(),
            configuration.Notifications.Enabled,
            totals.SubscriptionValueUsd);
    }
}

/// <summary>What the router did over a period, and what needs a decision.</summary>
/// <param name="Since">Start of the period (as on the Usage page for the same window).</param>
/// <param name="Until">End of the period.</param>
/// <param name="Prompts">Routing decisions made.</param>
/// <param name="Served">Requests served by a model (the Usage page's request count), the judge's included.</param>
/// <param name="Local">Of those, served by local models.</param>
/// <param name="LocalShare">Local / served.</param>
/// <param name="CostUsd">Cost at the models' prices.</param>
/// <param name="ReferenceCostUsd">Cost had everything gone to the reference model.</param>
/// <param name="SavedUsd">The difference.</param>
/// <param name="ReferenceModel">The reference model, when configured.</param>
/// <param name="Escalated">Decisions raised a tier for complexity, an ambiguous match or a retry.</param>
/// <param name="HeldByConversation">Decisions the conversation's floor raised.</param>
/// <param name="Degraded">Requests served below the tier they needed.</param>
/// <param name="Failed">Requests no model could serve.</param>
/// <param name="Signals">Feedback received (signs a decision was not good enough).</param>
/// <param name="SignalsByKind">Feedback per kind.</param>
/// <param name="Learned">Changes automatic tuning made in the period, each undoable on the Router learned page.</param>
/// <param name="Suggestions">Tuning suggestions waiting.</param>
/// <param name="Notices">What needs a decision now.</param>
/// <param name="Notify">Whether notifications are on (<see cref="NotificationOptions.Enabled"/>).</param>
/// <param name="SubscriptionValueUsd">What the subscriptions' requests would have cost at API prices (not spent).</param>
internal sealed record DigestResponse(
    DateTimeOffset Since,
    DateTimeOffset Until,
    int Prompts,
    long Served,
    long Local,
    double LocalShare,
    decimal CostUsd,
    decimal ReferenceCostUsd,
    decimal SavedUsd,
    string? ReferenceModel,
    int Escalated,
    int HeldByConversation,
    int Degraded,
    int Failed,
    int Signals,
    IReadOnlyDictionary<FeedbackKind, int> SignalsByKind,
    IReadOnlyList<DigestChange> Learned,
    int Suggestions,
    IReadOnlyList<RouterNotice> Notices,
    bool Notify,
    decimal SubscriptionValueUsd = 0m);

/// <summary>A change automatic tuning made: its id (for undo), when, what, and the configuration version it became.</summary>
internal sealed record DigestChange(long Id, DateTimeOffset Timestamp, string Summary, TuningChangeState State, long? Version)
{
    /// <summary>The summary without a learned utterance's text (<see cref="TuningChange.PlainSummary"/>), for webhooks.</summary>
    [JsonIgnore]
    public string? PlainSummary { get; init; }
}
