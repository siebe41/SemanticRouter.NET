using System.Text.Json.Serialization;

namespace SemanticRouter.AspNetCore;

/// <summary>What one served request used.</summary>
/// <param name="Timestamp">When it finished.</param>
/// <param name="Model">The catalog model that served it.</param>
/// <param name="Connector">Its connector.</param>
/// <param name="Route">The matched route, if any.</param>
/// <param name="Client">The gateway key name, if any.</param>
/// <param name="IsLocal">Whether the model is local.</param>
/// <param name="InputTokens">Input tokens, excluding cached ones.</param>
/// <param name="OutputTokens">Output tokens.</param>
/// <param name="CacheReadTokens">Input tokens read from the prompt cache.</param>
/// <param name="CacheWriteTokens">Input tokens written to the prompt cache.</param>
/// <param name="CostUsd">Cost at the model's configured prices.</param>
/// <param name="ReferenceCostUsd">Cost at the reference model's prices (<see cref="UsageOptions.ReferenceModel"/>), or <see cref="CostUsd"/>.</param>
/// <param name="Profile">The routing profile the request was routed under, if any.</param>
/// <param name="SubscriptionValueUsd">
/// For a subscription model, the request at the provider's API prices (<see cref="ModelDefinition.ApiValue"/>): value
/// received, not spend. 0 otherwise.
/// </param>
public sealed record UsageRecord(
    DateTimeOffset Timestamp, string Model, string Connector, string? Route, string? Client, bool IsLocal,
    long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, decimal CostUsd, decimal ReferenceCostUsd,
    string? Profile = null, decimal SubscriptionValueUsd = 0m);

/// <summary>
/// Usage added up. <c>SubscriptionValueUsd</c> is what the subscriptions' requests would have cost at API prices; it is
/// not part of <c>CostUsd</c>.
/// </summary>
public sealed record UsageTotals(
    long Requests, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, decimal CostUsd, decimal ReferenceCostUsd,
    decimal SubscriptionValueUsd = 0m)
{
    /// <summary>Nothing.</summary>
    public static UsageTotals Zero { get; } = new(0, 0, 0, 0, 0, 0, 0);

    /// <summary>All tokens: input, output and cache.</summary>
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;

    /// <summary>What routing saved compared with sending everything to the reference model.</summary>
    public decimal SavedUsd => ReferenceCostUsd - CostUsd;

    /// <summary>Adds a record.</summary>
    public UsageTotals Add(UsageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new(Requests + 1, InputTokens + record.InputTokens, OutputTokens + record.OutputTokens, CacheReadTokens + record.CacheReadTokens,
            CacheWriteTokens + record.CacheWriteTokens, CostUsd + record.CostUsd, ReferenceCostUsd + record.ReferenceCostUsd,
            SubscriptionValueUsd + record.SubscriptionValueUsd);
    }
}

/// <summary>How <see cref="IUsageLedger.Summarize"/> groups usage.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<UsageGrouping>))]
public enum UsageGrouping
{
    /// <summary>By catalog model.</summary>
    Model = 0,

    /// <summary>By connector.</summary>
    Connector = 1,

    /// <summary>By route.</summary>
    Route = 2,

    /// <summary>By day (in the budget time zone), <c>yyyy-MM-dd</c>.</summary>
    Day = 3,

    /// <summary>By gateway client.</summary>
    Client = 4,

    /// <summary>By routing profile.</summary>
    Profile = 5,

    /// <summary>By where the model runs: <c>local</c> or <c>cloud</c>.</summary>
    Location = 6,
}

/// <summary>One group of a usage summary.</summary>
public sealed record UsageGroup(string Key, UsageTotals Totals);

/// <summary>Records what every served request used, for the dashboard and for budgets.</summary>
public interface IUsageLedger
{
    /// <summary>Records a served request. Must not throw for storage problems.</summary>
    void Record(UsageRecord record);

