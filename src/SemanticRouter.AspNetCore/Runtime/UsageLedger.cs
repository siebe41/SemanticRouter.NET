using System.Text.Json.Serialization;

namespace SemanticRouter.AspNetCore;

/// <summary>What one served request used.</summary>
public sealed record UsageRecord
{
    /// <summary>When it finished.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>The catalog model that served it.</summary>
    public required string Model { get; init; }

    /// <summary>Its connector.</summary>
    public required string Connector { get; init; }

    /// <summary>The matched route, if any.</summary>
    public string? Route { get; init; }

    /// <summary>The gateway key name, if any.</summary>
    public string? Client { get; init; }

    /// <summary>Whether the model is local.</summary>
    public required bool IsLocal { get; init; }

    /// <summary>Input tokens, excluding cached ones.</summary>
    public required long InputTokens { get; init; }

    /// <summary>Output tokens.</summary>
    public required long OutputTokens { get; init; }

    /// <summary>Input tokens read from the prompt cache.</summary>
    public required long CacheReadTokens { get; init; }

    /// <summary>Input tokens written to the prompt cache.</summary>
    public required long CacheWriteTokens { get; init; }

    /// <summary>Cost at the model's configured prices.</summary>
    public required decimal CostUsd { get; init; }

    /// <summary>Cost at the reference model's prices (<see cref="UsageOptions.ReferenceModel"/>), or <see cref="CostUsd"/>.</summary>
    public required decimal ReferenceCostUsd { get; init; }

    /// <summary>The routing profile the request was routed under, if any.</summary>
    public string? Profile { get; init; }

    /// <summary>
    /// For a subscription model, the request at the provider's API prices (<see cref="ModelDefinition.ApiValue"/>): value
    /// received, not spend. 0 otherwise.
    /// </summary>
    public decimal SubscriptionValueUsd { get; init; }
}

/// <summary>
/// Usage added up. <c>SubscriptionValueUsd</c> is what the subscriptions' requests would have cost at API prices; it is
/// not part of <c>CostUsd</c>.
/// </summary>
public sealed record UsageTotals
{
    /// <summary>Served requests.</summary>
    public required long Requests { get; init; }

    /// <summary>Input tokens, excluding cached ones.</summary>
    public required long InputTokens { get; init; }

    /// <summary>Output tokens.</summary>
    public required long OutputTokens { get; init; }

    /// <summary>Input tokens read from the prompt cache.</summary>
    public required long CacheReadTokens { get; init; }

    /// <summary>Input tokens written to the prompt cache.</summary>
    public required long CacheWriteTokens { get; init; }

    /// <summary>Cost at the models' prices.</summary>
    public required decimal CostUsd { get; init; }

    /// <summary>Cost at the reference model's prices.</summary>
    public required decimal ReferenceCostUsd { get; init; }

    /// <summary>The subscription requests at the providers' API prices (<see cref="UsageRecord.SubscriptionValueUsd"/>).</summary>
    public decimal SubscriptionValueUsd { get; init; }

    /// <summary>Nothing.</summary>
    public static UsageTotals Zero { get; } = new UsageTotals
    {
        Requests = 0,
        InputTokens = 0,
        OutputTokens = 0,
        CacheReadTokens = 0,
        CacheWriteTokens = 0,
        CostUsd = 0,
        ReferenceCostUsd = 0,
    };

    /// <summary>All tokens: input, output and cache.</summary>
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;

    /// <summary>What routing saved compared with sending everything to the reference model.</summary>
    public decimal SavedUsd => ReferenceCostUsd - CostUsd;

    /// <summary>Adds a record.</summary>
    public UsageTotals Add(UsageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new UsageTotals
        {
            Requests = Requests + 1,
            InputTokens = InputTokens + record.InputTokens,
            OutputTokens = OutputTokens + record.OutputTokens,
            CacheReadTokens = CacheReadTokens + record.CacheReadTokens,
            CacheWriteTokens = CacheWriteTokens + record.CacheWriteTokens,
            CostUsd = CostUsd + record.CostUsd,
            ReferenceCostUsd = ReferenceCostUsd + record.ReferenceCostUsd,
            SubscriptionValueUsd = SubscriptionValueUsd + record.SubscriptionValueUsd,
        };
    }

