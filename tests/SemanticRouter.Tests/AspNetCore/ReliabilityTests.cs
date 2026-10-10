using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Model reliability per kind of work (#44), through the router: a backend that fails on one route stops getting that
/// route, keeps the other, and gets it back after the period or a reset.
/// </summary>
public sealed class ReliabilityTests : RouterHostTest
{
    private const string Good = """{"id":"r","object":"chat.completion","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":3}}""";
    private const string Empty = """{"id":"r","object":"chat.completion","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":""},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":0}}""";

    private readonly ManualTimeProvider _time = new();

    public ReliabilityTests()
    {
        _time.Advance(DateTimeOffset.UtcNow - _time.GetUtcNow());

        // The stand-in local backend answers chat, and answers agentic tasks with nothing.
        LocalHttp = new StubHttpMessageHandler((_, body) => Reply(body.Contains("task", StringComparison.Ordinal) ? Empty : Good));
        CloudHttp = new StubHttpMessageHandler((_, _) => Reply(Good));
        Classifier.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string prompt, CancellationToken _) => new PromptClassificationResult
            {
                RequiredTier = 1,
                Reason = RoutingReason.SemanticMatch,
                RouteName = prompt.StartsWith("task", StringComparison.Ordinal) ? "agentic" : "chat",
                Similarity = 0.9f,
                Complexity = new ComplexityAnalysis { Score = 0.1, EstimatedTokens = 3 },
            });
    }

    private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private Task<HttpClient> StartDispatchAsync(Action<ReliabilityOptions>? reliability = null, bool chatRoute = true) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
                o.Reliability.MinimumTurns = 4;
                reliability?.Invoke(o.Reliability);
            },
            routing: o =>
            {
                o.Routes.Add(Route("agentic", 1, "implement this feature"));
                if (chatRoute)
                {
                    o.Routes.Add(Route("chat", 1, "hello there"));
                }
            },
            services: s => s.AddSingleton<TimeProvider>(_time));

    /// <summary>
    /// The tracker, with the test clock caught up to the real one first: the activity log stamps entries by the real
    /// clock, and a slow machine can take longer than the tracker's one-second margin to get here.
    /// </summary>
    private ModelReliabilityTracker Tracker
    {
        get
        {
            TimeSpan behind = DateTimeOffset.UtcNow - _time.GetUtcNow();
            if (behind > TimeSpan.Zero)
            {
                _time.Advance(behind);
            }

            return Services.GetRequiredService<ModelReliabilityTracker>();
        }
    }

    private static async Task<string> ModelForAsync(HttpClient client, string prompt)
    {
        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(JsonSerializer.Serialize(new { model = "auto", messages = new[] { new { role = "user", content = prompt } } }), Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return response.Headers.GetValues("X-Prompt-Route-Model").Single();
    }

    private async Task FailFourTasksAsync(HttpClient client)
    {
        int before = Services.GetRequiredService<RoutingFeedbackLog>().Recent(1000).Count;
        for (int i = 0; i < 4; i++)
        {
            (await ModelForAsync(client, $"task {i}: wire the endpoint")).Should().Be("local");
        }

        // An empty reply is noticed as the response finishes, which can be just after the client has it.
        for (int wait = 0; wait < 100 && Services.GetRequiredService<RoutingFeedbackLog>().Recent(1000).Count < before + 4; wait++)
        {
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task A_model_that_fails_on_one_route_stops_getting_it_and_keeps_the_other_until_the_period_ends()
    {
        HttpClient client = await StartDispatchAsync();
        await FailFourTasksAsync(client);
        (await ModelForAsync(client, "hello there")).Should().Be("local");

        ModelReliability row = Tracker.Refresh().Single(r => r.Model == "local" && r.Route == "agentic");

        (row.Turns, row.Failures, row.Rate).Should().Be((4, 4, 1.0));
        row.ExcludedUntil.Should().Be(_time.GetUtcNow() + TimeSpan.FromDays(1));
        row.Detail.Should().Be("4 of 4 turns drew feedback (4 empty replies)");

        HttpResponseMessage task = await client.PostAsync(
            "/v1/chat/completions",
            new StringContent(JsonSerializer.Serialize(new { model = "auto", messages = new[] { new { role = "user", content = "task 5: wire the endpoint" } } }), Encoding.UTF8, "application/json"));
        task.Headers.GetValues("X-Prompt-Route-Model").Single().Should().Be("cloud");
        task.Headers.GetValues("X-Prompt-Route-Unreliable").Single().Should().Be("local");
        (await ModelForAsync(client, "hello again")).Should().Be("local", "chat still goes to the local model");
        RoutingActivityEntry entry = Services.GetRequiredService<RoutingActivityLog>().Recent(2)[1];
        entry.Unreliable.Should().Be("local");

        // After the period it is offered again, with a fresh count.
        _time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        Tracker.Refresh().Should().NotContain(r => r.ExcludedUntil != null);
        (await ModelForAsync(client, "task 6: wire the endpoint")).Should().Be("local");
    }

    [Fact]
    public async Task The_models_page_api_shows_it_and_a_reset_offers_the_model_again()
    {
        HttpClient client = await StartDispatchAsync();
        await FailFourTasksAsync(client);

        JsonElement reliability = JsonDocument.Parse(await client.GetStringAsync("/router/api/reliability")).RootElement;
        JsonElement left = reliability.GetProperty("models").EnumerateArray().Single(m => m.GetProperty("excludedUntil").ValueKind != JsonValueKind.Null);
        (left.GetProperty("model").GetString(), left.GetProperty("route").GetString()).Should().Be(("local", "agentic"));
        (await ModelForAsync(client, "task 5: wire the endpoint")).Should().Be("cloud");

        // The activity log keeps real time; the tracker's clock must be past those turns for a fresh count to start after them.
        _time.Advance(TimeSpan.FromMinutes(1));
        (await client.PostAsJsonAsync("/router/api/reliability/reset", new { model = "local", route = "agentic" })).EnsureSuccessStatusCode();

        Tracker.Current.Should().NotContain(r => r.ExcludedUntil != null);
        (await ModelForAsync(client, "task 6: wire the endpoint")).Should().Be("local");
        (await client.PostAsJsonAsync("/router/api/reliability/reset", new { model = "" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_model_keeps_its_last_route_and_the_switch_turns_it_off()
    {
        HttpClient client = await StartDispatchAsync(chatRoute: false);
        await FailFourTasksAsync(client);

        Tracker.Refresh().Should().ContainSingle(r => r.Model == "local").Which.ExcludedUntil.Should().BeNull("agentic is the only route it can serve");

        await DisposeAsync();
        client = await StartDispatchAsync(o => o.Enabled = false);
        await FailFourTasksAsync(client);
        Tracker.Refresh().Should().BeEmpty();
        (await ModelForAsync(client, "task 5: wire the endpoint")).Should().Be("local");
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(0, true)]
    public async Task A_model_is_judged_against_its_peers_on_the_same_work(int peerFailures, bool leftOut)
    {
        await StartDispatchAsync();
        var activity = Services.GetRequiredService<RoutingActivityLog>();
        var feedback = Services.GetRequiredService<RoutingFeedbackLog>();
        void Turns(string model, int turns, int failures)
        {
            for (int i = 0; i < turns; i++)
            {
                RoutingActivityEntry entry = activity.Add(new RoutingActivityEntry
                {
                    Id = activity.Reserve(),
                    Timestamp = _time.GetUtcNow().AddHours(-1),
                    Route = "agentic",
                    Reason = RoutingReason.SemanticMatch,
                    ServedBy = model,
                    Outcome = "served",
                });
                if (i < failures)
                {
                    feedback.Add(new RoutingFeedback { Timestamp = entry.Timestamp, EntryId = entry.Id, Kind = FeedbackKind.Corrected, Model = model, Route = "agentic" });
                }
            }
        }

        // local: 4 of 10 (40%), above the 30% bar; it is only clearly worse when cloud does better than 25%.
        Turns("local", 10, 4);
        Turns("cloud", 10, peerFailures);

        ModelReliability local = Tracker.Refresh().Single(r => r.Model == "local" && r.Route == "agentic");

        local.PeerRate.Should().Be(peerFailures / 10.0);
        (local.ExcludedUntil is not null).Should().Be(leftOut);
    }
}
