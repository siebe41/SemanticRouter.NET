using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>Reading limit reset times from provider messages.</summary>
public sealed class QuotaResetParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 13, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("You've hit your session limit · resets 3:45pm", "2026-10-08T15:45:00Z")]
    [InlineData("Usage limit reached. resets 9am", "2026-10-09T09:00:00Z")]
    [InlineData("limit resets at 17:30", "2026-10-08T17:30:00Z")]
    [InlineData("You've hit your weekly limit · resets Oct 10, 3pm", "2026-10-10T15:00:00Z")]
    [InlineData("Please try again in 2 hours 30 minutes.", "2026-10-08T15:30:00Z")]
    [InlineData("Rate limit reached. Please try again in 4h12m.", "2026-10-08T17:12:00Z")]
    [InlineData("You've hit your usage limit. Try again in 1 day 2 hours.", "2026-10-09T15:00:00Z")]
    [InlineData("retry after 45s", "2026-10-08T13:00:45Z")]
    public void Reads_reset_times_in_the_forms_vendors_print(string message, string expected)
    {
        QuotaResetParser.TryParse(message, Now, TimeZoneInfo.Utc, out DateTimeOffset reset).Should().BeTrue();
        reset.Should().Be(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Honours_a_named_time_zone_and_ignores_messages_without_a_time()
    {
        QuotaResetParser.TryParse("resets 3pm (Europe/Amsterdam)", Now, TimeZoneInfo.Utc, out DateTimeOffset amsterdam).Should().BeTrue();
        amsterdam.Should().Be(new DateTimeOffset(2026, 10, 8, 13, 0, 0, TimeSpan.Zero).AddDays(1), "3pm CEST today has passed at 13:00 UTC (15:00 local), so tomorrow");
        QuotaResetParser.TryParse("Error: quota exceeded", Now, TimeZoneInfo.Utc, out _).Should().BeFalse();
        QuotaResetParser.TryParse("resets 3 counters", Now, TimeZoneInfo.Utc, out _).Should().BeFalse();
        QuotaResetParser.TryParseRetryAfter("120", Now, out DateTimeOffset retry).Should().BeTrue();
        retry.Should().Be(Now.AddMinutes(2));
    }

    [Fact]
    public void A_parsed_reset_replaces_the_fixed_quota_cooldown_within_limits()
    {
        var time = new ManualTimeProvider();
        var tracker = new ModelHealthTracker(new ModelSelectionOptions { QuotaCooldown = TimeSpan.FromMinutes(15) }, time);
        ModelDefinition claude = Model("claude", 4);
        ModelDefinition codex = Model("codex", 4);
        ModelDefinition other = Model("other", 4);

        tracker.ReportFailure(claude, ModelFailureKind.QuotaExceeded, time.GetUtcNow().AddHours(3));
        tracker.ReportFailure(codex, ModelFailureKind.QuotaExceeded, null);
        tracker.ReportFailure(other, ModelFailureKind.QuotaExceeded, time.GetUtcNow().AddDays(30));

        tracker.GetCooldowns()["claude"].Should().Be(time.GetUtcNow().AddHours(3));
        tracker.GetCooldowns()["codex"].Should().Be(time.GetUtcNow().AddMinutes(15), "no reset time: the configured cooldown");
        tracker.GetCooldowns()["other"].Should().Be(time.GetUtcNow().AddMinutes(15), "an implausible reset falls back too");
    }
}

/// <summary>Usage, cost, budgets and telemetry through the running router.</summary>
public sealed class UsageAndTelemetryTests : RouterHostTest
{
    private const string OpenAIReply = """{"id":"c1","object":"chat.completion","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"prompt_tokens_details":{"cached_tokens":4}}}""";
    private const string AnthropicJson = """{"model":"claude-sonnet-5-5","max_tokens":10,"messages":[{"role":"user","content":"hello there"}]}""";

    private readonly CapturingLoggerProvider _logs = new();

    private Task<HttpClient> StartDispatchAsync(Action<IServiceCollection>? services = null, Action<SemanticRouterOptions>? routing = null, bool withCli = false) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
            },
            routing,
            services: s =>
            {
                s.AddSingleton<ILoggerProvider>(_logs);
                services?.Invoke(s);
            },
            withCli: withCli);

    private static StringContent ChatFor(string model) => new($$"""{"model":"{{model}}","messages":[{"role":"user","content":"hello there"}]}""", Encoding.UTF8, "application/json");

    private static async Task<JsonNode> ReadNodeAsync(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    [Fact]
    public async Task Usage_from_every_backend_shape_adds_up_on_the_dashboard()
    {
        LocalHttp = StubHttpMessageHandler.Json(OpenAIReply);
        CloudHttp = StubHttpMessageHandler.Json(OpenAIReply);
        AnthropicHttp = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"m\",\"usage\":{\"input_tokens\":20,\"cache_read_input_tokens\":100,\"output_tokens\":1}}}\n\n" +
                "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}\n\n" +
                "event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":7}}\n\n" +
                "event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n",
                Encoding.UTF8, "text/event-stream"),
        });
        HttpClient client = await StartDispatchAsync(
            services: s => s.Configure<SemanticRouterConnectorsOptions>(o => o.Connectors["anthropic-native"] = new ConnectorOptions
            {
                Type = ConnectorType.Anthropic, BaseAddress = new Uri("https://api.anthropic.example"), ApiKey = "sk-ant-test",
            }),
            routing: o =>
            {
                o.Models.Add(Model("native", 5, costPerMillion: 10m, connector: "anthropic-native"));
                o.Models.Add(Model("reference", 5, costPerMillion: 100m, connector: "anthropic-native") is { } r ? Disabled(r) : null!);
            },
            withCli: true);
        Services.GetRequiredService<RouterRuntimeProvider>().Current.Configuration.Usage.ReferenceModel = "reference";

        (await client.PostAsync("/v1/chat/completions", ChatFor("local"))).StatusCode.Should().Be(HttpStatusCode.OK);          // OpenAI pass-through, JSON
        (await client.PostAsync("/v1/messages", Json(AnthropicJson.Replace("claude-sonnet-5-5", "cloud", StringComparison.Ordinal)))).StatusCode.Should().Be(HttpStatusCode.OK); // translated
        (await client.PostAsync("/v1/messages", Json(AnthropicJson.Replace("claude-sonnet-5-5", "native", StringComparison.Ordinal).Replace("\"max_tokens\":10", "\"max_tokens\":10,\"stream\":true", StringComparison.Ordinal)))).StatusCode.Should().Be(HttpStatusCode.OK); // Anthropic pass-through, SSE
        (await client.PostAsync("/v1/chat/completions", ChatFor("claude-cli"))).StatusCode.Should().Be(HttpStatusCode.OK);     // CLI

        RoutingActivityEntry[] entries = [.. Services.GetRequiredService<RoutingActivityLog>().Recent().OrderBy(e => e.Id)];
        entries.Select(e => (e.ServedBy, e.InputTokens, e.OutputTokens, e.CacheReadTokens)).Should().Equal(
            ("local", 6, 5, 4), ("cloud", 6, 5, 4), ("native", 20, 7, 100), ("claude-cli", 12, 3, null));
        entries[1].CostUsd.Should().Be((6 + 5 + 4) * 3m / 1_000_000m);
        entries[2].CostUsd.Should().Be((20 + 7 + 100) * 10m / 1_000_000m);

        JsonNode usage = await ReadNodeAsync(await client.GetAsync("/router/api/usage?window=day&by=model"));
        JsonNode totals = usage["totals"]!;
        totals["requests"]!.GetValue<long>().Should().Be(4);
        totals["inputTokens"]!.GetValue<long>().Should().Be(6 + 6 + 20 + 12);
        totals["outputTokens"]!.GetValue<long>().Should().Be(5 + 5 + 7 + 3);
        totals["cacheReadTokens"]!.GetValue<long>().Should().Be(4 + 4 + 100);
        totals["costUsd"]!.GetValue<decimal>().Should().Be(entries.Sum(e => e.CostUsd ?? 0));
        totals["referenceCostUsd"]!.GetValue<decimal>().Should().Be((15 + 15 + 127 + 15) * 100m / 1_000_000m, "every request priced at the reference model, cache tokens as input");
        usage["groups"]!.AsArray().Select(g => g!["key"]!.GetValue<string>()).Should().BeEquivalentTo("local", "cloud", "native", "claude-cli");
    }

    private static ModelDefinition Disabled(ModelDefinition model)
    {
        model.Enabled = false;
        return model;
    }

    [Fact]
    public async Task A_spent_budget_stops_the_model_and_a_request_only_it_can_serve_gets_a_503_naming_it()
    {
        ClassifyAs(tier: 4);
        CloudHttp = StubHttpMessageHandler.Json(OpenAIReply);
        LocalHttp = StubHttpMessageHandler.Json(OpenAIReply);
        HttpClient client = await StartDispatchAsync(services: s => s.Configure<UsageOptions>(o =>
        {
            o.TimeZone = "UTC";
            o.Budgets.Add(new BudgetDefinition { Name = "daily-cloud", Model = "cloud", MaxTokens = 10 });
        }));

        HttpResponseMessage first = await client.PostAsync("/v1/chat/completions", Json(ChatJson));
        HttpResponseMessage second = await client.PostAsync("/v1/chat/completions", Json(ChatJson));
        HttpResponseMessage forced = await client.PostAsync("/v1/chat/completions", ChatFor("cloud"));

        first.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("cloud");
        second.Headers.GetValues("X-Prompt-Route-Model").Should().Equal(["local"], "the spent budget keeps cloud from being tried");
        second.Headers.GetValues("X-Prompt-Route-Budget").Should().Equal("daily-cloud");
        second.Headers.GetValues("X-Prompt-Route-Degraded").Should().Equal("true");
        CloudHttp.Requests.Should().HaveCount(1);
        forced.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await forced.Content.ReadAsStringAsync()).Should().Contain("budget 'daily-cloud' is spent").And.Contain("15 of 10 tokens this day");
        _logs.All.Should().Contain("Budget daily-cloud (model cloud) is spent");

        JsonNode budget = (await ReadNodeAsync(await client.GetAsync("/router/api/status")))["budgets"]![0]!;
        budget["exceeded"]!.GetValue<bool>().Should().BeTrue();
        budget["percent"]!.GetValue<int>().Should().Be(150);
    }

    [Fact]
    public void Budgets_are_validated_and_warn_before_the_cap()
    {
        var document = new RouterConfiguration();
        document.Models.Add(Model("m", 1, connector: "c"));
        document.Connectors["c"] = new ConnectorOptions { BaseAddress = new Uri("http://localhost:1") };
        document.Usage.Budgets.Add(new BudgetDefinition { Name = "both", Model = "m", Connector = "c", MaxTokens = 1 });
        document.Usage.Budgets.Add(new BudgetDefinition { Name = "none", Model = "m" });
        document.Usage.Budgets.Add(new BudgetDefinition { Name = "ghost", Model = "ghost", MaxCostUsd = 1 });
        document.Usage.ReferenceModel = "nope";

        document.GetDocumentValidationErrors().Should()
            .Contain(e => e.Contains("'both' must name exactly one")).And
            .Contain(e => e.Contains("'none' needs MaxTokens or MaxCostUsd")).And
            .Contain(e => e.Contains("'ghost' covers model 'ghost'")).And
            .Contain(e => e.Contains("ReferenceModel 'nope'"));

        var ledger = new InMemoryUsageLedger();
        var time = new ManualTimeProvider();
        using var factory = LoggerFactory.Create(b => b.AddProvider(_logs));
        var gate = new BudgetGate(ledger, time, factory.CreateLogger<BudgetGate>());
        var usage = new UsageOptions { TimeZone = "UTC" };
        usage.Budgets.Add(new BudgetDefinition { Name = "monthly", Connector = "c", Period = BudgetPeriod.Month, MaxCostUsd = 10 });
        ledger.Record(new UsageRecord(time.GetUtcNow(), "m", "c", null, null, false, 0, 0, 0, 0, 8.5m, 8.5m));

        BudgetStatus status = gate.Status(usage).Single();
        status.Should().Match<BudgetStatus>(s => s.Warning && !s.Exceeded && s.Percent == 85);
        status.PeriodEnd.Should().Be(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
        gate.Blocking(Model("m", 1, connector: "c"), usage).Should().BeNull();
        _logs.All.Should().Contain("Budget monthly (connector c) is at 85%");
    }

    [Fact]
    public async Task A_429_with_retry_after_cools_the_model_until_then()
    {
        ClassifyAs(tier: 4);
        CloudHttp = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { { "Retry-After", "3600" } } });
        HttpClient client = await StartDispatchAsync();

        await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        DateTimeOffset until = Services.GetRequiredService<IModelHealth>().GetCooldowns()["cloud"];
        until.Should().BeCloseTo(DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_cli_limit_message_with_a_reset_time_cools_every_model_on_it_until_the_reset()
    {
        ClassifyAs(tier: 4);
        Cli = FakeCliProcessRunner.Failing(1, stderr: "You've hit your session limit · resets 11:59pm (UTC)");
        HttpClient client = await StartDispatchAsync(withCli: true);

        await client.PostAsync("/v1/chat/completions", ChatFor("claude-cli"));

        DateTimeOffset until = Services.GetRequiredService<IModelHealth>().GetCooldowns()["claude-cli"];
        until.TimeOfDay.Should().Be(new TimeSpan(23, 59, 0));
        until.Should().BeAfter(DateTimeOffset.UtcNow).And.BeBefore(DateTimeOffset.UtcNow.AddDays(1).AddMinutes(1));
    }

    [Fact]
    public async Task A_failed_over_request_is_one_trace_with_sibling_attempts_and_no_prompt_text()
    {
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SemanticRouterDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (spans) { spans.Add(activity); } },
        };
        ActivitySource.AddActivityListener(listener);
        ClassifyAs(tier: 1);
        LocalHttp = StubHttpMessageHandler.Throws();
        CloudHttp = StubHttpMessageHandler.Json(OpenAIReply);
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson.Replace("hello there", "my secret plan", StringComparison.Ordinal)));

        // The listener is process-wide: other tests' spans arrive too, so keep this request's trace only.
        string traceId = Services.GetRequiredService<RoutingActivityLog>().Recent().Single().TraceId!;
        Activity route;
        Activity[] attempts;
        lock (spans)
        {
            route = spans.Single(s => s.OperationName == "semantic_router.route" && s.TraceId.ToHexString() == traceId);
            attempts = [.. spans.Where(s => s.OperationName == "semantic_router.attempt" && s.TraceId == route.TraceId).OrderBy(s => s.StartTimeUtc)];
        }

        attempts.Should().HaveCount(2);
        attempts.Should().OnlyContain(a => a.ParentSpanId == route.SpanId && a.TraceId == route.TraceId);
        attempts[0].GetTagItem("semantic_router.model").Should().Be("local");
        attempts[0].GetTagItem("semantic_router.failure.kind").Should().Be("Unavailable");
        attempts[0].Status.Should().Be(ActivityStatusCode.Error);
        attempts[1].GetTagItem("semantic_router.outcome").Should().Be("Completed");
        route.GetTagItem("semantic_router.served_by").Should().Be("cloud");

        lock (spans)
        {
            spans.Where(s => s.TraceId == route.TraceId).SelectMany(s => s.TagObjects).Should().NotContain(t => (t.Value as string ?? string.Empty).Contains("secret plan"));
        }

        long id = long.Parse(response.Headers.GetValues("X-Prompt-Route-Id").Single(), System.Globalization.CultureInfo.InvariantCulture);
        RoutingActivityEntry entry = Services.GetRequiredService<RoutingActivityLog>().Recent().Single();
        entry.Id.Should().Be(id);
        entry.TraceId.Should().Be(route.TraceId.ToHexString());
    }

    [Fact]
    public async Task Backend_requests_carry_the_routers_trace_context()
    {
        // A real listener: trace propagation happens in HttpClientHandler, which a stub handler would replace.
        int port;
        using (var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        // http.sys lets any user listen on "localhost"; other host names need a URL reservation on Windows.
        string host = OperatingSystem.IsWindows() ? "localhost" : "127.0.0.1";
        using var backend = new HttpListener { Prefixes = { $"http://{host}:{port}/" } };
        backend.Start();
        string? traceparent = null;
        Task serve = Task.Run(async () =>
        {
            HttpListenerContext request = await backend.GetContextAsync();
            traceparent = request.Request.Headers["traceparent"];
            byte[] reply = Encoding.UTF8.GetBytes(OpenAIReply);
            request.Response.ContentType = "application/json";
            await request.Response.OutputStream.WriteAsync(reply);
            request.Response.Close();
        });

        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SemanticRouterDiagnostics.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { lock (spans) { spans.Add(activity); } },
        };
        ActivitySource.AddActivityListener(listener);
        HttpClient client = await StartDispatchAsync(
            services: s => s.Configure<SemanticRouterConnectorsOptions>(o => o.Connectors["real"] = new ConnectorOptions { BaseAddress = new Uri($"http://{host}:{port}") }),
            routing: o => o.Models.Add(Model("real-model", 1, connector: "real")));

        (await client.PostAsync("/v1/chat/completions", ChatFor("real-model"))).StatusCode.Should().Be(HttpStatusCode.OK);
        await serve;

        Activity attempt;
        lock (spans)
        {
            attempt = spans.Single(s => s.OperationName == "semantic_router.attempt" && (string?)s.GetTagItem("semantic_router.model") == "real-model");
        }

        traceparent.Should().NotBeNull().And.Contain(attempt.TraceId.ToHexString(), "the backend call joins the router's trace");
    }

    [Fact]
    public async Task Metrics_report_tokens_cost_failovers_and_time_to_first_byte()
    {
        var measurements = new List<(string Instrument, double Value, Dictionary<string, object?> Tags, object? Scope)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == SemanticRouterDiagnostics.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            lock (measurements)
            {
                measurements.Add((instrument.Name, value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value), instrument.Meter.Scope));
            }
        }

        listener.SetMeasurementEventCallback<long>((i, v, t, _) => Record(i, v, t));
        listener.SetMeasurementEventCallback<double>((i, v, t, _) => Record(i, v, t));
        listener.Start();
        ClassifyAs(tier: 1);
        LocalHttp = StubHttpMessageHandler.Throws();
        CloudHttp = StubHttpMessageHandler.Json(OpenAIReply);
        HttpClient client = await StartDispatchAsync();

        await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        // Each host has its own meter (scoped to its IMeterFactory); ignore the other tests' hosts.
        object factory = Services.GetRequiredService<IMeterFactory>();
        lock (measurements)
        {
            measurements.RemoveAll(m => !ReferenceEquals(m.Scope, factory));
            measurements.Where(m => m.Instrument == SemanticRouterDiagnostics.TokensInstrument && (string?)m.Tags["model"] == "cloud")
                .ToDictionary(m => (string)m.Tags["type"]!, m => m.Value)
                .Should().BeEquivalentTo(new Dictionary<string, double> { ["input"] = 6, ["output"] = 5, ["cache_read"] = 4 });
            measurements.Should().Contain(m => m.Instrument == SemanticRouterDiagnostics.CostInstrument && (string?)m.Tags["model"] == "cloud");
            measurements.Should().Contain(m => m.Instrument == SemanticRouterDiagnostics.FailoversInstrument && (string?)m.Tags["model"] == "local");
            measurements.Should().Contain(m => m.Instrument == SemanticRouterDiagnostics.CooldownsInstrument && (string?)m.Tags["kind"] == "Unavailable");
            measurements.Should().Contain(m => m.Instrument == SemanticRouterDiagnostics.TimeToFirstByteInstrument && (string?)m.Tags["model"] == "cloud");
            measurements.Should().Contain(m => m.Instrument == SemanticRouterDiagnostics.RequestDurationInstrument && (string?)m.Tags["outcome"] == "served");
        }
    }

    [Fact]
    public async Task Exports_omit_prompts_unless_asked_and_match_the_dashboards_percentiles()
    {
        HttpClient client = await StartDispatchAsync();
        for (int i = 0; i < 5; i++)
        {
            await client.PostAsync("/v1/chat/completions", Json(ChatJson));
        }

        string csv = await client.GetStringAsync("/router/api/activity/export?format=csv");
        string csvWithPrompts = await client.GetStringAsync("/router/api/activity/export?format=csv&prompts=true");
        string jsonl = await client.GetStringAsync("/router/api/activity/export?format=jsonl");
        JsonNode series = await ReadNodeAsync(await client.GetAsync("/router/api/activity/series?window=1h"));

        csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Should().HaveCount(6);
        csv.Should().StartWith("id,timestamp,protocol").And.NotContain("hello there").And.NotContain("prompt_preview");
        csvWithPrompts.Should().Contain("prompt_preview").And.Contain("hello there");
        string[] lines = jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(5).And.OnlyContain(l => !l.Contains("promptPreview"));

        double[] latencies = [.. lines.Select(l => JsonNode.Parse(l)!["totalMilliseconds"]!.GetValue<double>()).Order()];
        series["requests"]!.GetValue<int>().Should().Be(5);
        series["p50Milliseconds"]!.GetValue<double>().Should().Be(ActivityReports.Percentile(latencies, 50)!.Value);
        series["p95Milliseconds"]!.GetValue<double>().Should().Be(ActivityReports.Percentile(latencies, 95)!.Value);
        series["buckets"]!.AsArray().Should().HaveCount(60);
        series["buckets"]!.AsArray().Sum(b => b!["requests"]!.GetValue<int>()).Should().Be(5);
    }

    [Fact]
    public void Usage_survives_a_restart_in_sqlite()
    {
        string directory = Directory.CreateTempSubdirectory("router-usage").FullName;
        try
        {
            var options = Microsoft.Extensions.Options.Options.Create(new SqliteStateOptions { Path = Path.Combine(directory, "state.db") });
            using var factory = LoggerFactory.Create(_ => { });
            var first = new SqliteUsageLedger(new SqliteRouterDatabase(options, factory.CreateLogger<SqliteRouterDatabase>()));
            DateTimeOffset now = DateTimeOffset.UtcNow;
            first.Record(new UsageRecord(now, "cloud", "openai", "complex-code", "laptop", false, 100, 50, 10, 0, 0.0012m, 0.01m));
            first.Record(new UsageRecord(now, "local", "lmstudio", "smalltalk", null, true, 30, 20, 0, 0, 0m, 0.002m));

            var restarted = new SqliteUsageLedger(new SqliteRouterDatabase(options, factory.CreateLogger<SqliteRouterDatabase>()));
            UsageTotals totals = restarted.Total(now.AddMinutes(-1), now.AddMinutes(1));
            UsageTotals cloud = restarted.Total(now.AddMinutes(-1), now.AddMinutes(1), model: "cloud");

            totals.Should().Be(new UsageTotals(2, 130, 70, 10, 0, 0.0012m, 0.012m));
            totals.SavedUsd.Should().Be(0.0108m);
            cloud.Requests.Should().Be(1);
            restarted.Summarize(now.AddMinutes(-1), now.AddMinutes(1), UsageGrouping.Client).Select(g => g.Key).Should().BeEquivalentTo("laptop", "(no key)");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
