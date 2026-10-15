using System.Globalization;
using System.Text.Json.Serialization;

namespace SemanticRouter.AspNetCore;

/// <summary>Something a person needs to decide (<see cref="RouterNotices"/>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NoticeKind>))]
public enum NoticeKind
{
    /// <summary>A subscription keeps hitting its usage limit, or a budget keeps running out.</summary>
    LimitReached,

    /// <summary>A model was left out of most of the work it does (model reliability).</summary>
    ModelUnreliable,

    /// <summary>Every request to a connector has failed for a long time.</summary>
    ConnectorDown,

    /// <summary>Automatic tuning wants changes it may not make on its own.</summary>
    TuningSuggestion,
}

/// <summary>One thing that needs a decision.</summary>
/// <param name="Key">Stable while the condition lasts, e.g. <c>down:lmstudio</c>, so it is announced once.</param>
/// <param name="Kind">What kind of decision.</param>
/// <param name="Title">One line, e.g. "lmstudio has been down for 2 hours".</param>
/// <param name="Detail">What happened and what would help.</param>
/// <param name="Link">The dashboard page to decide on, e.g. <c>#/learned</c>.</param>
/// <param name="Since">Since when, as far as the router knows.</param>
public sealed record RouterNotice(string Key, NoticeKind Kind, string Title, string Detail, string Link, DateTimeOffset Since)
{
    /// <summary>
    /// <see cref="Detail"/> without text that can come from prompts or replies (a learned utterance, a connector's
    /// error), for webhooks while <see cref="NotificationOptions.IncludeDetails"/> is off. <see langword="null"/>: none
    /// was written, and webhooks leave the detail out.
    /// </summary>
    [JsonIgnore]
    public string? PlainDetail { get; init; }
}

