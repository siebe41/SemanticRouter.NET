using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Webhooks (#71): each notice reaches a webhook once when it appears and once when it clears, the digest once a week,
/// in JSON, Slack or ntfy shape; a failing webhook is retried, then logged, and never holds up the check.
/// </summary>
public sealed class WebhookTests : RouterHostTest
{
    private readonly ManualTimeProvider _time = new();
    private readonly CapturingLoggerProvider _logs = new();
    private readonly ConcurrentQueue<Delivery> _deliveries = new();
    private Func<HttpRequestMessage, Task<HttpResponseMessage>> _reply = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

    public WebhookTests()
    {
        _time.Advance(DateTimeOffset.UtcNow - _time.GetUtcNow());
        WebhookNotifier.RetryDelays = [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)];
    }

    private sealed record Delivery(string Url, string? ContentType, string Body, IReadOnlyDictionary<string, string> Headers);

    private NoticeService Service => Services.GetRequiredService<NoticeService>();

    private Task<HttpClient> StartWithAsync(Action<NotificationOptions> notifications, Action<IServiceCollection>? services = null) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
            },
            services: s =>
            {
                s.AddSingleton<TimeProvider>(_time);
                s.AddSingleton<ILoggerProvider>(_logs);
                s.Configure<TuningOptions>(t => t.Mode = TuningMode.Suggest);
                s.Configure(notifications);
                s.AddHttpClient(WebhookNotifier.ClientName).ConfigurePrimaryHttpMessageHandler(() => new Recorder(this));
                services?.Invoke(s);
            });

    /// <summary>Raises the "automatic tuning has a suggestion" notice (or, with <paramref name="clear"/>, dismisses it).</summary>
    private void Suggestion(bool clear = false) =>
        Services.GetRequiredService<ITuningStore>().Save(new TuningChange
        {
            Id = 1,
            Timestamp = _time.GetUtcNow(),
            Kind = TuningChangeKind.DefaultThreshold,
            OldValue = 0.75f,
            NewValue = 0.73f,
            State = clear ? TuningChangeState.Dismissed : TuningChangeState.Suggested,
        });

    private async Task CheckAsync()
    {
        Service.Check();
        await Service.LastDelivery.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_notice_reaches_each_webhook_once_when_it_appears_and_once_when_it_clears()
    {
        await StartWithAsync(n =>
        {
            n.DashboardUrl = "http://router.lan:5080/router/";
            n.Webhooks["json"] = new WebhookOptions { Url = "https://hooks.example/json" };
            n.Webhooks["team"] = new WebhookOptions { Url = "https://hooks.slack.example/T/B/x", Format = WebhookFormat.Slack };
            n.Webhooks["phone"] = new WebhookOptions { Url = "https://ntfy.example/router", Format = WebhookFormat.Ntfy };
            n.Webhooks["weekly"] = new WebhookOptions { Url = "https://hooks.example/weekly", Notices = false };
        });
        Suggestion();

        await CheckAsync();
        await CheckAsync();

        _deliveries.Select(d => d.Url).Should().BeEquivalentTo(["https://hooks.example/json", "https://hooks.slack.example/T/B/x", "https://ntfy.example/router"], "once each, and not to the digest-only webhook");
        JsonElement json = JsonDocument.Parse(Single("https://hooks.example/json").Body).RootElement;
        (json.GetProperty("type").GetString(), json.GetProperty("state").GetString(), json.GetProperty("key").GetString(), json.GetProperty("kind").GetString())
            .Should().Be(("notice", "appeared", "tuning:suggestions", "TuningSuggestion"));
        json.GetProperty("title").GetString().Should().Be("Automatic tuning has a suggestion");
        json.GetProperty("link").GetString().Should().Be("http://router.lan:5080/router/#/learned");
        Single("https://hooks.example/json").ContentType.Should().Be("application/json");

        string slack = JsonDocument.Parse(Single("https://hooks.slack.example/T/B/x").Body).RootElement.GetProperty("text").GetString()!;
        slack.Should().StartWith("*Needs a decision: Automatic tuning has a suggestion*").And.Contain("<http://router.lan:5080/router/#/learned|Decide on the dashboard>");

        Delivery ntfy = Single("https://ntfy.example/router");
        ntfy.ContentType.Should().Be("text/plain");
        ntfy.Body.Should().Contain("0.75 → 0.73");
        ntfy.Headers.Should().Contain("Title", "Needs a decision: Automatic tuning has a suggestion").And.Contain("Click", "http://router.lan:5080/router/#/learned").And.Contain("Priority", "4");

        _deliveries.Clear();
        Suggestion(clear: true);
        await CheckAsync();

        _deliveries.Should().HaveCount(3);
        JsonDocument.Parse(Single("https://hooks.example/json").Body).RootElement.GetProperty("state").GetString().Should().Be("cleared");
        JsonDocument.Parse(Single("https://hooks.slack.example/T/B/x").Body).RootElement.GetProperty("text").GetString()
            .Should().Be("No longer needs a decision: Automatic tuning has a suggestion");
        Single("https://ntfy.example/router").Headers.Should().Contain("Tags", "white_check_mark");
    }

    [Fact]
    public async Task The_digest_goes_out_once_a_week()
    {
        await StartWithAsync(n => n.Webhooks["weekly"] = new WebhookOptions { Url = "https://hooks.example/weekly", Notices = false });

        await CheckAsync();
        _deliveries.Should().BeEmpty("the first look starts the week");

        _time.Advance(TimeSpan.FromDays(6));
        await CheckAsync();
        _deliveries.Should().BeEmpty();

        _time.Advance(TimeSpan.FromDays(1));
        Suggestion();
        await CheckAsync();
        await CheckAsync();

        Delivery digest = _deliveries.Should().ContainSingle().Subject;
        JsonElement json = JsonDocument.Parse(digest.Body).RootElement;
        json.GetProperty("type").GetString().Should().Be("digest");
        json.GetProperty("summary").GetString().Should().StartWith("0 prompts routed").And.Contain("Needs a decision: Automatic tuning has a suggestion.");
        json.GetProperty("digest").GetProperty("notices").GetArrayLength().Should().Be(1);

        _time.Advance(TimeSpan.FromDays(7));
        await CheckAsync();
        _deliveries.Should().HaveCount(2, "and again a week later");
    }

    [Fact]
    public async Task The_digest_week_survives_a_restart_through_the_state_store()
    {
        var store = new MemoryValues();
        store.Save(NoticeService.DigestSentKey, _time.GetUtcNow().AddDays(-8).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        await StartWithAsync(
            n => n.Webhooks["weekly"] = new WebhookOptions { Url = "https://hooks.example/weekly", Notices = false },
            s => s.AddSingleton<IStateValueStore>(store));

        await CheckAsync();

        _deliveries.Should().ContainSingle("a week had passed before the restart");
        DateTimeOffset.Parse(store.Load(NoticeService.DigestSentKey)!, System.Globalization.CultureInfo.InvariantCulture).Should().Be(_time.GetUtcNow());
    }

    [Fact]
    public async Task A_failing_webhook_is_retried_then_logged_and_never_holds_up_the_check()
    {
        var gate = new TaskCompletionSource();
        _reply = async _ =>
        {
            await gate.Task;
            return new HttpResponseMessage(HttpStatusCode.BadGateway);
        };
        await StartWithAsync(n => n.Webhooks["json"] = new WebhookOptions { Url = "https://hooks.example/json" });
        Suggestion();

        Service.Check().Appeared.Should().ContainSingle();
        Service.LastDelivery.IsCompleted.Should().BeFalse("delivery runs in the background");

        gate.SetResult();
        await Service.LastDelivery.WaitAsync(TimeSpan.FromSeconds(10));

        _deliveries.Should().HaveCount(3, "the first attempt and two retries");
        _logs.All.Should().Contain("Webhook json could not be reached after 3 attempts (HTTP 502)");
    }

    [Fact]
    public async Task An_unreachable_webhook_is_logged()
    {
        _reply = _ => throw new HttpRequestException("Connection refused");
        await StartWithAsync(n => n.Webhooks["json"] = new WebhookOptions { Url = "https://hooks.example/json" });
        Suggestion();

        await CheckAsync();

        _logs.All.Should().Contain("Webhook json could not be reached after 3 attempts (Connection refused)");
    }

    [Fact]
    public async Task The_switch_silences_webhooks_too()
    {
        await StartWithAsync(n =>
        {
            n.Enabled = false;
            n.Webhooks["json"] = new WebhookOptions { Url = "https://hooks.example/json" };
        });
        Suggestion();

        await CheckAsync();
        _time.Advance(TimeSpan.FromDays(8));
        await CheckAsync();

        _deliveries.Should().BeEmpty();
    }

    [Fact]
    public void Webhook_urls_are_secrets()
    {
        var document = new RouterConfiguration();
        document.Notifications.Webhooks["team"] = new WebhookOptions { Url = "https://hooks.slack.example/T/B/token", Format = WebhookFormat.Slack };
        document.Notifications.Webhooks["phone"] = new WebhookOptions { Url = "${secret:notifications/webhooks/phone}", Format = WebhookFormat.Ntfy };

        RouterConfiguration masked = document.Masked();
        masked.Notifications.Webhooks["team"].Url.Should().Be(RouterConfiguration.MaskedSecret);
        masked.Notifications.Webhooks["phone"].Url.Should().Be("${secret:notifications/webhooks/phone}");
        document.LiteralSecretSettings().Should().Equal("Notifications:Webhooks:team:Url");

        var store = new SecretsTests.MemorySecretStore();
        document.MoveSecretsToStore(store);
        document.Notifications.Webhooks["team"].Url.Should().Be("${secret:notifications/webhooks/team}");
        store.Values["notifications/webhooks/team"].Should().Be("https://hooks.slack.example/T/B/token");
        document.ResolveSecrets(store, []).Notifications.Webhooks["team"].Url.Should().Be("https://hooks.slack.example/T/B/token");
    }

    [Fact]
    public void Webhook_settings_are_validated()
    {
        var options = new NotificationOptions { DashboardUrl = "router.lan" };
        options.Webhooks["ftp"] = new WebhookOptions { Url = "ftp://example" };
        options.Webhooks["empty"] = new WebhookOptions();
        options.Webhooks["ok"] = new WebhookOptions { Url = "https://ntfy.sh/topic" };
        options.Webhooks["ref"] = new WebhookOptions { Url = "${secret:notifications/webhooks/ref}" };

        options.GetValidationErrors().Should().BeEquivalentTo(
            "Notifications:DashboardUrl must be an http or https address.",
            "Notifications:Webhooks:ftp:Url must be an http or https address (or a ${secret:…} reference).",
            "Notifications:Webhooks:empty:Url must be an http or https address (or a ${secret:…} reference).");
    }

    [Fact]
    public async Task An_ntfy_title_outside_ascii_is_encoded_for_its_header()
    {
        var notice = new RouterNotice("connector:down:café", NoticeKind.ConnectorDown, "Connector café is down", "Every request failed.", "#/connectors", DateTimeOffset.UtcNow);

        using HttpContent content = WebhookNotifier.NoticeMessage(WebhookFormat.Ntfy, notice, cleared: false, dashboardUrl: null);

        string title = content.Headers.GetValues("Title").Single();
        title.Should().StartWith("=?UTF-8?B?");
        Encoding.UTF8.GetString(Convert.FromBase64String(title[10..^2])).Should().Be("Needs a decision: Connector café is down");
        (await content.ReadAsStringAsync()).Should().Be("Every request failed.");
        content.Headers.Contains("Click").Should().BeFalse("there is no dashboard address to link to");
    }

    [Fact]
    public void The_digest_reads_as_a_few_lines()
    {
        var digest = new DigestResponse(
            DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow, Prompts: 1234, Served: 1234, Local: 765, LocalShare: 0.62, CostUsd: 3.2m,
            ReferenceCostUsd: 21.6m, SavedUsd: 18.4m, ReferenceModel: "opus", Escalated: 8, HeldByConversation: 2, Degraded: 2, Failed: 0,
            Signals: 0, new Dictionary<FeedbackKind, int>(), [], Suggestions: 0, [], Notify: true);

        WebhookNotifier.DigestText(digest).Should().Be(
            "1,234 prompts routed, 62% served locally, $3.20 spent, $18.40 saved against opus.\n"
            + "10 raised a tier, 2 degraded, 0 failed.\n"
            + "No signs that a decision fell short.");
    }

    private Delivery Single(string url) => _deliveries.Should().ContainSingle(d => d.Url == url).Subject;

    private sealed class Recorder(WebhookTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Content?.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            owner._deliveries.Enqueue(new Delivery(request.RequestUri!.ToString(), request.Content?.Headers.ContentType?.MediaType, body, headers));
            return await owner._reply(request);
        }
    }

    private sealed class MemoryValues : IStateValueStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

        public string? Load(string key) => _values.TryGetValue(key, out string? value) ? value : null;

        public void Save(string key, string value) => _values[key] = value;
    }
}
