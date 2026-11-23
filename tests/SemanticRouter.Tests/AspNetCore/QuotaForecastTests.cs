using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// The quota forecast (#374), back-tested on synthetic history: a subscription with a weekly limit of
/// <see cref="TrueLimit"/> tokens, used 16 hours a day (nothing from midnight to 8:00) with ±15% noise per hour, that
/// runs out about four and a half days into each week and resets a week after the last reset.
/// </summary>
public sealed class QuotaForecastTests : RouterHostTest
{
    /// <summary>The error band these tests hold the forecast to: a share of the time from now until it really runs out.</summary>
    private const double Band = 0.15;

    private const double TrueLimit = 1_000_000;
    private const double TokensPerHour = 9_000;
    private static readonly TimeSpan Week = TimeSpan.FromDays(7);

    private readonly ManualTimeProvider _time = new();

    /// <summary>
    /// The tokens used in hour <paramref name="hour"/> of the simulation (which starts at noon): none at night, else one
    /// request at one and a half times the daily average pace, ±15%.
    /// </summary>
    private static double TokensAt(long hour) =>
        (hour + 12) % 24 < 8 ? 0 : 1.5 * TokensPerHour * (1 + (0.3 * (((ulong)hour * 2654435761UL % 1000 / 1000.0) - 0.5)));

    private static RouterConfiguration Configuration()
    {
        var configuration = new RouterConfiguration();
        configuration.Connectors["claude"] = new ConnectorOptions { Type = ConnectorTypes.Cli, Preset = CliPreset.ClaudeCode };
        configuration.Connectors["lmstudio"] = new ConnectorOptions { BaseAddress = new Uri("http://localhost:1234") };
        configuration.Connectors["openai"] = new ConnectorOptions { BaseAddress = new Uri("https://api.example.com"), ApiKey = "sk-test" };
        configuration.Models.Add(Model("claude-cli", 4, connector: "claude"));
        configuration.Models.Add(Model("local", 1, local: true, connector: "lmstudio"));
        configuration.Models.Add(Model("gpt", 3, costPerMillion: 10m, connector: "openai"));
        configuration.Routes.Add(Route("coding", 3, "write a function"));
        configuration.Routes.Add(Route("smalltalk", 1, "hello there"));
        configuration.Usage.TimeZone = "UTC";
        return configuration;
    }

    /// <summary>
    /// Runs <paramref name="cycles"/> whole weeks, each ending in a limit hit (reported with the next reset) and a request
    /// served after the reset, then <paramref name="into"/> of the current week. Returns the start of the current week and
    /// when it really runs out.
    /// </summary>
    private (DateTimeOffset CycleStart, DateTimeOffset RunsOut) Simulate(IUsageLedger ledger, ConnectorIncidents incidents, int cycles, TimeSpan into)
    {
        DateTimeOffset origin = _time.GetUtcNow();
        long hour = 0;
        for (int k = 0; k < cycles; k++)
        {
            DateTimeOffset reset = origin + (Week * (k + 1));
            double used = 0;
            while (true)
            {
                DateTimeOffset at = origin + TimeSpan.FromHours(++hour);
                double tokens = TokensAt(hour);
                if (used + tokens > TrueLimit)
                {
                    MoveTo(at);
                    incidents.Failed("claude", ModelFailureKind.QuotaExceeded, "You've hit your limit", reset);
                    MoveTo(at.AddMinutes(20));
                    incidents.Failed("claude", ModelFailureKind.QuotaExceeded, "You've hit your limit", reset);
                    MoveTo(reset.AddMinutes(30));
                    incidents.Served("claude");
                    hour = (long)(reset - origin).TotalHours;
                    break;
                }

                used += tokens;
                if (tokens > 0)
                {
                    ledger.Record(Usage(at, "claude-cli", "claude", (long)tokens, "coding"));
                }
            }
        }

        DateTimeOffset start = origin + (Week * cycles);
        DateTimeOffset now = start + into;
        double current = 0;
        DateTimeOffset? runsOut = null;
        for (long h = hour + 1; runsOut is null; h++)
        {
            DateTimeOffset at = origin + TimeSpan.FromHours(h);
            double tokens = TokensAt(h);
            if (at >= now)
            {
                runsOut = current + tokens > TrueLimit ? at : null;
            }
            else if (tokens > 0)
            {
                ledger.Record(Usage(at, "claude-cli", "claude", (long)tokens, "coding"));
            }

            current += tokens;
        }

        MoveTo(now);
        return (start, runsOut.Value);
    }

