using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace SemanticRouter.AspNetCore;

/// <summary>Where a budget stands in its current period.</summary>
public sealed record BudgetStatus
{
    /// <summary>The budget's name (<see cref="BudgetDefinition.Name"/>).</summary>
    public required string Name { get; init; }

    /// <summary>What it covers, e.g. <c>model gpt-4o</c> or <c>connector openai</c>.</summary>
    public required string Covers { get; init; }

    /// <summary>The budget period.</summary>
    public required BudgetPeriod Period { get; init; }

    /// <summary>Start of the current period.</summary>
    public required DateTimeOffset PeriodStart { get; init; }

    /// <summary>End of the current period.</summary>
    public required DateTimeOffset PeriodEnd { get; init; }

    /// <summary>Tokens used in the period.</summary>
    public required long TokensUsed { get; init; }

    /// <summary>The token limit, if any.</summary>
    public long? MaxTokens { get; init; }

    /// <summary>Spend in the period, at the models' current prices.</summary>
    public required decimal CostUsedUsd { get; init; }

    /// <summary>The spend limit, if any.</summary>
    public decimal? MaxCostUsd { get; init; }

    /// <summary>How much of the budget is used, by the nearer of the two limits, rounded down.</summary>
    public required int Percent { get; init; }

    /// <summary>Whether a limit is reached, so the budget's models are not tried until the period ends.</summary>
    public required bool Exceeded { get; init; }

    /// <summary>Whether the budget is past <see cref="UsageOptions.WarnAtPercent"/> but not exceeded.</summary>
    public required bool Warning { get; init; }

    /// <summary>"12,400 of 10,000 tokens" / "$4.10 of $5.00", for messages.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (MaxTokens is { } maxTokens)
        {
            parts.Add(string.Format(CultureInfo.InvariantCulture, "{0:N0} of {1:N0} tokens", TokensUsed, maxTokens));
        }

        if (MaxCostUsd is { } maxCost)
        {
            parts.Add(string.Format(CultureInfo.InvariantCulture, "${0:0.00} of ${1:0.00}", CostUsedUsd, maxCost));
        }

        return string.Join(", ", parts) + " this " + Period.ToString().ToLowerInvariant();
    }
}