    /// <summary>Totals between <paramref name="since"/> (inclusive) and <paramref name="until"/> (exclusive), optionally for one model, connector or profile.</summary>
    UsageTotals Total(DateTimeOffset since, DateTimeOffset until, string? model = null, string? connector = null, string? profile = null);

    /// <summary>Totals per group between <paramref name="since"/> and <paramref name="until"/>, largest cost first.</summary>
    IReadOnlyList<UsageGroup> Summarize(DateTimeOffset since, DateTimeOffset until, UsageGrouping by, TimeZoneInfo? zone = null);

    /// <summary>
    /// The records between <paramref name="since"/> and <paramref name="until"/>, optionally for one model, connector or
    /// profile, so <see cref="UsagePricing"/> can price their tokens at the catalog's current prices. <see langword="null"/>
    /// (the default) for a ledger that keeps only totals: its usage stays at the prices it was recorded at.
    /// </summary>
    IReadOnlyList<UsageRecord>? Records(DateTimeOffset since, DateTimeOffset until, string? model = null, string? connector = null, string? profile = null) => null;
}

/// <summary>The default <see cref="IUsageLedger"/>: this process's usage, in memory, for the last 40 days.</summary>
public sealed class InMemoryUsageLedger : IUsageLedger
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(40);
    private readonly List<UsageRecord> _records = [];
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public void Record(UsageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            _records.Add(record);
            if (_records.Count % 1000 == 0)
            {
                DateTimeOffset oldest = record.Timestamp - Retention;
                _records.RemoveAll(r => r.Timestamp < oldest);
            }
        }
    }

    /// <inheritdoc />
    public UsageTotals Total(DateTimeOffset since, DateTimeOffset until, string? model = null, string? connector = null, string? profile = null) =>
        Records(since, until, model, connector, profile).Aggregate(UsageTotals.Zero, (total, r) => total.Add(r));

    /// <inheritdoc />
    public IReadOnlyList<UsageRecord> Records(DateTimeOffset since, DateTimeOffset until, string? model = null, string? connector = null, string? profile = null)
    {
        lock (_gate)
        {
            return _records
                .Where(r => r.Timestamp >= since && r.Timestamp < until
                            && (model is null || string.Equals(r.Model, model, StringComparison.OrdinalIgnoreCase))
                            && (connector is null || string.Equals(r.Connector, connector, StringComparison.OrdinalIgnoreCase))
                            && (profile is null || string.Equals(r.Profile, profile, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<UsageGroup> Summarize(DateTimeOffset since, DateTimeOffset until, UsageGrouping by, TimeZoneInfo? zone = null)
    {
        lock (_gate)
        {
            return UsageSummaries.Group(_records.Where(r => r.Timestamp >= since && r.Timestamp < until), by, zone ?? TimeZoneInfo.Local);
        }
    }
}

/// <summary>Shared grouping for ledgers.</summary>
public static class UsageSummaries
{
    /// <summary>Groups <paramref name="records"/> by <paramref name="by"/>, largest cost (then tokens) first.</summary>
    public static IReadOnlyList<UsageGroup> Group(IEnumerable<UsageRecord> records, UsageGrouping by, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(zone);
        return records
            .GroupBy(r => KeyOf(r, by, zone), StringComparer.OrdinalIgnoreCase)
            .Select(g => new UsageGroup(g.Key, g.Aggregate(UsageTotals.Zero, (total, r) => total.Add(r))))
            .OrderByDescending(g => by == UsageGrouping.Day ? 0 : g.Totals.CostUsd)
            .ThenByDescending(g => by == UsageGrouping.Day ? 0 : g.Totals.TotalTokens)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>The group key of one record.</summary>
    public static string KeyOf(UsageRecord record, UsageGrouping by, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(record);
        return by switch
        {
            UsageGrouping.Model => record.Model,
            UsageGrouping.Connector => record.Connector,
            UsageGrouping.Route => record.Route ?? "(no route)",
            UsageGrouping.Client => record.Client ?? "(no key)",
            UsageGrouping.Profile => record.Profile ?? "(no profile)",
            UsageGrouping.Location => record.IsLocal ? "local" : "cloud",
            _ => TimeZoneInfo.ConvertTime(record.Timestamp, zone).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}

/// <summary>
/// Usage at the catalog's current prices. A ledger stores what each request cost when it was served; these price the
/// stored tokens again, so correcting a model's price (or adding one to a model that had none) corrects its past usage,
/// its subscription value and the budgets too. Records of a model no longer in the catalog keep their stored cost.
/// </summary>
public static class UsagePricing
{
    /// <summary><see cref="IUsageLedger.Total"/>, priced at <paramref name="prices"/>' current prices when the ledger keeps records.</summary>
    /// <param name="ledger">The ledger.</param>
    /// <param name="prices">The configuration whose models and reference model price the usage; <see langword="null"/> for the stored costs.</param>
    /// <param name="since">From (inclusive).</param>
    /// <param name="until">To (exclusive).</param>
    /// <param name="model">Only this model.</param>
    /// <param name="connector">Only this connector.</param>
    /// <param name="profile">Only this profile.</param>
    public static UsageTotals TotalAtCurrentPrices(
        this IUsageLedger ledger, RouterConfiguration? prices, DateTimeOffset since, DateTimeOffset until, string? model = null, string? connector = null, string? profile = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        return prices is not null && ledger.Records(since, until, model, connector, profile) is { } records
            ? records.Select(Pricer(prices)).Aggregate(UsageTotals.Zero, (total, r) => total.Add(r))
            : ledger.Total(since, until, model, connector, profile);
    }

    /// <summary><see cref="IUsageLedger.Summarize"/>, priced at <paramref name="prices"/>' current prices when the ledger keeps records.</summary>
    /// <param name="ledger">The ledger.</param>
    /// <param name="prices">The configuration whose models and reference model price the usage; <see langword="null"/> for the stored costs.</param>
    /// <param name="since">From (inclusive).</param>
    /// <param name="until">To (exclusive).</param>
    /// <param name="by">How to group.</param>
    /// <param name="zone">The time zone days are counted in.</param>
    public static IReadOnlyList<UsageGroup> SummarizeAtCurrentPrices(
        this IUsageLedger ledger, RouterConfiguration? prices, DateTimeOffset since, DateTimeOffset until, UsageGrouping by, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        return prices is not null && ledger.Records(since, until) is { } records
            ? UsageSummaries.Group(records.Select(Pricer(prices)), by, zone ?? TimeZoneInfo.Local)
            : ledger.Summarize(since, until, by, zone);
    }

    /// <summary>
    /// Prices records the way the dispatcher does when it records them: cost at the model's prices, the reference cost at
    /// the reference model's (cache included) or the cost, and a subscription's value at its API prices.
    /// </summary>
    /// <param name="prices">The configuration whose models and reference model price the records.</param>
    public static Func<UsageRecord, UsageRecord> Pricer(RouterConfiguration prices)
    {
        ArgumentNullException.ThrowIfNull(prices);
        var models = new Dictionary<string, ModelDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (ModelDefinition m in prices.Models)
        {
            models.TryAdd(m.Name, m);
        }

        ModelDefinition? reference = prices.Usage.ReferenceModel is { Length: > 0 } name ? models.GetValueOrDefault(name) : null;
        return r =>
        {
            ModelDefinition? model = models.GetValueOrDefault(r.Model);
            decimal cost = model?.Cost(r.InputTokens, r.OutputTokens, r.CacheReadTokens, r.CacheWriteTokens) ?? r.CostUsd;
            return r with
            {
                CostUsd = cost,
                ReferenceCostUsd = reference?.Cost(r.InputTokens, r.OutputTokens, r.CacheReadTokens, r.CacheWriteTokens) ?? cost,
                SubscriptionValueUsd = model?.ApiValue(r.InputTokens, r.OutputTokens, r.CacheReadTokens, r.CacheWriteTokens) ?? r.SubscriptionValueUsd,
            };
        };
    }
}
