namespace SemanticRouter.AspNetCore;

/// <summary>
/// Where the input tokens of a period's requests went (system prompt, tools, injected blocks, history, tool results,
/// images, the new asks), what that context cost, and how often each sign of too much context came up.
/// </summary>
internal sealed record ContextSummary
{
    /// <summary>The parts, in the order the dashboard shows them.</summary>
    private static readonly (string Key, string Label, Func<ContextBreakdown, int> Tokens)[] Parts =
    [
        ("system", "System prompt", b => b.SystemTokens),
        ("tools", "Tool definitions", b => b.ToolDefinitionTokens),
        ("injected", "Injected blocks", b => b.InjectedTokens),
        ("history", "History", b => b.HistoryTokens),
        ("toolResults", "Tool results", b => b.ToolResultTokens),
        ("images", "Images", b => b.ImageTokens),
        ("prompt", "New asks", b => b.PromptTokens),
    ];

    public required DateTimeOffset Since { get; init; }

    public required DateTimeOffset Until { get; init; }

    /// <summary>Requests that were analyzed.</summary>
    public required int Requests { get; init; }

    /// <summary>Their input, estimated.</summary>
    public required long TotalTokens { get; init; }

    /// <summary>What their input cost, where the backend reported usage.</summary>
    public required decimal CostUsd { get; init; }

    /// <summary>What the input of requests on subscriptions would have cost at API prices.</summary>
    public required decimal ApiValueUsd { get; init; }

    /// <summary>Of the tool definitions sent, those for tools the conversation never called.</summary>
    public required long UnusedToolTokens { get; init; }

    /// <summary>Content sent more than once in the same request.</summary>
    public required long RepeatedTokens { get; init; }

    public required IReadOnlyList<Part> Components { get; init; }

    /// <summary>Each kind of finding that came up, most requests first.</summary>
    public required IReadOnlyList<Finding> Findings { get; init; }

    /// <summary>The largest requests, largest first.</summary>
    public required IReadOnlyList<Request> Heaviest { get; init; }

    /// <summary>One part of the input across the period.</summary>
    public sealed record Part
    {
        public required string Key { get; init; }

        public required string Label { get; init; }

        public required long Tokens { get; init; }

        public required long AverageTokens { get; init; }

        /// <summary>Its share of all input tokens (0–1).</summary>
        public required double Share { get; init; }

        /// <summary>Its share of the input cost: each request's cost split by its parts' sizes.</summary>
        public required decimal CostUsd { get; init; }

        /// <summary>Its share of the input's value at API prices, for subscriptions.</summary>
        public required decimal ApiValueUsd { get; init; }
    }

    /// <summary>A kind of finding across the period.</summary>
    public sealed record Finding
    {
        public required string Code { get; init; }

        public required string Severity { get; init; }

        public required string Title { get; init; }

        public required string Advice { get; init; }

        /// <summary>Requests it came up in.</summary>
        public required int Requests { get; init; }

        /// <summary>Their share of the analyzed requests (0–1).</summary>
        public required double Share { get; init; }

        /// <summary>The input cost of those requests.</summary>
        public required decimal CostUsd { get; init; }

        /// <summary>The newest request it came up in, to open on the Activity page.</summary>
        public required long Example { get; init; }
    }

    /// <summary>One large request.</summary>
    public sealed record Request
    {
        public required long Id { get; init; }

        public required DateTimeOffset Timestamp { get; init; }

        public string? Model { get; init; }

        public string PromptPreview { get; init; } = string.Empty;

        public required ContextBreakdown Context { get; init; }
    }

    /// <summary>Builds the summary from the period's activity; entries without a breakdown are left out.</summary>
    public static ContextSummary Build(IReadOnlyList<RoutingActivityEntry> activity, DateTimeOffset since, DateTimeOffset until, int heaviest = 10)
    {
        RoutingActivityEntry[] analyzed = [.. activity.Where(e => e.Context is { TotalTokens: > 0 })];
        long total = analyzed.Sum(e => (long)e.Context!.TotalTokens);
        var cost = new decimal[Parts.Length];
        var value = new decimal[Parts.Length];
        var tokens = new long[Parts.Length];
        foreach (RoutingActivityEntry entry in analyzed)
        {
            ContextBreakdown context = entry.Context!;
            for (int i = 0; i < Parts.Length; i++)
            {
                int part = Parts[i].Tokens(context);
                tokens[i] += part;
                decimal share = (decimal)part / context.TotalTokens;
                cost[i] += (context.InputCostUsd ?? 0) * share;
                value[i] += (context.InputApiValueUsd ?? 0) * share;
            }
        }

        return new ContextSummary
        {
            Since = since,
            Until = until,
            Requests = analyzed.Length,
            TotalTokens = total,
            CostUsd = analyzed.Sum(e => e.Context!.InputCostUsd ?? 0),
            ApiValueUsd = analyzed.Sum(e => e.Context!.InputApiValueUsd ?? 0),
            UnusedToolTokens = analyzed.Sum(e => (long)e.Context!.UnusedToolDefinitionTokens),
            RepeatedTokens = analyzed.Sum(e => (long)e.Context!.RepeatedTokens),
            Components =
            [
                .. Parts.Select((p, i) => new Part
                {
                    Key = p.Key,
                    Label = p.Label,
                    Tokens = tokens[i],
                    AverageTokens = analyzed.Length > 0 ? tokens[i] / analyzed.Length : 0,
                    Share = total > 0 ? Math.Round((double)tokens[i] / total, 4) : 0,
                    CostUsd = Math.Round(cost[i], 6),
                    ApiValueUsd = Math.Round(value[i], 6),
                }),
            ],
            Findings =
            [
                .. ContextAnalyzer.Kinds
                    .Select(kind => (Kind: kind, Entries: analyzed.Where(e => e.Context!.Findings.Contains(kind.Code)).ToArray()))
                    .Where(f => f.Entries.Length > 0)
                    .Select(f => new Finding
                    {
                        Code = f.Kind.Code,
                        Severity = f.Kind.Severity,
                        Title = f.Kind.Title,
                        Advice = f.Kind.Advice,
                        Requests = f.Entries.Length,
                        Share = Math.Round((double)f.Entries.Length / analyzed.Length, 4),
                        CostUsd = f.Entries.Sum(e => e.Context!.InputCostUsd ?? 0),
                        Example = f.Entries.Max(e => e.Id),
                    })
                    .OrderByDescending(f => f.Requests),
            ],
            Heaviest =
            [
                .. analyzed
                    .OrderByDescending(e => e.Context!.TotalTokens)
                    .ThenByDescending(e => e.Id)
                    .Take(heaviest)
                    .Select(e => new Request { Id = e.Id, Timestamp = e.Timestamp, Model = e.ServedBy ?? e.PlannedModel, PromptPreview = e.PromptPreview, Context = e.Context! }),
            ],
        };
    }
}
