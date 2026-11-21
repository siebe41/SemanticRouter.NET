using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>The in-memory ledger's running totals: what budgets read on every request.</summary>
public sealed class UsageTotalsTests
{
    private readonly ManualTimeProvider _time = new();

    private UsageRecord Request(int i) => new UsageRecord
    {
        Timestamp = _time.GetUtcNow().AddMinutes(-i),
        Model = i % 2 == 0 ? "cloud" : "Other",
        Connector = i % 3 == 0 ? "c" : "d",
        IsLocal = false,
        InputTokens = i,
        OutputTokens = 2 * i,
        CacheReadTokens = 0,
        CacheWriteTokens = 0,
        CostUsd = i * 0.001m,
        ReferenceCostUsd = i * 0.002m,
        Profile = i % 4 == 0 ? "work" : null,
    };

    [Fact]
    public void Running_totals_stay_equal_to_the_records_as_requests_arrive()
    {
        var ledger = new InMemoryUsageLedger();
        DateTimeOffset now = _time.GetUtcNow();

        // More ranges than are kept, so some are dropped and added up again.
        (DateTimeOffset Since, DateTimeOffset Until, string? Model, string? Connector, string? Profile)[] ranges =
        [
            .. Enumerable.Range(0, RunningUsageTotals<object>.Capacity + 4).Select(h => (now.AddHours(-h), now.AddHours(1), (string?)null, (string?)null, (string?)null)),
            (now.AddDays(-1), now.AddDays(1), "CLOUD", null, null),
            (now.AddDays(-1), now.AddDays(1), null, "c", null),
            (now.AddDays(-1), now.AddDays(1), null, null, "Work"),
        ];
        for (int i = 0; i < 2000; i++)
        {
            ledger.Record(Request(i % 300));
            if (i % 97 == 0)
            {
                foreach ((DateTimeOffset since, DateTimeOffset until, string? model, string? connector, string? profile) in ranges)
                {
                    ledger.Total(since, until, model, connector, profile).Should().Be(
                        ledger.Records(since, until, model, connector, profile).Aggregate(UsageTotals.Zero, (t, r) => t.Add(r)));
                }
            }
        }
    }

    [Fact]
    public void A_budget_check_does_not_add_up_the_periods_records()
    {
        var ledger = new InMemoryUsageLedger();
        for (int i = 0; i < 200_000; i++)
        {
            ledger.Record(new UsageRecord
            {
                Timestamp = _time.GetUtcNow().AddSeconds(-i),
                Model = "cloud",
                Connector = "c",
                IsLocal = false,
                InputTokens = 1000,
                OutputTokens = 100,
                CacheReadTokens = 0,
                CacheWriteTokens = 0,
                CostUsd = 0m,
                ReferenceCostUsd = 0m,
            });
        }

        var configuration = new RouterConfiguration();
        ModelDefinition cloud = new() { Name = "cloud", Connector = "c", InputCostPerMillionTokens = 2, OutputCostPerMillionTokens = 10 };
        configuration.Models.Add(cloud);
        configuration.Usage.TimeZone = "UTC";
        configuration.Usage.Budgets.Add(new BudgetDefinition { Name = "monthly", Connector = "c", Period = BudgetPeriod.Month, MaxCostUsd = 1_000_000 });
        var gate = new BudgetGate(ledger, _time, NullLogger<BudgetGate>.Instance, () => configuration);
        gate.Blocking(cloud, configuration.Usage).Should().BeNull();

        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++)
        {
            ledger.Record(new UsageRecord
            {
                Timestamp = _time.GetUtcNow(),
                Model = "cloud",
                Connector = "c",
                IsLocal = false,
                InputTokens = 1000,
                OutputTokens = 100,
                CacheReadTokens = 0,
                CacheWriteTokens = 0,
                CostUsd = 0m,
                ReferenceCostUsd = 0m,
            });
            gate.Blocking(cloud, configuration.Usage);
        }

        watch.Stop();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "1,000 checks against 200,000 records");
        gate.Status(configuration.Usage).Single().CostUsedUsd.Should().Be(201_000 * 0.003m);
    }

    [Fact]
    public void Past_its_cap_the_ledger_folds_old_records_into_summaries_with_the_same_totals()
    {
        var bounded = new InMemoryUsageLedger(maxRecords: 100);
        var reference = new InMemoryUsageLedger();
        for (int i = 6000; i >= 0; i--)
        {
            UsageRecord record = Request(i) with { Route = i % 5 == 0 ? "code" : null, Client = i % 7 == 0 ? "laptop" : null, IsLocal = i % 6 == 0 };
            bounded.Record(record);
            reference.Record(record);
        }

        bounded.Held.Records.Should().BeLessThanOrEqualTo(100);
        bounded.Held.Summaries.Should().BePositive();

        DateTimeOffset now = _time.GetUtcNow();
        DateTimeOffset today = new(now.UtcDateTime.Date, TimeSpan.Zero);
        TimeZoneInfo plus545 = TimeZoneInfo.CreateCustomTimeZone("UTC+05:45", TimeSpan.FromMinutes(345), "UTC+05:45", "UTC+05:45");
        (DateTimeOffset Since, DateTimeOffset Until)[] periods =
        [
            (DateTimeOffset.MinValue, DateTimeOffset.MaxValue),
            (today, today.AddDays(1)),
            (today.AddDays(-2), today.AddDays(-1)),
            (today.AddDays(-3).AddMinutes(-345), today.AddDays(-2).AddMinutes(-345)), // a day in UTC+05:45
        ];
        foreach ((DateTimeOffset since, DateTimeOffset until) in periods)
        {
            bounded.Total(since, until).Should().Be(reference.Total(since, until));
            bounded.Total(since, until, model: "cloud", connector: "c").Should().Be(reference.Total(since, until, model: "cloud", connector: "c"));
            bounded.Total(since, until, profile: "work").Should().Be(reference.Total(since, until, profile: "work"));
            foreach (UsageGrouping by in Enum.GetValues<UsageGrouping>())
            {
                foreach (TimeZoneInfo zone in new[] { TimeZoneInfo.Utc, plus545 })
                {
                    bounded.Summarize(since, until, by, zone).Should().Equal(reference.Summarize(since, until, by, zone), $"grouped by {by} in {zone.Id}");
                }
            }
        }

        bounded.Records(DateTimeOffset.MinValue, DateTimeOffset.MaxValue).Aggregate(UsageTotals.Zero, (t, r) => t.Add(r)).TotalTokens
            .Should().Be(reference.Total(DateTimeOffset.MinValue, DateTimeOffset.MaxValue).TotalTokens, "a summary is returned as one record holding its tokens");
    }
}
