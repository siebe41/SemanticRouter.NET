namespace SemanticRouter.AspNetCore;

/// <summary>Feedback per route and per model over a period, against the decisions the router made there.</summary>
public sealed record FeedbackSummary
{
    /// <summary>Start of the period.</summary>
    public required DateTimeOffset Since { get; init; }

    /// <summary>End of the period.</summary>
    public required DateTimeOffset Until { get; init; }

    /// <summary>Decisions the router made itself (not tool-result turns or explicit choices).</summary>
    public required int Decisions { get; init; }

    /// <summary>Decisions that received any feedback.</summary>
    public required int Feedback { get; init; }

    /// <summary>Feedback items of each kind.</summary>
    public required IReadOnlyDictionary<FeedbackKind, int> ByKind { get; init; }

    /// <summary>Per route, highest feedback rate first.</summary>
    public required IReadOnlyList<FeedbackSummary.Group> ByRoute { get; init; }

    /// <summary>Per model that answered, highest feedback rate first.</summary>
    public required IReadOnlyList<FeedbackSummary.Group> ByModel { get; init; }

    /// <summary>One route or model: its decisions, how many got feedback, and of which kinds.</summary>
    public sealed record Group
    {
        /// <summary>The route or model.</summary>
        public required string Key { get; init; }

        /// <summary>Decisions the router made there.</summary>
        public required int Decisions { get; init; }

        /// <summary>Of those, how many received feedback.</summary>
        public required int WithFeedback { get; init; }

        /// <summary>Share of its decisions with feedback (0–1).</summary>
        public required double Rate { get; init; }

        /// <summary>Feedback items of each kind.</summary>
        public required IReadOnlyDictionary<FeedbackKind, int> ByKind { get; init; }
    }

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
                    return new Group
                    {
                        Key = g.Key,
                        Decisions = g.Count(),
                        WithFeedback = with,
                        Rate = g.Any() ? Math.Round((double)with / g.Count(), 4) : 0,
                        ByKind = kinds,
                    };
                })
                .OrderByDescending(g => g.Rate)
                .ThenByDescending(g => g.Decisions),
        ];

        return new FeedbackSummary
        {
            Since = since,
            Until = until,
            Decisions = decisions.Length,
            Feedback = decisions.Count(e => judged.ContainsKey(e.Id)),
            ByKind = feedback.GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => g.Count()),
            ByRoute = GroupBy(e => e.Route, f => f.Route),
            ByModel = GroupBy(e => e.ServedBy ?? e.PlannedModel, f => f.Model),
        };
    }
}