    private void MoveTo(DateTimeOffset at) => _time.Advance(at - _time.GetUtcNow());

    private static UsageRecord Usage(DateTimeOffset at, string model, string connector, long tokens, string? route, string? client = "laptop", decimal cost = 0) => new()
    {
        Timestamp = at,
        Model = model,
        Connector = connector,
        Route = route,
        Client = client,
        IsLocal = connector == "lmstudio",
        InputTokens = tokens,
        OutputTokens = 0,
        CacheReadTokens = 0,
        CacheWriteTokens = 0,
        CostUsd = cost,
        ReferenceCostUsd = cost,
    };

    private QuotaForecaster Forecaster(IUsageLedger ledger, ConnectorIncidents incidents, RouterConfiguration configuration) =>
        new(ledger, incidents, new BudgetGate(ledger, _time, NullLogger<BudgetGate>.Instance, () => configuration), _time);

    private static void WithinBand(DateTimeOffset? forecast, DateTimeOffset actual, DateTimeOffset now)
    {
        forecast.Should().NotBeNull();
        TimeSpan error = (forecast!.Value - actual).Duration();
        (error / (actual - now)).Should().BeLessThan(Band, $"forecast {forecast:u}, actual {actual:u}");
    }

    [Fact]
    public void Four_recorded_limit_hits_learn_the_limit_and_period_and_forecast_within_the_band()
    {
        var ledger = new InMemoryUsageLedger();
        var incidents = new ConnectorIncidents(_time);
        RouterConfiguration configuration = Configuration();
        (DateTimeOffset start, DateTimeOffset actual) = Simulate(ledger, incidents, cycles: 4, into: TimeSpan.FromHours(43));
        DateTimeOffset now = _time.GetUtcNow();

        ConnectorForecast claude = Forecaster(ledger, incidents, configuration).Forecast(configuration).Connectors.Single();

        claude.Connector.Should().Be("claude");
        claude.Subscription.Should().BeTrue();
        claude.Unit.Should().Be(QuotaUnit.Tokens);
        claude.LimitHits.Should().Be(4, "the second refusal in each week belongs to the same episode");
        claude.Limit.Source.Should().Be("learned");
        claude.Limit.Cycles.Should().Be(4);
        claude.Limit.Confidence.Should().Be("high");
        claude.Limit.Value!.Value.Should().BeInRange(TrueLimit * 0.98, TrueLimit);
        claude.Period.Should().Be(Week);
        claude.PeriodSource.Should().Be("learned", "the resets were reported a week apart");
        claude.CycleStart.Should().Be(start);
        claude.NextReset.Should().Be(start + Week);
        claude.Status.Should().Be(QuotaStatus.Hot, "it runs out about four and a half days into a seven-day week");
        claude.PacePerHour.Should().BeApproximately(TokensPerHour, TokensPerHour * 0.05);

        claude.Backtest.Should().NotBeNull();
        claude.Backtest!.Cycles.Should().Be(4);
        claude.Backtest.MeanRelativeError.Should().BeLessThan(Band);
        WithinBand(claude.RunsOutAt, actual, now);
        claude.RunsOutEarliest.Should().BeOnOrBefore(claude.RunsOutAt!.Value);
        claude.RunsOutLatest.Should().BeOnOrAfter(claude.RunsOutAt.Value);
        claude.Summary.Should().StartWith("At this pace claude runs out ").And.Contain("it resets");

        // The burn: one route and one client, and the local model that could take the route a tier lower.
        claude.Routes.Should().ContainSingle().Which.Should().Match<QuotaDriver>(d => d.Key == "coding" && d.Share == 1);
        claude.Routes[0].MoveTo.Should().BeEquivalentTo(new QuotaMove { Route = "coding", FromTier = 3, Tier = 1, Model = "local", Local = true });
        claude.Clients.Should().ContainSingle().Which.Key.Should().Be("laptop");
    }