/// <summary>
/// Decides whether a model may be tried under the configured budgets, and reports where each budget stands. Spend is at
/// the catalog's current prices when <paramref name="prices"/> gives the configuration, as on the Usage page.
/// </summary>
internal sealed partial class BudgetGate(IUsageLedger ledger, TimeProvider time, ILogger<BudgetGate> logger, Func<RouterConfiguration?>? prices = null)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _warned = new(StringComparer.OrdinalIgnoreCase);

    public static TimeZoneInfo ZoneOf(UsageOptions usage) =>
        usage.TimeZone is { Length: > 0 } id && TimeZoneInfo.TryFindSystemTimeZoneById(id, out TimeZoneInfo? zone) ? zone : TimeZoneInfo.Local;

    /// <summary>The period containing <paramref name="now"/>, in <paramref name="zone"/>.</summary>
    public static (DateTimeOffset Start, DateTimeOffset End) PeriodOf(BudgetPeriod period, DateTimeOffset now, TimeZoneInfo zone)
    {
        DateTime local = TimeZoneInfo.ConvertTime(now, zone).DateTime.Date;
        DateTime start = period switch
        {
            BudgetPeriod.Week => local.AddDays(-(((int)local.DayOfWeek + 6) % 7)),
            BudgetPeriod.Month => new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified),
            _ => local,
        };
        DateTime end = period switch
        {
            BudgetPeriod.Week => start.AddDays(7),
            BudgetPeriod.Month => start.AddMonths(1),
            _ => start.AddDays(1),
        };
        return (ToOffset(start, zone), ToOffset(end, zone));
    }

    /// <summary>Every budget's current state.</summary>
    public IReadOnlyList<BudgetStatus> Status(UsageOptions usage) =>
        usage.Budgets.Select(b => StatusOf(b, usage)).ToArray();

    /// <summary>The first budget covering <paramref name="model"/> (or the request's <paramref name="profile"/>) that is spent, or <see langword="null"/>.</summary>
    public BudgetStatus? Blocking(ModelDefinition model, UsageOptions usage, string? profile = null)
    {
        foreach (BudgetDefinition budget in usage.Budgets)
        {
            if (Covers(budget, model, profile) && StatusOf(budget, usage) is { Exceeded: true } spent)
            {
                return spent;
            }
        }

        return null;
    }

    /// <summary>In how many of the budget's last <paramref name="periods"/> periods (the current one included) it was spent.</summary>
    public int PeriodsSpent(BudgetDefinition budget, UsageOptions usage, int periods)
    {
        TimeZoneInfo zone = ZoneOf(usage);
        DateTimeOffset at = time.GetUtcNow();
        int spent = 0;
        for (int i = 0; i < periods; i++)
        {
            (DateTimeOffset start, DateTimeOffset end) = PeriodOf(budget.Period, at, zone);
            spent += Exceeded(budget, Spent(budget, start, end)) ? 1 : 0;
            at = start.AddTicks(-1);
        }

        return spent;
    }

    private static bool Exceeded(BudgetDefinition budget, UsageTotals spent) =>
        (budget.MaxTokens is { } t && spent.TotalTokens >= t) || (budget.MaxCostUsd is { } c && spent.CostUsd >= c);

    public static bool Covers(BudgetDefinition budget, ModelDefinition model, string? profile = null) =>
        (budget.Model is { Length: > 0 } name && string.Equals(name, model.Name, StringComparison.OrdinalIgnoreCase))
        || (budget.Connector is { Length: > 0 } connector && string.Equals(connector, model.Connector, StringComparison.OrdinalIgnoreCase))

        // A spent profile budget stops the models that cost money; free ones (local, subscriptions) keep serving it.
        || (budget.Profile is { Length: > 0 } covered && string.Equals(covered, profile, StringComparison.OrdinalIgnoreCase)
            && (model.InputCostPerMillionTokens > 0 || model.OutputCostPerMillionTokens > 0));

    private BudgetStatus StatusOf(BudgetDefinition budget, UsageOptions usage)
    {
        (DateTimeOffset start, DateTimeOffset end) = PeriodOf(budget.Period, time.GetUtcNow(), ZoneOf(usage));
        UsageTotals spent = Spent(budget, start, end);
        double tokenShare = budget.MaxTokens is > 0 and { } maxTokens ? (double)spent.TotalTokens / maxTokens : 0;
        double costShare = budget.MaxCostUsd is > 0 and { } maxCost ? (double)(spent.CostUsd / maxCost) : 0;
        bool exceeded = Exceeded(budget, spent);
        int percent = (int)Math.Floor(Math.Max(tokenShare, costShare) * 100);
        bool warning = !exceeded && percent >= usage.WarnAtPercent;
        var status = new BudgetStatus
        {
            Name = budget.Name,
            Covers = budget.Model is { Length: > 0 } m ? $"model {m}" : budget.Connector is { Length: > 0 } c ? $"connector {c}" : $"profile {budget.Profile}",
            Period = budget.Period,
            PeriodStart = start,
            PeriodEnd = end,
            TokensUsed = spent.TotalTokens,
            MaxTokens = budget.MaxTokens,
            CostUsedUsd = spent.CostUsd,
            MaxCostUsd = budget.MaxCostUsd,
            Percent = percent,
            Exceeded = exceeded,
            Warning = warning,
        };

        string key = budget.Name + (exceeded ? ":spent" : ":warn");
        if ((warning || exceeded) && (!_warned.TryGetValue(key, out DateTimeOffset warnedFor) || warnedFor != start))
        {
            // Once per budget and period, so a busy router does not repeat it on every request.
            _warned[key] = start;
            if (exceeded)
            {
                LogSpent(logger, budget.Name, status.Covers, status.Describe());
            }
            else
            {
                LogWarning(logger, budget.Name, status.Covers, percent, status.Describe());
            }
        }

        return status;
    }

    private UsageTotals Spent(BudgetDefinition budget, DateTimeOffset start, DateTimeOffset end) =>
        ledger.TotalAtCurrentPrices(prices?.Invoke(), start, end, NullIfEmpty(budget.Model), NullIfEmpty(budget.Connector), NullIfEmpty(budget.Profile));

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static DateTimeOffset ToOffset(DateTime local, TimeZoneInfo zone) =>
        new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Budget {Budget} ({Covers}) is at {Percent}%: {Usage}.")]
    private static partial void LogWarning(ILogger logger, string budget, string covers, int percent, string usage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Budget {Budget} ({Covers}) is spent ({Usage}); it is not tried again until the period ends.")]
    private static partial void LogSpent(ILogger logger, string budget, string covers, string usage);
}
