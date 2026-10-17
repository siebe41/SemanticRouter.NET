using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Latency as a selection factor (#70): each model's typical time to first byte comes from its recent served turns, and
/// <c>auto:fastest</c> takes the quicker of two capable models while plain <c>auto</c> keeps choosing by cost. A model that
/// answers after another failed is timed from its own attempt (#119).
/// </summary>
public sealed class ModelLatencyTests : RouterHostTest
{
    private static RoutingActivityEntry Served(string model, double ttfb, DateTimeOffset at, string outcome = "served") =>
        new() { Timestamp = at, Tier = 4, ServedBy = model, Outcome = outcome, TimeToFirstByteMilliseconds = ttfb };

    [Fact]
    public void The_tracker_takes_each_models_median_over_its_recent_served_turns()
    {
        var clock = new ManualTimeProvider();
        DateTimeOffset now = clock.GetUtcNow();
        var activity = new RoutingActivityLog();
        foreach (double ttfb in new[] { 900, 200, 300, 250, 5000 })
        {
            activity.Add(Served("quick", ttfb, now.AddMinutes(-5)));
        }

        foreach (double ttfb in new[] { 100, 100, 100, 100 })
        {
            activity.Add(Served("sparse", ttfb, now.AddMinutes(-5)));
        }

        for (int i = 0; i < 5; i++)
        {
            activity.Add(Served("stale", 50, now.AddDays(-2)));
            activity.Add(Served("failing", 50, now.AddMinutes(-1), outcome: "failed"));
        }

        var tracker = new ModelLatencyTracker(activity, clock);

        tracker.Typical().Should().BeEquivalentTo(new Dictionary<string, TimeSpan> { ["quick"] = TimeSpan.FromMilliseconds(300) },
            "sparse has fewer than 5 turns, stale's are outside the day, failing's were not served");
        tracker.TypicalTimeToFirstByte(Model("QUICK", 4)).Should().Be(TimeSpan.FromMilliseconds(300));

        tracker.Configure(new ModelSelectionOptions { LatencySamples = 4, LatencyWindow = TimeSpan.FromDays(3) });

        tracker.Typical().Keys.Should().BeEquivalentTo(["quick", "sparse", "stale"]);
    }

    [Fact]
    public async Task A_request_gets_the_last_figures_while_new_ones_are_worked_out_in_the_background()
    {
        var clock = new ManualTimeProvider();
        var activity = new RoutingActivityLog();
        for (int i = 0; i < 5; i++)
        {
            activity.Add(Served("quick", 300, clock.GetUtcNow().AddMinutes(-5)));
        }

        var tracker = new ModelLatencyTracker(activity, clock);
        tracker.Typical()["quick"].Should().Be(TimeSpan.FromMilliseconds(300), "the first figures are worked out while the caller waits");

        for (int i = 0; i < 20; i++)
        {
            activity.Add(Served("quick", 900, clock.GetUtcNow().AddMinutes(-1)));
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        tracker.Typical()["quick"].Should().Be(TimeSpan.FromMilliseconds(300), "the stale figures are returned at once");

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (tracker.Typical()["quick"] != TimeSpan.FromMilliseconds(900) && System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(10);
        }

        tracker.Typical()["quick"].Should().Be(TimeSpan.FromMilliseconds(900), "the refresh landed");
    }

    [Fact]
    public async Task Auto_fastest_takes_the_quicker_capable_model_and_says_why()
    {
        // "cloud" (tier 4, cost 3) is slow, "quick" (tier 4, cost 5) is fast; both can do the work.
        ClassifyAs(tier: 4);
        HttpClient client = await StartAsync(
            o => o.PathPrefixes.Add("/v1/chat/completions"),
            routing: o =>
            {
                ModelDefinition quick = Model("quick", 4, costPerMillion: 5m, connector: "anthropic");
                quick.Model = "quick-model";
                o.Models.Add(quick);
            });
        RoutingActivityLog activity = Services.GetRequiredService<RoutingActivityLog>();
        for (int i = 0; i < 5; i++)
        {
            activity.Add(Served("cloud", 1500 + i, DateTimeOffset.UtcNow.AddMinutes(-5)));
            activity.Add(Served("quick", 300 + i, DateTimeOffset.UtcNow.AddMinutes(-5)));
        }

        HttpResponseMessage fastest = await client.PostAsync("/v1/chat/completions", Chat("auto:fastest"));
        fastest.StatusCode.Should().Be(HttpStatusCode.OK);
        fastest.Headers.GetValues("X-Prompt-Route-Model").Single().Should().Be("quick");
        string why = fastest.Headers.GetValues("X-Prompt-Route-Selection").Single();
        why.Should().Be("fastest: quick (302 ms to first byte) over cheaper cloud (1,502 ms to first byte)");
        activity.Recent(1).Single().Selection.Should().Be(why);

        HttpResponseMessage cheapest = await client.PostAsync("/v1/chat/completions", Chat("auto"));
        cheapest.Headers.GetValues("X-Prompt-Route-Model").Single().Should().Be("cloud", "the default strategy still chooses by cost");
        cheapest.Headers.Contains("X-Prompt-Route-Selection").Should().BeFalse();
        activity.Recent(1).Single().Selection.Should().BeNull();
    }

    [Fact]
    public async Task After_a_failover_the_serving_model_is_timed_from_its_own_attempt()
    {
        // "local" takes 800 ms to fail, then "cloud" answers 120 ms after it is asked (#119).
        var clock = new ManualTimeProvider();
        LocalHttp = new StubHttpMessageHandler((_, _) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(800));
            throw new HttpRequestException("Connection refused");
        });
        CloudHttp = new StubHttpMessageHandler((_, _) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(120));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(OpenAIReply, Encoding.UTF8, "application/json") };
        });
        HttpClient client = await StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
            },
            services: s => s.AddSingleton<TimeProvider>(clock));

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Chat("auto"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        LocalHttp.Requests.Should().ContainSingle();
        RoutingActivityLog activity = Services.GetRequiredService<RoutingActivityLog>();
        RoutingActivityEntry entry = activity.Recent(1).Single();
        entry.ServedBy.Should().Be("cloud");
        entry.SentTo.Should().Equal("local", "cloud");
        entry.TimeToFirstByteMilliseconds.Should().Be(920, "the client waited through the failed attempt");
        entry.TotalMilliseconds.Should().Be(920);
        entry.AttemptTimeToFirstByteMilliseconds.Should().Be(120, "cloud's own first byte counts from when it was asked");
        entry.AttemptMilliseconds.Should().Be(120);

        // Activity entries carry wall-clock timestamps, so the tracker reads them on the system clock.
        var tracker = new ModelLatencyTracker(activity);
        tracker.Configure(new ModelSelectionOptions { LatencySamples = 1 });
        tracker.Typical().Should().BeEquivalentTo(new Dictionary<string, TimeSpan> { ["cloud"] = TimeSpan.FromMilliseconds(120) });

        RouterConfiguration config = Services.GetRequiredService<RouterRuntimeProvider>().Current.Configuration;
        EndpointStatus endpoint = EndpointHealth.Build(config, new Dictionary<string, DateTimeOffset>(), activity.Recent(10), null, null)
            .Single(e => e.Connector == "anthropic");
        endpoint.FirstByteP50.Should().Be(120);
        endpoint.TotalP50.Should().Be(120);
    }

    private const string OpenAIReply = """{"id":"c1","object":"chat.completion","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5}}""";

    private static StringContent Chat(string model) =>
        new($$"""{"model":"{{model}}","messages":[{"role":"user","content":"implement the parser"}]}""", Encoding.UTF8, "application/json");
}