    [Fact]
    public void Two_recorded_limit_hits_forecast_within_the_band_from_the_one_whole_cycle_between_them()
    {
        var ledger = new InMemoryUsageLedger();
        var incidents = new ConnectorIncidents(_time);
        RouterConfiguration configuration = Configuration();
        (DateTimeOffset start, DateTimeOffset actual) = Simulate(ledger, incidents, cycles: 2, into: TimeSpan.FromHours(55));
        DateTimeOffset now = _time.GetUtcNow();

        ConnectorForecast claude = Forecaster(ledger, incidents, configuration).Forecast(configuration).Connectors.Single();

        claude.LimitHits.Should().Be(2);
        claude.Limit.Cycles.Should().Be(1, "the first hit has no reset before it to count from");
        claude.Limit.Confidence.Should().Be("low");
        claude.Period.Should().BeNull("two resets do not show a fixed interval");
        claude.CycleStart.Should().Be(start);
        claude.NextReset.Should().BeNull();
        claude.Backtest.Should().BeNull("one cycle leaves none to test against");
        WithinBand(claude.RunsOutAt, actual, now);
        claude.Notes.Should().Contain(n => n.Contains("Usage:Quotas:claude:Period"));
    }

    [Fact]
    public void A_configured_period_makes_both_hits_whole_cycles_and_gives_the_next_reset()
    {
        var ledger = new InMemoryUsageLedger();
        var incidents = new ConnectorIncidents(_time);
        RouterConfiguration configuration = Configuration();
        configuration.Usage.Quotas["claude"] = new UsageQuota { Period = Week };
        (DateTimeOffset start, DateTimeOffset actual) = Simulate(ledger, incidents, cycles: 2, into: TimeSpan.FromHours(55));
        DateTimeOffset now = _time.GetUtcNow();

        ConnectorForecast claude = Forecaster(ledger, incidents, configuration).Forecast(configuration).Connectors.Single();

        claude.PeriodSource.Should().Be("configured");
        claude.Limit.Cycles.Should().Be(2);
        claude.Limit.Source.Should().Be("learned");
        claude.NextReset.Should().Be(start + Week);
        claude.Backtest.Should().NotBeNull();
        claude.Backtest!.MeanRelativeError.Should().BeLessThan(Band);
        WithinBand(claude.RunsOutAt, actual, now);
    }

    [Fact]
    public void Without_history_it_says_so_with_no_confidence()
    {
        var ledger = new InMemoryUsageLedger();
        var incidents = new ConnectorIncidents(_time);
        RouterConfiguration configuration = Configuration();
        ledger.Record(Usage(_time.GetUtcNow().AddHours(-1), "claude-cli", "claude", 5_000, "coding"));

        QuotaForecast forecast = Forecaster(ledger, incidents, configuration).Forecast(configuration);

        ConnectorForecast claude = forecast.Connectors.Should().ContainSingle("only the subscription is forecast, not the HTTP connectors").Subject;
        claude.Status.Should().Be(QuotaStatus.Unknown);
        claude.LimitHits.Should().Be(0);
        claude.Limit.Value.Should().BeNull();
        claude.Limit.Cycles.Should().Be(0);
        claude.Limit.Confidence.Should().Be("none");
        claude.RunsOutAt.Should().BeNull();
        claude.Backtest.Should().BeNull();
        claude.PacePerHour.Should().BeGreaterThan(0);
        claude.Notes.Should().ContainSingle().Which.Should().StartWith("No usage limit hit recorded yet");
        claude.Summary.Should().Contain("No usage limit hit recorded yet");
        forecast.Budgets.Should().BeEmpty();
    }

    [Fact]
    public void A_limit_set_by_hand_overrides_the_learned_one_and_a_cold_subscription_lists_routes_to_move_up()
    {
        var ledger = new InMemoryUsageLedger();
        var incidents = new ConnectorIncidents(_time);
        RouterConfiguration configuration = Configuration();
        configuration.Usage.Quotas["claude"] = new UsageQuota { MaxTokens = 3_000_000 };
        Simulate(ledger, incidents, cycles: 4, into: TimeSpan.FromHours(43));
        DateTimeOffset now = _time.GetUtcNow();
        for (int i = 1; i <= 20; i++)
        {
            ledger.Record(Usage(now.AddHours(-i), "local", "lmstudio", 1_000, "smalltalk", client: "phone"));
        }

        ConnectorForecast claude = Forecaster(ledger, incidents, configuration).Forecast(configuration).Connectors.Single();

        claude.Limit.Source.Should().Be("configured");
        claude.Limit.Confidence.Should().Be("configured");
        claude.Limit.Value.Should().Be(3_000_000);
        claude.Limit.Learned!.Value.Should().BeInRange(TrueLimit * 0.98, TrueLimit, "the learned limit is still reported");
        claude.Status.Should().Be(QuotaStatus.Cold, "at this pace about half of three million tokens is left at the reset");
        (1 - claude.ShareAtReset!.Value).Should().BeGreaterThan(QuotaForecaster.ColdShare);
        claude.Summary.Should().Contain("unused");

        QuotaUpgrade upgrade = claude.Upgrades.Should().ContainSingle().Subject;
        upgrade.Route.Should().Be("smalltalk");
        upgrade.Requests.Should().Be(20);
        upgrade.Fits.Should().BeTrue();
        upgrade.ServedBy.Should().Equal("local");
        upgrade.MoveTo.Should().BeEquivalentTo(new QuotaMove { Route = "smalltalk", FromTier = 1, Tier = 4, Model = "claude-cli", Local = false });
    }

