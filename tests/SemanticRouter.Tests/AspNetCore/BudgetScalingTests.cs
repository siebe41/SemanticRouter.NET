using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// A budget is checked on every request, so its cost must not grow with the usage already recorded (#226): it used
/// to read and price every row of the period, 0.6 to 2.5 s per request at 200,000 rows.
/// </summary>
public sealed class BudgetScalingTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("router-budget").FullName;
    private readonly ManualTimeProvider _time = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private string DbPath => Path.Combine(_directory, "state.db");

    private SqliteUsageLedger Ledger() => new(new SqliteRouterDatabase(
        Microsoft.Extensions.Options.Options.Create(new SqliteStateOptions { Path = DbPath }), NullLogger<SqliteRouterDatabase>.Instance, _time));

    private static readonly RouterConfiguration Prices = PricesOf(
        new ModelDefinition { Name = "cloud", Connector = "api", InputCostPerMillionTokens = 3, OutputCostPerMillionTokens = 15, CacheReadCostPerMillionTokens = 0.3m },
        new ModelDefinition { Name = "local", Connector = "lm", IsLocal = true },
        new ModelDefinition { Name = "sub", Connector = "cli", ApiInputCostPerMillionTokens = 2, ApiOutputCostPerMillionTokens = 10 });

    private static RouterConfiguration PricesOf(params ModelDefinition[] models)
    {
        var configuration = new RouterConfiguration();
        foreach (ModelDefinition model in models)
        {
            configuration.Models.Add(model);
        }

        configuration.Usage.ReferenceModel = "cloud";
        return configuration;
    }

    private static UsageOptions MonthlyBudget(string connector = "api")
    {
        var usage = new UsageOptions { TimeZone = "UTC" };
        usage.Budgets.Add(new BudgetDefinition { Name = "monthly", Connector = connector, Period = BudgetPeriod.Month, MaxCostUsd = 1_000_000 });
        return usage;
    }

    private UsageRecord Record(string model, string connector, int i) => new(
        _time.GetUtcNow(), model, connector, null, null, false, 1_000 + i, 200 + i, i % 7, i % 3, 0.0123m * (i % 5), 0.5m, SubscriptionValueUsd: 0.01m);

    [Fact]
    public void Totals_per_model_price_the_same_as_every_record()
    {
        SqliteUsageLedger ledger = Ledger();
        for (int i = 0; i < 60; i++)
        {
            ledger.Record(Record(i % 4 == 0 ? "gone" : i % 3 == 0 ? "CLOUD" : i % 2 == 0 ? "sub" : "cloud", i % 4 == 0 ? "old" : i % 2 == 0 ? "cli" : "api", i));
        }

        DateTimeOffset since = _time.GetUtcNow().AddDays(-1);
        DateTimeOffset until = _time.GetUtcNow().AddMinutes(1);
        foreach (bool open in new[] { true, false })
        {
            // Still open, so kept and brought up to date; then over, so added up afresh.
            _time.Advance(open ? TimeSpan.Zero : TimeSpan.FromMinutes(2));
            foreach (string? connector in new[] { null, "api", "old" })
            {
                UsageTotals perRecord = ledger.Records(since, until, connector: connector)
                    .Select(UsagePricing.Pricer(Prices)).Aggregate(UsageTotals.Zero, (total, r) => total.Add(r));

                ledger.TotalAtCurrentPrices(Prices, since, until, connector: connector).Should().Be(perRecord, $"open {open}, connector {connector}");
                ledger.Total(since, until, connector: connector).Should().Be(
                    ledger.Records(since, until, connector: connector).Aggregate(UsageTotals.Zero, (total, r) => total.Add(r)));
            }
        }
    }

    [Fact]
    public void An_open_period_reads_only_the_rows_added_since_it_was_counted()
    {
        const int Rows = 50_000;
        Preload(Rows);
        SqliteUsageLedger ledger = Ledger();
        var gate = new BudgetGate(ledger, _time, NullLogger<BudgetGate>.Instance, () => Prices);
        UsageOptions usage = MonthlyBudget();

        decimal before = gate.Status(usage).Single().CostUsedUsd;
        before.Should().Be(Rows * Prices.Models[0].Cost(1_000, 200));

        // Were the old rows read again, their costs would no longer add up.
        Execute("UPDATE usage SET cost = 'not a number', reference_cost = 'not a number';");
        ledger.Record(Record("cloud", "api", 0));

        var timer = Stopwatch.StartNew();
        for (int i = 0; i < 1_000; i++)
        {
            gate.Blocking(Prices.Models[0], usage).Should().BeNull();
        }

        timer.Stop();
        gate.Status(usage).Single().CostUsedUsd.Should().Be(before + Prices.Models[0].Cost(1_000, 200));
        timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "1,000 checks over 50,000 rows used to take minutes");
    }

    [Fact]
    public void Rows_of_another_router_sharing_the_file_are_counted()
    {
        SqliteUsageLedger first = Ledger();
        SqliteUsageLedger second = Ledger();
        var gate = new BudgetGate(first, _time, NullLogger<BudgetGate>.Instance, () => Prices);
        UsageOptions usage = MonthlyBudget("cli");

        gate.Status(usage).Single().TokensUsed.Should().Be(0);
        second.Record(Record("sub", "cli", 1));
        first.Record(Record("sub", "cli", 2));

        gate.Status(usage).Single().TokensUsed.Should().Be(1_001 + 201 + 1 + 1 + 1_002 + 202 + 2 + 2);
    }

    private void Preload(int rows)
    {
        Ledger(); // creates the schema
        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT INTO usage (timestamp_ticks, model, connector, is_local, input, output, cache_read, cache_write, cost, reference_cost) " +
            "VALUES ($ts, 'cloud', 'api', 0, 1000, 200, 0, 0, '1', '1');";
        insert.Parameters.AddWithValue("$ts", _time.GetUtcNow().UtcTicks);
        for (int i = 0; i < rows; i++)
        {
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DbPath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