    /// <summary>Adds other totals.</summary>
    public UsageTotals Add(UsageTotals other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new UsageTotals
        {
            Requests = Requests + other.Requests,
            InputTokens = InputTokens + other.InputTokens,
            OutputTokens = OutputTokens + other.OutputTokens,
            CacheReadTokens = CacheReadTokens + other.CacheReadTokens,
            CacheWriteTokens = CacheWriteTokens + other.CacheWriteTokens,
            CostUsd = CostUsd + other.CostUsd,
            ReferenceCostUsd = ReferenceCostUsd + other.ReferenceCostUsd,
            SubscriptionValueUsd = SubscriptionValueUsd + other.SubscriptionValueUsd,
        };
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
public sealed record UsageGroup
{
    /// <summary>The group: a model, connector, route, day, client, profile or location, by the grouping asked for.</summary>
    public required string Key { get; init; }

    /// <summary>The group's usage.</summary>
    public required UsageTotals Totals { get; init; }
}

/// <summary>
/// One model's usage within one group of a summary (<see cref="Key"/>, as in <see cref="UsageGroup"/>), at the
/// prices it was recorded at.
/// </summary>
public sealed record UsageModelTotals
{
    /// <summary>The group, as in <see cref="UsageGroup.Key"/>.</summary>
    public required string Key { get; init; }

    /// <summary>The catalog model.</summary>
    public required string Model { get; init; }

    /// <summary>The model's usage in the group.</summary>
    public required UsageTotals Totals { get; init; }
}

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

    /// <summary>
    /// Totals per group and model between <paramref name="since"/> and <paramref name="until"/>, optionally for one
    /// model, connector or profile, so <see cref="UsagePricing"/> can price the tokens at the catalog's current prices
    /// without reading every record: cost is linear in tokens, so a model's token totals price the same as its records.
    /// With <see cref="UsageGrouping.Model"/>, the key is the model. <see langword="null"/> (the default) for a ledger
    /// that does not add them up: <see cref="Records"/> is used then.
    /// </summary>
    IReadOnlyList<UsageModelTotals>? ModelTotals(
        DateTimeOffset since, DateTimeOffset until, UsageGrouping by, TimeZoneInfo? zone = null, string? model = null, string? connector = null, string? profile = null) => null;
}

/// <summary>
/// The default <see cref="IUsageLedger"/>: this process's usage, in memory, for the last 40 days. The totals of the
/// ranges asked for most recently (a budget's current period) are kept up to date as records arrive, so a budget check
/// does not add up the period's records on every request.
/// </summary>
public sealed class InMemoryUsageLedger : IUsageLedger
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(40);
    private readonly List<UsageRecord> _records = [];
    private readonly RunningUsageTotals<UsageModelTotalsBuilder> _running = new();
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
                if (_records.RemoveAll(r => r.Timestamp < oldest) > 0)
                {
                    _running.Clear(); // a range reaching back past the retention lost records
                }
            }

            foreach ((UsageRange range, UsageModelTotalsBuilder totals) in _running.Entries)
            {
                if (range.Covers(record))
                {
                    totals.Add(record.Model, record);
                }
            }
        }
    }

    /// <inheritdoc />
    public UsageTotals Total(DateTimeOffset since, DateTimeOffset until, string? model = null, string? connector = null, string? profile = null) =>
        ModelTotals(since, until, UsageGrouping.Model, null, model, connector, profile).Aggregate(UsageTotals.Zero, (total, g) => total.Add(g.Totals));

    /// <inheritdoc />
    public IReadOnlyList<UsageRecord> Records(DateTimeOffset since, DateTimeOffset until, string? model = null, string? connector = null, string? profile = null)
    {
        var range = new UsageRange(since, until, model, connector, profile);
        lock (_gate)
        {
            return _records.Where(range.Covers).ToArray();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<UsageModelTotals> ModelTotals(
        DateTimeOffset since, DateTimeOffset until, UsageGrouping by, TimeZoneInfo? zone = null, string? model = null, string? connector = null, string? profile = null)
    {
        var range = new UsageRange(since, until, model, connector, profile);
        lock (_gate)
        {
            return by == UsageGrouping.Model
                ? _running.GetOrAdd(range, r => UsageModelTotalsBuilder.Of(_records.Where(r.Covers), by, TimeZoneInfo.Local)).ToList()
                : UsageModelTotalsBuilder.Of(_records.Where(range.Covers), by, zone ?? TimeZoneInfo.Local).ToList();
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

/// <summary>A range of usage to add up: a period, optionally for one model, connector or profile.</summary>
internal readonly record struct UsageRange(DateTimeOffset Since, DateTimeOffset Until, string? Model, string? Connector, string? Profile)
{
    public bool Covers(UsageRecord record) =>
        record.Timestamp >= Since && record.Timestamp < Until
        && (Model is null || string.Equals(record.Model, Model, StringComparison.OrdinalIgnoreCase))
        && (Connector is null || string.Equals(record.Connector, Connector, StringComparison.OrdinalIgnoreCase))
        && (Profile is null || string.Equals(record.Profile, Profile, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Usage added up per group and model; keys and models are compared ignoring case, as in <see cref="UsageSummaries"/>.</summary>
internal sealed class UsageModelTotalsBuilder
{
    private readonly Dictionary<(string Key, string Model), UsageModelTotals> _totals = new(KeyComparer.Instance);

    public static UsageModelTotalsBuilder Of(IEnumerable<UsageRecord> records, UsageGrouping by, TimeZoneInfo zone)
    {
        var builder = new UsageModelTotalsBuilder();
        foreach (UsageRecord record in records)
        {
            builder.Add(UsageSummaries.KeyOf(record, by, zone), record);
        }

        return builder;
    }

    public void Add(string key, UsageRecord record) => Add(key, record.Model, UsageTotals.Zero.Add(record));

    public void Add(string key, string model, UsageTotals totals) =>
        _totals[(key, model)] = _totals.TryGetValue((key, model), out UsageModelTotals? sum)
            ? sum with { Totals = sum.Totals.Add(totals) }
            : new UsageModelTotals { Key = key, Model = model, Totals = totals };

    public void Add(IEnumerable<UsageModelTotals> totals)
    {
        foreach (UsageModelTotals group in totals)
        {
            Add(group.Key, group.Model, group.Totals);
        }
    }

    public IReadOnlyList<UsageModelTotals> ToList() => [.. _totals.Values];

    private sealed class KeyComparer : IEqualityComparer<(string Key, string Model)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((string Key, string Model) x, (string Key, string Model) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Key, y.Key) && StringComparer.OrdinalIgnoreCase.Equals(x.Model, y.Model);

        public int GetHashCode((string Key, string Model) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Key), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Model));
    }
}

/// <summary>
/// The running totals of the ranges a ledger was asked for most recently, at most <see cref="Capacity"/>: the least
/// recently used goes first. Not thread-safe; the ledger locks around it.
/// </summary>
internal sealed class RunningUsageTotals<T>
    where T : class
{
    public const int Capacity = 16;
    private readonly Dictionary<UsageRange, (T Totals, long Used)> _entries = [];
    private long _uses;

    public IEnumerable<KeyValuePair<UsageRange, T>> Entries => _entries.Select(e => KeyValuePair.Create(e.Key, e.Value.Totals));

    public T GetOrAdd(UsageRange range, Func<UsageRange, T> create)
    {
        if (_entries.TryGetValue(range, out (T Totals, long Used) entry))
        {
            _entries[range] = (entry.Totals, ++_uses);
            return entry.Totals;
        }

        if (_entries.Count >= Capacity)
        {
            _entries.Remove(_entries.MinBy(e => e.Value.Used).Key);
        }

        T totals = create(range);
        _entries[range] = (totals, ++_uses);
        return totals;
    }

    public void Clear() => _entries.Clear();

    public void RemoveWhere(Func<UsageRange, bool> predicate)
    {
        foreach (UsageRange range in _entries.Keys.Where(predicate).ToArray())
        {
            _entries.Remove(range);
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
        return Order(records
            .GroupBy(r => KeyOf(r, by, zone), StringComparer.OrdinalIgnoreCase)
            .Select(g => new UsageGroup { Key = g.Key, Totals = g.Aggregate(UsageTotals.Zero, (total, r) => total.Add(r)) }), by);
    }

    /// <summary>Adds up <paramref name="totals"/> per group, in the order of the groups of records.</summary>
    internal static IReadOnlyList<UsageGroup> Group(IEnumerable<UsageModelTotals> totals, UsageGrouping by) =>
        Order(totals
            .GroupBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new UsageGroup { Key = g.Key, Totals = g.Aggregate(UsageTotals.Zero, (total, t) => total.Add(t.Totals)) }), by);

    private static UsageGroup[] Order(IEnumerable<UsageGroup> groups, UsageGrouping by) => groups
        .OrderByDescending(g => by == UsageGrouping.Day ? 0 : g.Totals.CostUsd)
        .ThenByDescending(g => by == UsageGrouping.Day ? 0 : g.Totals.TotalTokens)
        .ThenBy(g => g.Key, StringComparer.Ordinal)
        .ToArray();

    /// <summary>The group key of one record.</summary>
    public static string KeyOf(UsageRecord record, UsageGrouping by, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(record);
        return by switch
        {
            UsageGrouping.Model => record.Model,
            UsageGrouping.Connector => record.Connector,
            UsageGrouping.Route => record.Route ?? NoRoute,
            UsageGrouping.Client => record.Client ?? NoClient,
            UsageGrouping.Profile => record.Profile ?? NoProfile,
            UsageGrouping.Location => record.IsLocal ? "local" : "cloud",
            _ => DayKey(record.Timestamp, zone),
        };
    }

    internal const string NoRoute = "(no route)";
    internal const string NoClient = "(no key)";
    internal const string NoProfile = "(no profile)";

    /// <summary>The day <paramref name="timestamp"/> falls on in <paramref name="zone"/>, <c>yyyy-MM-dd</c>.</summary>
    internal static string DayKey(DateTimeOffset timestamp, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(timestamp, zone).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Usage at the catalog's current prices. A ledger stores what each request cost when it was served; these price the
/// stored tokens again, so correcting a model's price (or adding one to a model that had none) corrects its past usage,
/// its subscription value and the budgets too. Records of a model no longer in the catalog keep their stored cost.
/// </summary>
public static class UsagePricing
{
    /// <summary><see cref="IUsageLedger.Total"/>, priced at <paramref name="prices"/>' current prices when the ledger keeps records or per-model totals.</summary>
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
        if (prices is null)
        {
            return ledger.Total(since, until, model, connector, profile);
        }

        if (ledger.ModelTotals(since, until, UsageGrouping.Model, null, model, connector, profile) is { } totals)
        {
            Func<UsageModelTotals, UsageTotals> price = TotalsPricer(prices);
            return totals.Aggregate(UsageTotals.Zero, (total, t) => total.Add(price(t)));
        }

        return ledger.Records(since, until, model, connector, profile) is { } records
            ? records.Select(Pricer(prices)).Aggregate(UsageTotals.Zero, (total, r) => total.Add(r))
            : ledger.Total(since, until, model, connector, profile);
    }

    /// <summary><see cref="IUsageLedger.Summarize"/>, priced at <paramref name="prices"/>' current prices when the ledger keeps records or per-model totals.</summary>
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
        if (prices is null)
        {
            return ledger.Summarize(since, until, by, zone);
        }

        if (ledger.ModelTotals(since, until, by, zone) is { } totals)
        {
            Func<UsageModelTotals, UsageTotals> price = TotalsPricer(prices);
            return UsageSummaries.Group(totals.Select(t => t with { Totals = price(t) }), by);
        }

        return ledger.Records(since, until) is { } records
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
        Func<string, long, long, long, long, decimal, decimal, (decimal Cost, decimal Reference, decimal Value)> price = Prices(prices);
        return r =>
        {
            (decimal cost, decimal reference, decimal value) = price(r.Model, r.InputTokens, r.OutputTokens, r.CacheReadTokens, r.CacheWriteTokens, r.CostUsd, r.SubscriptionValueUsd);
            return r with { CostUsd = cost, ReferenceCostUsd = reference, SubscriptionValueUsd = value };
        };
    }

    /// <summary>
    /// <see cref="Pricer"/> for one model's totals: prices are per token, so pricing the token totals gives what pricing
    /// each record and adding them up gives.
    /// </summary>
    private static Func<UsageModelTotals, UsageTotals> TotalsPricer(RouterConfiguration prices)
    {
        Func<string, long, long, long, long, decimal, decimal, (decimal Cost, decimal Reference, decimal Value)> price = Prices(prices);
        return t =>
        {
            UsageTotals totals = t.Totals;
            (decimal cost, decimal reference, decimal value) = price(
                t.Model, totals.InputTokens, totals.OutputTokens, totals.CacheReadTokens, totals.CacheWriteTokens, totals.CostUsd, totals.SubscriptionValueUsd);
            return totals with { CostUsd = cost, ReferenceCostUsd = reference, SubscriptionValueUsd = value };
        };
    }

    private static Func<string, long, long, long, long, decimal, decimal, (decimal Cost, decimal Reference, decimal Value)> Prices(RouterConfiguration prices)
    {
        ArgumentNullException.ThrowIfNull(prices);
        var models = new Dictionary<string, ModelDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (ModelDefinition m in prices.Models)
        {
            models.TryAdd(m.Name, m);
        }

        ModelDefinition? reference = prices.Usage.ReferenceModel is { Length: > 0 } name ? models.GetValueOrDefault(name) : null;
        return (name, input, output, cacheRead, cacheWrite, storedCost, storedValue) =>
        {
            ModelDefinition? model = models.GetValueOrDefault(name);
            decimal cost = model?.Cost(input, output, cacheRead, cacheWrite) ?? storedCost;
            return (cost, reference?.Cost(input, output, cacheRead, cacheWrite) ?? cost, model?.ApiValue(input, output, cacheRead, cacheWrite) ?? storedValue);
        };
    }
}