    [Fact]
    public void While_out_of_its_limit_it_says_until_when()
    {
        var ledger = new InMemoryUsageLedger();
        var incidents = new ConnectorIncidents(_time);
        RouterConfiguration configuration = Configuration();
        DateTimeOffset reset = _time.GetUtcNow().AddHours(3);
        incidents.Failed("claude", ModelFailureKind.QuotaExceeded, "You've hit your session limit · resets 3pm", reset);

        ConnectorForecast claude = Forecaster(ledger, incidents, configuration).Forecast(configuration).Connectors.Single();

        claude.Status.Should().Be(QuotaStatus.Limited);
        claude.LimitedUntil.Should().Be(reset);
        claude.NextReset.Should().Be(reset);
        claude.Summary.Should().StartWith("claude is out of its limit until about ");
    }

    [Fact]
    public void A_budget_on_pace_to_be_spent_before_the_period_ends_runs_hot()
    {
        var ledger = new InMemoryUsageLedger();
        var incidents = new ConnectorIncidents(_time);
        RouterConfiguration configuration = Configuration();
        configuration.Usage.Budgets.Add(new BudgetDefinition { Name = "daily", Connector = "openai", MaxCostUsd = 1m });
        DateTimeOffset now = _time.GetUtcNow(); // noon UTC
        for (int i = 0; i < 6; i++)
        {
            ledger.Record(Usage(now.AddHours(-i - 0.5), "gpt", "openai", 10_000, "coding", cost: 0.1m));
        }

        BudgetForecast budget = Forecaster(ledger, incidents, configuration).Forecast(configuration).Budgets.Single();

        budget.Name.Should().Be("daily");
        budget.Unit.Should().Be(QuotaUnit.Usd);
        budget.Used.Should().BeApproximately(0.6, 0.0001);
        budget.Limit.Should().Be(1);
        budget.Status.Should().Be(QuotaStatus.Hot);
        budget.RunsOutAt.Should().BeBefore(budget.PeriodEnd);
        budget.ShareAtEnd.Should().BeGreaterThan(1);
    }

    [Fact]
    public void Limit_episodes_group_the_refusals_until_the_connector_serves_again_and_survive_a_restart()
    {
        var store = new Values();
        using var incidents = new ConnectorIncidents(_time, store);
        DateTimeOffset reset = _time.GetUtcNow().AddHours(5);
        incidents.Failed("claude", ModelFailureKind.QuotaExceeded, "limit", null);
        _time.Advance(TimeSpan.FromMinutes(15));
        incidents.Failed("claude", ModelFailureKind.QuotaExceeded, "limit, resets 5pm", reset);
        incidents.Failed("claude", ModelFailureKind.Unavailable, "crashed", null);
        _time.Advance(TimeSpan.FromHours(6));
        incidents.Served("claude");
        DateTimeOffset served = _time.GetUtcNow();
        _time.Advance(TimeSpan.FromDays(1));
        incidents.Failed("claude", ModelFailureKind.QuotaExceeded, "limit", reset.AddDays(30)); // past 8 days: not believed
        incidents.Flush(TimeSpan.FromSeconds(10)).Should().BeTrue();

        using var restarted = new ConnectorIncidents(_time, store);
        IReadOnlyList<LimitEpisode> episodes = restarted.LimitEpisodes("claude");

        episodes.Should().HaveCount(2);
        episodes[0].ResetsAt.Should().Be(reset);
        episodes[0].ServedAgain.Should().Be(served);
        episodes[0].Reset.Should().Be(reset, "the reported reset came before the first request served");
        episodes[0].LastHit.Should().Be(episodes[0].Hit.AddMinutes(15));
        episodes[1].ResetsAt.Should().BeNull();
        episodes[1].ServedAgain.Should().BeNull();
        store.Saved.Should().Contain("\"episodes\"").And.NotContain("\"reset\"", "the derived reset is not stored");
    }

