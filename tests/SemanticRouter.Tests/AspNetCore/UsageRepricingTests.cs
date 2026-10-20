using Microsoft.Extensions.Logging.Abstractions;
using SemanticRouter.AspNetCore;
using SemanticRouter.Storage.Sqlite;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>Usage priced at the catalog's current prices, so a corrected price corrects past usage and budgets.</summary>
public sealed class UsageRepricingTests
{
    private readonly ManualTimeProvider _time = new();

    private DateTimeOffset Since => _time.GetUtcNow().AddHours(-1);

    private DateTimeOffset Until => _time.GetUtcNow().AddHours(1);

    private static RouterConfiguration Catalog(params ModelDefinition[] models)
    {
        var configuration = new RouterConfiguration();
        foreach (ModelDefinition model in models)
        {
            configuration.Models.Add(model);
        }

        return configuration;
    }

    // A million tokens of each kind, recorded when the model was free.
    private UsageRecord Million(string model, string connector = "c", string? route = null) =>
        new(_time.GetUtcNow(), model, connector, route, null, false, 1_000_000, 1_000_000, 1_000_000, 1_000_000, 0m, 0m);

    [Fact]
    public void A_price_set_after_the_requests_prices_them_and_their_cache_tokens()
    {
        var ledger = new InMemoryUsageLedger();
        ledger.Record(Million("cloud"));
        RouterConfiguration prices = Catalog(new ModelDefinition
        {
            Name = "cloud",
            Connector = "c",
            InputCostPerMillionTokens = 3,
            OutputCostPerMillionTokens = 15,
            CacheReadCostPerMillionTokens = 0.3m,
            CacheWriteCostPerMillionTokens = 3.75m,
        });

        ledger.Total(Since, Until).CostUsd.Should().Be(0m, "the ledger keeps what it recorded");
        ledger.TotalAtCurrentPrices(prices, Since, Until).CostUsd.Should().Be(22.05m);
        ledger.SummarizeAtCurrentPrices(prices, Since, Until, UsageGrouping.Model).Single().Totals.CostUsd.Should().Be(22.05m);
    }

    [Fact]
    public void A_subscription_is_valued_at_its_api_cache_prices_and_the_reference_model_follows_its_price()
    {
        var ledger = new InMemoryUsageLedger();
        ledger.Record(Million("sub", "cli", route: "code"));
        RouterConfiguration prices = Catalog(
            new ModelDefinition
            {
                Name = "sub",
                Connector = "cli",
                ApiInputCostPerMillionTokens = 2,
                ApiOutputCostPerMillionTokens = 10,
                ApiCacheReadCostPerMillionTokens = 0.2m,
                ApiCacheWriteCostPerMillionTokens = 2.5m,
            },
            new ModelDefinition { Name = "big", Connector = "c", InputCostPerMillionTokens = 5, OutputCostPerMillionTokens = 25 });
        prices.Usage.ReferenceModel = "big";

        UsageTotals totals = ledger.SummarizeAtCurrentPrices(prices, Since, Until, UsageGrouping.Route).Single(g => g.Key == "code").Totals;

        totals.CostUsd.Should().Be(0m, "a subscription costs nothing per request");
        totals.SubscriptionValueUsd.Should().Be(14.7m);
        totals.ReferenceCostUsd.Should().Be(40m, "the reference model has no cache prices, so cache tokens are at its input price");
    }

    [Fact]
    public void A_model_no_longer_in_the_catalog_keeps_its_recorded_cost()
    {
        var ledger = new InMemoryUsageLedger();
        ledger.Record(new UsageRecord(_time.GetUtcNow(), "gone", "c", null, null, false, 10, 10, 0, 0, 1.25m, 1.25m, SubscriptionValueUsd: 0.5m));

        UsageTotals totals = ledger.TotalAtCurrentPrices(Catalog(), Since, Until);

        totals.CostUsd.Should().Be(1.25m);
        totals.SubscriptionValueUsd.Should().Be(0.5m);
    }

    [Fact]
    public void A_ledger_that_keeps_only_totals_stays_at_its_recorded_prices()
    {
        var ledger = new TotalsOnlyLedger();
        ledger.Record(Million("cloud"));

        ledger.TotalAtCurrentPrices(Catalog(new ModelDefinition { Name = "cloud", Connector = "c", InputCostPerMillionTokens = 3 }), Since, Until)
            .Should().Be(ledger.Total(Since, Until));
    }

    [Fact]
    public void A_budget_counts_spend_at_the_current_prices()
    {
        var ledger = new InMemoryUsageLedger();
        ledger.Record(Million("cloud"));
        RouterConfiguration prices = Catalog(new ModelDefinition { Name = "cloud", Connector = "c", InputCostPerMillionTokens = 1, OutputCostPerMillionTokens = 1 });
        var usage = new UsageOptions { TimeZone = "UTC" };
        usage.Budgets.Add(new BudgetDefinition { Name = "daily", Model = "cloud", MaxCostUsd = 3 });
        var gate = new BudgetGate(ledger, _time, NullLogger<BudgetGate>.Instance, () => prices);

        gate.Status(usage).Single().Should().Match<BudgetStatus>(s => s.Exceeded && s.CostUsedUsd == 4m);
    }

    [Fact]
    public void The_sqlite_ledger_hands_out_its_records_for_repricing()
    {
        string directory = Directory.CreateTempSubdirectory("router-usage").FullName;
        try
        {
            var options = new SqliteStateOptions { Path = Path.Combine(directory, "state.db") };
            var ledger = new SqliteUsageLedger(new SqliteRouterDatabase(Microsoft.Extensions.Options.Options.Create(options), NullLogger<SqliteRouterDatabase>.Instance, _time));
            ledger.Record(Million("cloud"));
            ledger.Record(Million("other", connector: "d"));

            ledger.Records(Since, Until, connector: "c").Should().ContainSingle().Which.Model.Should().Be("cloud");
            ledger.TotalAtCurrentPrices(Catalog(new ModelDefinition { Name = "cloud", Connector = "c", OutputCostPerMillionTokens = 2 }), Since, Until).CostUsd.Should().Be(2m);
        }
        finally
        {
            SqliteFiles.Release(directory);
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A third-party ledger written before records were exposed.</summary>
    private sealed class TotalsOnlyLedger : IUsageLedger
    {
        private UsageTotals _totals = UsageTotals.Zero;

        public void Record(UsageRecord record) => _totals = _totals.Add(record);

        public UsageTotals Total(DateTimeOffset since, DateTimeOffset until, string? model = null, string? connector = null, string? profile = null) => _totals;

        public IReadOnlyList<UsageGroup> Summarize(DateTimeOffset since, DateTimeOffset until, UsageGrouping by, TimeZoneInfo? zone = null) => [new("all", _totals)];
    }
}
