using System.Text.Json.Serialization;

namespace SemanticRouter.AspNetCore;

/// <summary>The period a budget covers. Periods start at midnight in <see cref="UsageOptions.TimeZone"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BudgetPeriod>))]
public enum BudgetPeriod
{
    /// <summary>A calendar day.</summary>
    Day = 0,

    /// <summary>A week starting on Monday.</summary>
    Week = 1,

    /// <summary>A calendar month.</summary>
    Month = 2,
}

/// <summary>
/// A spending limit for one model or one connector. Once the period's usage reaches a limit, the router stops trying
/// the model (or the connector's models) until the next period; requests fall through to the next candidate, and a
/// request that nothing else can serve gets a 503 naming the budget.
/// </summary>
public sealed class BudgetDefinition
{
    /// <summary>A name for messages and the dashboard, e.g. <c>daily-openai</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The catalog model this budget covers. Set exactly one of this, <see cref="Connector"/> or <see cref="Profile"/>.</summary>
    public string? Model { get; set; }

    /// <summary>The connector whose models (together) this budget covers.</summary>
    public string? Connector { get; set; }

    /// <summary>
    /// The routing profile whose requests (on any model) this budget covers. Once spent, requests under the profile are
    /// only tried on models that cost nothing (local models and flat-rate subscriptions).
    /// </summary>
    public string? Profile { get; set; }

    /// <summary>The period. Default <see cref="BudgetPeriod.Day"/>.</summary>
    public BudgetPeriod Period { get; set; } = BudgetPeriod.Day;

    /// <summary>Most tokens (input, output and cache) per period, if limited.</summary>
    public long? MaxTokens { get; set; }

    /// <summary>Most spend in USD per period, at the models' configured prices, if limited.</summary>
    public decimal? MaxCostUsd { get; set; }
}

/// <summary>
/// The usage limit of one connector's account, set by hand for the quota forecast (<see cref="UsageOptions.Quotas"/>).
/// A subscription does not publish its limit, so without one the forecast learns it from the times the connector ran out.
/// It only informs the forecast: the router does not stop at it (a budget does that).
/// </summary>
public sealed class UsageQuota
{
    /// <summary>
    /// Tokens (input, output and cache, as budgets count them) per period. Set this or <see cref="MaxRequests"/>, or
    /// neither to keep learning the limit while setting the <see cref="Period"/>.
    /// </summary>
    public long? MaxTokens { get; set; }

    /// <summary>Requests per period, for a plan that counts requests (premium requests, messages).</summary>
    public long? MaxRequests { get; set; }

    /// <summary>
    /// How long the limit lasts before it resets, e.g. <c>7.00:00:00</c> for a weekly limit or <c>05:00:00</c>; at least
    /// an hour. Without it the forecast learns it from the resets it saw, when they repeat at a fixed interval.
    /// </summary>
    public TimeSpan? Period { get; set; }

    /// <summary>
    /// A moment the limit reset (e.g. last Tuesday 9:00 with its offset); later resets are whole <see cref="Period"/>s
    /// after it. Without it, the latest reset the router saw.
    /// </summary>
    public DateTimeOffset? PeriodStart { get; set; }
}

/// <summary>Usage accounting and budgets. Bound from <c>SemanticRouter:Usage</c>.</summary>
public sealed class UsageOptions
{
    /// <summary>
    /// A catalog model (it may be disabled) whose prices are the yardstick for "saved by routing": what each request
    /// would have cost there, minus what it cost. Leave empty to skip the estimate.
    /// </summary>
    public string? ReferenceModel { get; set; }

    /// <summary>Warn (log and dashboard) when a budget reaches this share of its limit. Default 80.</summary>
    public int WarnAtPercent { get; set; } = 80;

    /// <summary>The time zone budget periods follow (IANA or Windows id). Default: this machine's.</summary>
    public string? TimeZone { get; set; }

    /// <summary>Spending limits.</summary>
    public IList<BudgetDefinition> Budgets { get; } = new List<BudgetDefinition>();

    /// <summary>
    /// Usage limits set by hand per connector name, for the quota forecast (<c>GET api/forecast</c>). Empty by default:
    /// the forecast learns each subscription's limit from the times it ran out.
    /// </summary>
    public IDictionary<string, UsageQuota> Quotas { get; } = new Dictionary<string, UsageQuota>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Context cost analysis: where each request's input tokens go, and what to trim.</summary>
    public ContextCostOptions Context { get; set; } = new();
}