/// <summary>
/// The router asks for attention only when a person needs to decide something: a subscription that keeps hitting its
/// limit (or a budget that keeps running out), a model left out of most of its work, a connector down for
/// <see cref="NotificationOptions.ConnectorDownAfter"/>, or tuning changes waiting for approval. Everything else it
/// handles on its own and reports in the digest.
/// </summary>
internal sealed class RouterNotices(
    RouterRuntimeProvider provider,
    BudgetGate budgets,
    ConnectorIncidents incidents,
    ModelReliabilityTracker reliability,
    RouterTuner tuner,
    TimeProvider? time = null)
{
    /// <summary>A budget spent in this many of its last <see cref="BudgetPeriods"/> periods keeps running out.</summary>
    internal const int BudgetPeriodsSpent = 2;

    internal const int BudgetPeriods = 4;

    // The Overview asks often; reliability is recomputed from the activity log at most this often.
    private static readonly TimeSpan ReliabilityEvery = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private (DateTimeOffset At, IReadOnlyList<ModelReliability> Rows)? _reliability;

    /// <summary>What needs a decision now, oldest first.</summary>
    public IReadOnlyList<RouterNotice> Current()
    {
        RouterConfiguration configuration = provider.Current.Configuration;
        DateTimeOffset now = _time.GetUtcNow();
        var notices = new List<RouterNotice>();
        Limits(configuration, notices);
        Unreliable(configuration, now, notices);
        Down(configuration, now, notices);
        Suggestions(configuration, notices);
        return [.. notices.OrderBy(n => n.Since)];
    }

    private void Limits(RouterConfiguration configuration, List<RouterNotice> notices)
    {
        int needed = configuration.Notifications.LimitDays;
        foreach ((string connector, int days, string? detail) in incidents.LimitDays().Where(l => l.Days >= needed))
        {
            notices.Add(new RouterNotice(
                "limit:" + connector,
                NoticeKind.LimitReached,
                $"{connector} keeps hitting its usage limit",
                LimitDetail(days, detail),
                "#/models",
                _time.GetUtcNow().AddDays(-7))
            {
                PlainDetail = LimitDetail(days, null),
            });
        }

        static string LimitDetail(int days, string? latest) =>
            $"It hit its limit on {days} of the last 7 days{(latest is { Length: > 0 } ? $" (latest: {Clip(latest)})" : string.Empty)}. " +
            "While it is out, its work goes to other models. A larger plan or a second account would keep it serving.";

        UsageOptions usage = configuration.Usage;
        foreach (BudgetDefinition budget in usage.Budgets)
        {
            int spent = budgets.PeriodsSpent(budget, usage, BudgetPeriods);
            if (spent < BudgetPeriodsSpent)
            {
                continue;
            }

            BudgetStatus status = budgets.Status(usage).First(s => s.Name == budget.Name);
            string period = budget.Period.ToString().ToLowerInvariant();
            string detail = $"It was spent in {spent} of the last {BudgetPeriods} {period}s ({status.Covers}; now {status.Describe()}). " +
                "Raise it, or let cheaper models take more of this work.";
            notices.Add(new RouterNotice("budget:" + budget.Name, NoticeKind.LimitReached, $"Budget {budget.Name} keeps running out", detail, "#/usage", status.PeriodStart)
            {
                PlainDetail = detail,
            });
        }
    }

    private void Unreliable(RouterConfiguration configuration, DateTimeOffset now, List<RouterNotice> notices)
    {
        ReliabilityOptions options = configuration.Middleware.Reliability;
        if (!options.Enabled)
        {
            return;
        }

        IReadOnlyList<ModelReliability> rows;
        lock (_gate)
        {
            if (_reliability is not { } cached || now - cached.At >= ReliabilityEvery || now < cached.At)
            {
                _reliability = cached = (now, reliability.Refresh());
            }

            rows = cached.Rows;
        }

        foreach (IGrouping<string, ModelReliability> model in rows.GroupBy(r => r.Model, StringComparer.OrdinalIgnoreCase))
        {
            ModelReliability[] judged = [.. model.Where(r => r.Turns >= options.MinimumTurns || r.ExcludedUntil > now)];
            ModelReliability[] excluded = [.. judged.Where(r => r.ExcludedUntil > now)];
            if (excluded.Length == 0 || excluded.Length * 2 <= judged.Length)
            {
                continue;
            }

            string work = string.Join(", ", excluded.Select(r => r.Route is { } route ? "route " + route : "requests with tools"));

            // The reliability detail is counts ("7 of 20 turns drew feedback"), no prompt or reply text.
            string detail = $"It is left out of {excluded.Length} of the {judged.Length} kinds of work it does ({work}) until " +
                $"{excluded.Max(r => r.ExcludedUntil)!.Value.ToString("d MMM HH:mm", CultureInfo.InvariantCulture)} UTC: {Clip(excluded[0].Detail ?? string.Empty)}. " +
                "Check the model and its connector, or take it out of the catalog.";
            notices.Add(new RouterNotice(
                "unreliable:" + model.Key,
                NoticeKind.ModelUnreliable,
                $"{model.Key} is unreliable for most of its work",
                detail,
                "#/models",
                excluded.Min(r => r.ExcludedSince) ?? now)
            {
                PlainDetail = detail,
            });
        }
    }

    private void Down(RouterConfiguration configuration, DateTimeOffset now, List<RouterNotice> notices)
    {
        TimeSpan after = configuration.Notifications.ConnectorDownAfter;
        foreach ((string connector, DateTimeOffset since, string? detail) in incidents.Failing().Where(f => now - f.Since >= after))
        {
            if (!configuration.Connectors.ContainsKey(connector))
            {
                continue;
            }

            notices.Add(new RouterNotice(
                "down:" + connector,
                NoticeKind.ConnectorDown,
                $"{connector} has been down for {Duration(now - since)}",
                DownDetail(since, detail),
                "#/connectors",
                since)
            {
                PlainDetail = DownDetail(since, null),
            });
        }

        static string DownDetail(DateTimeOffset since, string? latest) =>
            $"Every request to it has failed since {since.ToString("d MMM HH:mm", CultureInfo.InvariantCulture)} UTC" +
            $"{(latest is { Length: > 0 } ? $" (latest: {Clip(latest)})" : string.Empty)}. Its models' work goes to the others. " +
            "Start the server, check its address and key, or turn its models off.";
    }

    private void Suggestions(RouterConfiguration configuration, List<RouterNotice> notices)
    {
        TuningChange[] waiting = [.. tuner.Changes().Where(c => c.State == TuningChangeState.Suggested)];
        if (waiting.Length == 0)
        {
            return;
        }

        string why = configuration.Tuning.Mode == TuningMode.Suggest
            ? "Tuning:Mode is Suggest, so it applies nothing on its own"
            : "they undo or redo a change someone turned down earlier";
        notices.Add(new RouterNotice(
            "tuning:suggestions",
            NoticeKind.TuningSuggestion,
            waiting.Length == 1 ? "Automatic tuning has a suggestion" : $"Automatic tuning has {waiting.Length} suggestions",
            Detail(c => c.Summary),
            "#/learned",
            waiting.Min(c => c.Timestamp))
        {
            PlainDetail = Detail(c => c.PlainSummary),
        });

        string Detail(Func<TuningChange, string> summary) =>
            $"{(waiting.Length == 1 ? "It" : "They")} wait for you because {why}: {Clip(string.Join("; ", waiting.Take(3).Select(summary)))}. Apply or dismiss {(waiting.Length == 1 ? "it" : "them")}.";
    }

    private static string Duration(TimeSpan span) => span.TotalHours >= 48
        ? $"{(int)span.TotalDays} days"
        : span.TotalMinutes >= 120 ? $"{(int)span.TotalHours} hours" : $"{(int)span.TotalMinutes} minutes";

    private static string Clip(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
