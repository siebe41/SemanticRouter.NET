namespace SemanticRouter.AspNetCore;

/// <summary>Feedback per route and per model over a period, against the decisions the router made there.</summary>
/// <param name="Since">Start of the period.</param>
/// <param name="Until">End of the period.</param>
/// <param name="Decisions">Decisions the router made itself (not tool-result turns or explicit choices).</param>
/// <param name="Feedback">Decisions that received any feedback.</param>
/// <param name="ByKind">Feedback items of each kind.</param>
/// <param name="ByRoute">Per route, highest feedback rate first.</param>
/// <param name="ByModel">Per model that answered, highest feedback rate first.</param>
public sealed record FeedbackSummary(
    DateTimeOffset Since,
    DateTimeOffset Until,
    int Decisions,
    int Feedback,
    IReadOnlyDictionary<FeedbackKind, int> ByKind,
    IReadOnlyList<FeedbackSummary.Group> ByRoute,
    IReadOnlyList<FeedbackSummary.Group> ByModel)
{
    /// <summary>One route or model: its decisions, how many got feedback, and of which kinds.</summary>
    /// <param name="Key">The route or model.</param>
    /// <param name="Decisions">Decisions the router made there.</param>
    /// <param name="WithFeedback">Of those, how many received feedback.</param>
    /// <param name="Rate">Share of its decisions with feedback (0–1).</param>
    /// <param name="ByKind">Feedback items of each kind.</param>
    public sealed record Group(string Key, int Decisions, int WithFeedback, double Rate, IReadOnlyDictionary<FeedbackKind, int> ByKind);

    /// <summary>Builds the summary from the period's activity and feedback.</summary>
    public static FeedbackSummary Build(IReadOnlyList<RoutingActivityEntry> activity, IReadOnlyList<RoutingFeedback> feedback, DateTimeOffset since, DateTimeOffset until)
    {
        RoutingActivityEntry[] decisions = [.. activity.Where(e => !e.ToolContinuation
            && e.Reason is not (RoutingReason.ModelOverride or RoutingReason.ExplicitCallout or RoutingReason.ConversationAffinity))];
        var judged = feedback.GroupBy(f => f.EntryId).ToDictionary(g => g.Key, g => g.Select(f => f.Kind).Distinct().ToArray());

        IReadOnlyList<Group> GroupBy(Func<RoutingActivityEntry, string?> key, Func<RoutingFeedback, string?> feedbackKey) =>
        [
            .. decisions
                .GroupBy(e => key(e) ?? "(none)")
                .Select(g =>
                {
                    int with = g.Count(e => judged.ContainsKey(e.Id));
                    Dictionary<FeedbackKind, int> kinds = feedback
                        .Where(f => (feedbackKey(f) ?? "(none)") == g.Key)
                        .GroupBy(f => f.Kind)
                        .ToDictionary(k => k.Key, k => k.Count());
                    return new Group(g.Key, g.Count(), with, g.Any() ? Math.Round((double)with / g.Count(), 4) : 0, kinds);
                })
                .OrderByDescending(g => g.Rate)
                .ThenByDescending(g => g.Decisions),
        ];

        return new FeedbackSummary(
            since,
            until,
            decisions.Length,
            decisions.Count(e => judged.ContainsKey(e.Id)),
            feedback.GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => g.Count()),
            GroupBy(e => e.Route, f => f.Route),
            GroupBy(e => e.ServedBy ?? e.PlannedModel, f => f.Model));
    }
}
