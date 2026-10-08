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
}