    [Fact]
    public void State_saved_before_limit_episodes_still_loads()
    {
        var store = new Values();
        DateTimeOffset hit = _time.GetUtcNow().AddDays(-1);
        store.Save(ConnectorIncidents.StoreKey, $$$"""{"claude":{"failingSince":null,"detail":null,"limitHits":["{{{hit:O}}}"],"limitDetail":"limit"}}""");

        using var incidents = new ConnectorIncidents(_time, store);

        incidents.LimitDays().Should().ContainSingle().Which.Days.Should().Be(1);
        incidents.LimitEpisodes("claude").Should().BeEmpty("the days-only history has no reset times to learn from");
    }

    [Fact]
    public void Quotas_are_validated()
    {
        RouterConfiguration configuration = Configuration();
        configuration.Usage.Quotas["ghost"] = new UsageQuota { MaxTokens = 10 };
        configuration.Usage.Quotas["claude"] = new UsageQuota { MaxTokens = 10, MaxRequests = 5, Period = TimeSpan.FromMinutes(5) };
        configuration.Usage.Quotas["lmstudio"] = new UsageQuota { MaxRequests = 0, PeriodStart = DateTimeOffset.UnixEpoch };
        configuration.Usage.Quotas["openai"] = new UsageQuota();

        configuration.GetDocumentValidationErrors().Should()
            .Contain("Usage:Quotas:ghost names a connector that is not configured.").And
            .Contain("Usage:Quotas:claude sets MaxTokens and MaxRequests: set one.").And
            .Contain("Usage:Quotas:openai needs MaxTokens, MaxRequests or Period.").And
            .Contain("Usage:Quotas:claude:Period must be at least an hour.").And
            .Contain("Usage:Quotas:lmstudio limit must be at least 1.").And
            .Contain("Usage:Quotas:lmstudio:PeriodStart needs a Period.");
        RouterConfiguration.FromJson(configuration.ToJson()).Usage.Quotas["CLAUDE"].Period.Should().Be(TimeSpan.FromMinutes(5), "the names ignore case after a round trip");
    }

    [Fact]
    public async Task The_admin_api_serves_the_forecast()
    {
        HttpClient client = await StartAsync(withCli: true, services: s => s.AddSingleton<TimeProvider>(_time));
        Simulate(Services.GetRequiredService<IUsageLedger>(), Services.GetRequiredService<ConnectorIncidents>(), cycles: 3, into: TimeSpan.FromDays(1));

        JsonElement forecast = JsonDocument.Parse(await client.GetStringAsync("/router/api/forecast")).RootElement;

        forecast.GetProperty("whatIf").GetString().Should().Be("#/whatif");
        forecast.GetProperty("budgets").GetArrayLength().Should().Be(0);
        JsonElement claude = forecast.GetProperty("connectors").EnumerateArray().Should().ContainSingle().Subject;
        claude.GetProperty("connector").GetString().Should().Be("claude");
        claude.GetProperty("status").GetString().Should().Be("Hot");
        claude.GetProperty("unit").GetString().Should().Be("Tokens");
        claude.GetProperty("period").GetString().Should().Be("7.00:00:00");
        claude.GetProperty("limit").GetProperty("source").GetString().Should().Be("learned");
        claude.GetProperty("limit").GetProperty("cycles").GetInt32().Should().Be(3);
        claude.GetProperty("backtest").GetProperty("meanRelativeError").GetDouble().Should().BeLessThan(Band);
        claude.GetProperty("runsOutAt").GetDateTimeOffset().Should().BeBefore(claude.GetProperty("nextReset").GetDateTimeOffset());
        claude.GetProperty("routes")[0].GetProperty("key").GetString().Should().Be("coding");
    }

    private sealed class Values : IStateValueStore
    {
        private readonly Dictionary<string, string> _values = [];
        private readonly Lock _gate = new();

        public string Saved
        {
            get
            {
                lock (_gate)
                {
                    return string.Join("\n", _values.Values);
                }
            }
        }

        public string? Load(string key)
        {
            lock (_gate)
            {
                return _values.GetValueOrDefault(key);
            }
        }

        public void Save(string key, string value)
        {
            lock (_gate)
            {
                _values[key] = value;
            }
        }
    }
}
