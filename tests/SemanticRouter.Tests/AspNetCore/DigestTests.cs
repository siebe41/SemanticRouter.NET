using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SemanticRouter.AspNetCore;
using SemanticRouter.Desktop;
using SemanticRouter.Tests.Support;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// The weekly digest and notifications (#49): the digest adds up the same requests as the Usage page, each kind of
/// notice has a test that raises it, and a healthy week raises none.
/// </summary>
public sealed class DigestTests : RouterHostTest
{
    private const string Good = """{"id":"r","object":"chat.completion","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1000,"completion_tokens":200}}""";
    private const string Empty = """{"id":"r","object":"chat.completion","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":""},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":0}}""";

    private readonly ManualTimeProvider _time = new();
    private bool _localDown;

    public DigestTests()
    {
        _time.Advance(DateTimeOffset.UtcNow - _time.GetUtcNow());

        // The local backend answers chat, answers agentic tasks with nothing, and can be taken down.
        LocalHttp = new StubHttpMessageHandler((_, body) => _localDown
            ? throw new HttpRequestException("Connection refused")
            : Reply(body.Contains("task", StringComparison.Ordinal) ? Empty : Good));
        CloudHttp = new StubHttpMessageHandler((_, _) => Reply(Good));
        Classifier.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string prompt, CancellationToken _) => new PromptClassificationResult
            {
                RequiredTier = prompt.StartsWith("hard", StringComparison.Ordinal) ? 4 : 1,
                Reason = prompt.StartsWith("hard", StringComparison.Ordinal) ? RoutingReason.ComplexityEscalation : RoutingReason.SemanticMatch,
                RouteName = prompt.StartsWith("task", StringComparison.Ordinal) ? "agentic" : "chat",
                Similarity = 0.9f,
                Complexity = new ComplexityAnalysis { Score = 0.1, EstimatedTokens = 3 },
            });
    }

    private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private Task<HttpClient> StartDispatchAsync(Action<IServiceCollection>? services = null, Action<TuningOptions>? tuning = null) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
                o.Reliability.MinimumTurns = 4;
            },
            routing: o =>
            {
                o.Routes.Add(Route("agentic", 1, "implement this feature"));
                o.Routes.Add(Route("chat", 1, "hello there"));
            },
            services: s =>
            {
                s.AddSingleton<TimeProvider>(_time);
                s.Configure<UsageOptions>(u =>
                {
                    u.ReferenceModel = "cloud";
                    u.TimeZone = "UTC";
                });
                if (tuning is not null)
                {
                    s.Configure(tuning);
                }

                services?.Invoke(s);
            });

    private RouterNotices Notices => Services.GetRequiredService<RouterNotices>();

    private static async Task<HttpStatusCode> AskAsync(HttpClient client, string prompt)
    {
        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(JsonSerializer.Serialize(new { model = "auto", messages = new[] { new { role = "user", content = prompt } } }), Encoding.UTF8, "application/json"));
        return response.StatusCode;
    }

    private static async Task<JsonElement> JsonOf(HttpClient client, string path)
    {
        HttpResponseMessage response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    /// <summary>A healthy week: chat on the local model, hard prompts on the cloud model, nothing failing.</summary>
    private static async Task HealthyWeekAsync(HttpClient client)
    {
        for (int i = 0; i < 6; i++)
        {
            (await AskAsync(client, $"hello number {i}")).Should().Be(HttpStatusCode.OK);
        }

        for (int i = 0; i < 2; i++)
        {
            (await AskAsync(client, $"hard question {i}")).Should().Be(HttpStatusCode.OK);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("week")]
    [InlineData("30d")]
    public async Task The_digest_adds_up_the_same_requests_as_the_usage_page(string? window)
    {
        HttpClient client = await StartDispatchAsync();
        await HealthyWeekAsync(client);
        string query = window is null ? string.Empty : "?window=" + window;

        JsonElement digest = await JsonOf(client, "/router/api/digest" + query);
        JsonElement usage = await JsonOf(client, "/router/api/usage" + query + (window is null ? "?" : "&") + "by=location");

        JsonElement totals = usage.GetProperty("totals");
        digest.GetProperty("since").GetDateTimeOffset().Should().BeCloseTo(usage.GetProperty("since").GetDateTimeOffset(), TimeSpan.FromSeconds(5), "the same window, a moment apart");
        digest.GetProperty("served").GetInt64().Should().Be(totals.GetProperty("requests").GetInt64()).And.Be(8);
        digest.GetProperty("costUsd").GetDecimal().Should().Be(totals.GetProperty("costUsd").GetDecimal());
        digest.GetProperty("referenceCostUsd").GetDecimal().Should().Be(totals.GetProperty("referenceCostUsd").GetDecimal());
        digest.GetProperty("savedUsd").GetDecimal().Should().Be(totals.GetProperty("savedUsd").GetDecimal()).And.BeGreaterThan(0);
        long local = usage.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("key").GetString() == "local").GetProperty("totals").GetProperty("requests").GetInt64();
        digest.GetProperty("local").GetInt64().Should().Be(local).And.Be(6);
        digest.GetProperty("localShare").GetDouble().Should().Be(0.75);
        digest.GetProperty("prompts").GetInt32().Should().Be(8);
        digest.GetProperty("escalated").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task A_healthy_week_raises_no_notice()
    {
        HttpClient client = await StartDispatchAsync(s => s.Configure<UsageOptions>(u =>
            u.Budgets.Add(new BudgetDefinition { Name = "cloud-daily", Model = "cloud", MaxTokens = 1_000_000 })));
        await HealthyWeekAsync(client);
        _time.Advance(TimeSpan.FromHours(3));

        Notices.Current().Should().BeEmpty();
        Services.GetRequiredService<NoticeService>().Check().Appeared.Should().BeEmpty();
        JsonElement digest = await JsonOf(client, "/router/api/digest");
        digest.GetProperty("notices").GetArrayLength().Should().Be(0);
        digest.GetProperty("notify").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task A_connector_down_for_an_hour_needs_a_decision_and_clears_when_it_answers()
    {
        HttpClient client = await StartDispatchAsync();
        _localDown = true;
        (await AskAsync(client, "hello there")).Should().Be(HttpStatusCode.OK, "the cloud model takes over");

        _time.Advance(TimeSpan.FromMinutes(59));
        Notices.Current().Should().BeEmpty("not down for long yet");

        _time.Advance(TimeSpan.FromMinutes(2));
        RouterNotice notice = Notices.Current().Should().ContainSingle().Subject;
        (notice.Key, notice.Kind, notice.Link).Should().Be(("down:lmstudio", NoticeKind.ConnectorDown, "#/connectors"));
        notice.Title.Should().Be("lmstudio has been down for 61 minutes");
        notice.Detail.Should().Contain("Connection refused");
        notice.PlainDetail.Should().StartWith("Every request to it has failed since").And.NotContain("Connection refused", "webhooks leave out what the connector said");

        _localDown = false;
        (await client.PostAsync("/router/api/health/reset", null)).StatusCode.Should().Be(HttpStatusCode.OK, "clear the cooldown so the local model is tried");
        (await AskAsync(client, "hello again")).Should().Be(HttpStatusCode.OK);
        Notices.Current().Should().BeEmpty();
    }

    [Fact]
    public async Task A_subscription_that_keeps_hitting_its_limit_needs_a_decision()
    {
        await StartDispatchAsync();
        ConnectorIncidents incidents = Services.GetRequiredService<ConnectorIncidents>();

        incidents.Failed("anthropic", ModelFailureKind.QuotaExceeded, "usage limit reached, resets 3pm");
        _time.Advance(TimeSpan.FromDays(1));
        incidents.Failed("anthropic", ModelFailureKind.QuotaExceeded, "usage limit reached, resets 3pm");
        incidents.Failed("anthropic", ModelFailureKind.QuotaExceeded, "usage limit reached, resets 4pm");
        Notices.Current().Should().BeEmpty("two days is not yet a pattern");

        _time.Advance(TimeSpan.FromDays(1));
        incidents.Failed("anthropic", ModelFailureKind.QuotaExceeded, "usage limit reached, resets 5pm");
        RouterNotice notice = Notices.Current().Should().ContainSingle().Subject;
        (notice.Key, notice.Kind, notice.Title).Should().Be(("limit:anthropic", NoticeKind.LimitReached, "anthropic keeps hitting its usage limit"));
        notice.Detail.Should().Contain("3 of the last 7 days").And.Contain("resets 5pm");
        notice.PlainDetail.Should().Contain("3 of the last 7 days").And.NotContain("resets 5pm");
        Notices.Current().Should().NotContain(n => n.Kind == NoticeKind.ConnectorDown, "a usage limit does not make a connector down");

        _time.Advance(TimeSpan.FromDays(6));
        Notices.Current().Should().BeEmpty("the older days fell out of the week");
    }

    [Fact]
    public async Task A_budget_that_keeps_running_out_needs_a_decision()
    {
        await StartDispatchAsync(s => s.Configure<UsageOptions>(u =>
            u.Budgets.Add(new BudgetDefinition { Name = "cloud-daily", Model = "cloud", MaxTokens = 1000 })));
        IUsageLedger ledger = Services.GetRequiredService<IUsageLedger>();
        void Spend(DateTimeOffset at) => ledger.Record(new UsageRecord
        {
            Timestamp = at,
            Model = "cloud",
            Connector = "anthropic",
            IsLocal = false,
            InputTokens = 900,
            OutputTokens = 200,
            CacheReadTokens = 0,
            CacheWriteTokens = 0,
            CostUsd = 0.01m,
            ReferenceCostUsd = 0.01m,
        });

        Spend(_time.GetUtcNow());
        Notices.Current().Should().BeEmpty("spent once is a busy day");

        Spend(_time.GetUtcNow().AddDays(-2));
        RouterNotice notice = Notices.Current().Should().ContainSingle().Subject;
        (notice.Key, notice.Kind, notice.Link).Should().Be(("budget:cloud-daily", NoticeKind.LimitReached, "#/usage"));
        notice.Detail.Should().StartWith("It was spent in 2 of the last 4 days (model cloud;");
        notice.PlainDetail.Should().Be(notice.Detail, "a budget notice quotes no prompt or reply");
    }

    [Fact]
    public async Task A_model_unreliable_for_most_of_its_work_needs_a_decision()
    {
        HttpClient client = await StartDispatchAsync();
        for (int i = 0; i < 4; i++)
        {
            (await AskAsync(client, $"task {i}: wire the endpoint")).Should().Be(HttpStatusCode.OK);
        }

        RoutingFeedbackLog feedback = Services.GetRequiredService<RoutingFeedbackLog>();
        for (int wait = 0; wait < 100 && feedback.Recent().Count < 4; wait++)
        {
            await Task.Delay(20);
        }

        _time.Advance(TimeSpan.FromMinutes(1)); // the activity log stamps entries by the real clock
        RouterNotice notice = Notices.Current().Should().ContainSingle().Subject;
        (notice.Key, notice.Kind).Should().Be(("unreliable:local", NoticeKind.ModelUnreliable));
        notice.Detail.Should().StartWith("It is left out of 1 of the 1 kinds of work it does (route agentic)");
        notice.PlainDetail.Should().Be(notice.Detail, "the reliability detail is counts");
    }

    [Fact]
    public async Task A_model_unreliable_for_some_of_its_work_is_left_to_the_router()
    {
        HttpClient client = await StartDispatchAsync();
        for (int i = 0; i < 4; i++)
        {
            (await AskAsync(client, $"hello number {i}")).Should().Be(HttpStatusCode.OK);
            (await AskAsync(client, $"task {i}: wire the endpoint")).Should().Be(HttpStatusCode.OK);
        }

        RoutingFeedbackLog feedback = Services.GetRequiredService<RoutingFeedbackLog>();
        for (int wait = 0; wait < 100 && feedback.Recent().Count < 4; wait++)
        {
            await Task.Delay(20);
        }

        _time.Advance(TimeSpan.FromMinutes(1)); // the activity log stamps entries by the real clock
        Services.GetRequiredService<ModelReliabilityTracker>().Refresh().Should().Contain(r => r.Model == "local" && r.ExcludedUntil != null, "it is left out of agentic work");
        Notices.Current().Should().BeEmpty("chat, half of its work, it still does well");
    }

    [Fact]
    public async Task Tuning_changes_it_may_not_make_on_its_own_need_a_decision()
    {
        await StartDispatchAsync(tuning: t => t.Mode = TuningMode.Suggest);
        Services.GetRequiredService<ITuningStore>().Save(new TuningChange
        {
            Id = 1,
            Timestamp = _time.GetUtcNow(),
            Kind = TuningChangeKind.DefaultThreshold,
            OldValue = 0.75f,
            NewValue = 0.73f,
            State = TuningChangeState.Suggested,
        });

        RouterNotice notice = Notices.Current().Should().ContainSingle().Subject;
        (notice.Key, notice.Kind, notice.Title, notice.Link).Should().Be(("tuning:suggestions", NoticeKind.TuningSuggestion, "Automatic tuning has a suggestion", "#/learned"));
        notice.Detail.Should().Contain("Tuning:Mode is Suggest").And.Contain("0.75 → 0.73");
        notice.PlainDetail.Should().Be(notice.Detail, "a threshold change quotes no prompt");
    }

    [Fact]
    public async Task A_notice_is_announced_once_and_once_more_when_it_clears()
    {
        await StartDispatchAsync(tuning: t => t.Mode = TuningMode.Suggest);
        NoticeService service = Services.GetRequiredService<NoticeService>();
        ITuningStore store = Services.GetRequiredService<ITuningStore>();
        var change = new TuningChange { Id = 1, Timestamp = _time.GetUtcNow(), Kind = TuningChangeKind.DefaultThreshold, OldValue = 0.75f, NewValue = 0.73f, State = TuningChangeState.Suggested };
        store.Save(change);

        service.Check().Appeared.Select(n => n.Key).Should().Equal("tuning:suggestions");
        service.Check().Appeared.Should().BeEmpty("it was announced already");

        store.Save(change with { State = TuningChangeState.Dismissed });
        (IReadOnlyList<RouterNotice> appeared, IReadOnlyList<RouterNotice> cleared) = service.Check();
        appeared.Should().BeEmpty();
        cleared.Select(n => n.Key).Should().Equal("tuning:suggestions");
    }

    [Fact]
    public async Task The_switch_silences_notifications_but_the_dashboard_still_lists_them()
    {
        HttpClient client = await StartDispatchAsync(s => s.Configure<NotificationOptions>(n => n.Enabled = false), t => t.Mode = TuningMode.Suggest);
        Services.GetRequiredService<ITuningStore>().Save(new TuningChange
        {
            Id = 1,
            Timestamp = _time.GetUtcNow(),
            Kind = TuningChangeKind.DefaultThreshold,
            OldValue = 0.75f,
            NewValue = 0.73f,
            State = TuningChangeState.Suggested,
        });

        string json = await client.GetStringAsync("/router/api/digest");
        TrayDigest tray = TrayDigest.Parse(json);

        tray.Notify.Should().BeFalse();
        tray.Notices.Should().ContainSingle();
        tray.New(new HashSet<string>()).Should().BeEmpty("the desktop app shows no notification when they are off");
    }

    [Fact]
    public void The_tray_shows_the_week_what_was_learned_and_what_needs_a_decision()
    {
        TrayDigest tray = TrayDigest.Parse("""
            {"prompts":1234,"served":1200,"localShare":0.62,"savedUsd":4.1,"referenceModel":"opus",
             "learned":[{"id":3,"state":"Applied"},{"id":2,"state":"Undone"},{"id":1,"state":"Applied"}],
             "notices":[{"key":"down:lmstudio","title":"lmstudio has been down for 2 hours","detail":"…","link":"#/connectors"}],"notify":true}
            """);

        tray.Week.Should().Be(string.Format(System.Globalization.CultureInfo.CurrentCulture, "This week: {0:N0} prompts · {1:P0} local · saved $4.10", 1234, 0.62));
        tray.Learned.Should().Be(2);
        (tray.NeedsLine, tray.NeedsLink).Should().Be(("Needs a decision: lmstudio has been down for 2 hours", "#/connectors"));
        tray.New(new HashSet<string>()).Should().ContainSingle();
        tray.New(new HashSet<string> { "down:lmstudio" }).Should().BeEmpty();
        TrayDigest.Parse("""{"prompts":0}""").Should().Match<TrayDigest>(d => d.Week == null && d.NeedsLine == null && d.Learned == 0);
    }
}
