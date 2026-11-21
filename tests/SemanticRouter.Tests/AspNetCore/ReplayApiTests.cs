using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>What-if replay (#373): recent requests and the evaluation set through the running and a candidate configuration.</summary>
public sealed class ReplayApiTests : RouterHostTest
{
    private static readonly string[] Prompts =
    [
        "hello there, how are you",
        "good morning to you",
        "hi there friend",
        "design a distributed system for payments",
        "architect a scalable platform for us",
        "what is the capital of Australia",
    ];

    private Task<HttpClient> StartRealAsync() =>
        StartAsync(
            mockClassifier: false,
            routing: o =>
            {
                o.DefaultSimilarityThreshold = 0.3f;
                o.Routes.Add(Route("smalltalk", 1, "hello how are you", "good morning", "hi there"));
                o.Routes.Add(Route("reasoning", 4, "design a distributed system", "architect a scalable platform"));
            });

    private static StringContent Text(string text) => new(text, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonObject> ConfigOf(HttpClient client) =>
        (JsonObject)JsonNode.Parse(await client.GetStringAsync("/router/api/config"))!["config"]!;

    /// <summary>Records the prompts as requests the router classified, an hour ago, with the tokens their answers took.</summary>
    private void Record(params string[] prompts)
    {
        RoutingActivityLog activity = Services.GetRequiredService<RoutingActivityLog>();
        foreach (string prompt in prompts)
        {
            activity.Add(new RoutingActivityEntry
            {
                Timestamp = DateTimeOffset.UtcNow.AddHours(-1),
                PromptPreview = prompt,
                Tier = 1,
                Reason = RoutingReason.SemanticMatch,
                InputTokens = 1_000_000,
                OutputTokens = 0,
            });
        }
    }

    [Fact]
    public async Task An_unchanged_configuration_moves_nothing()
    {
        HttpClient client = await StartRealAsync();
        Record(Prompts);
        RoutingActivityLog activity = Services.GetRequiredService<RoutingActivityLog>();
        activity.Add(new RoutingActivityEntry { Timestamp = DateTimeOffset.UtcNow.AddHours(-1), PromptPreview = "a long prompt cut off at its preview…" });
        activity.Add(new RoutingActivityEntry { Timestamp = DateTimeOffset.UtcNow.AddHours(-1), PromptPreview = string.Empty });
        activity.Add(new RoutingActivityEntry { Timestamp = DateTimeOffset.UtcNow.AddHours(-1), PromptPreview = "use opus", Reason = RoutingReason.ModelOverride });
        activity.Add(new RoutingActivityEntry { Timestamp = DateTimeOffset.UtcNow.AddDays(-10), PromptPreview = "older than the window" });
        (await client.PutAsync("/router/api/evaluation/set", Text("""
            {"prompt":"hello there, how are you","tier":1}
            {"prompt":"design a distributed system for payments","tier":4}
            """))).EnsureSuccessStatusCode();

        JsonElement report = await JsonOf(await client.PostAsync("/router/api/replay?days=7", Text((await ConfigOf(client)).ToJsonString())));

        report.GetProperty("scope").GetString().Should().Be("per-message");
        JsonElement requests = report.GetProperty("requests");
        requests.GetProperty("inWindow").GetInt32().Should().Be(Prompts.Length + 3);
        requests.GetProperty("replayed").GetInt32().Should().Be(Prompts.Length);
        requests.GetProperty("promptTruncated").GetInt32().Should().Be(1);
        requests.GetProperty("promptNotKept").GetInt32().Should().Be(1);
        requests.GetProperty("notClassified").GetInt32().Should().Be(1);
        report.GetProperty("moved").GetInt32().Should().Be(0);
        report.GetProperty("moves").GetArrayLength().Should().Be(0);
        report.GetProperty("tiers").EnumerateArray().Should().OnlyContain(t => t.GetProperty("before").GetInt32() == t.GetProperty("after").GetInt32());
        report.GetProperty("cost").GetProperty("deltaUsd").GetDecimal().Should().Be(0);
        report.GetProperty("cost").GetProperty("priced").GetInt32().Should().Be(Prompts.Length);
        JsonElement evaluation = report.GetProperty("evaluation");
        evaluation.GetProperty("cases").GetInt32().Should().Be(2);
        evaluation.GetProperty("moved").GetInt32().Should().Be(0);
        evaluation.GetProperty("correctAfter").GetInt32().Should().Be(evaluation.GetProperty("correctBefore").GetInt32());
        report.GetProperty("embedded").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task A_raised_threshold_moves_exactly_the_requests_whose_scores_cross_it()
    {
        HttpClient client = await StartRealAsync();

        // Each prompt's best route score, as the running router sees it. Classifying is recorded as activity too, so the
        // requests to replay are recorded once that is cleared.
        var scores = new Dictionary<string, float>();
        foreach (string prompt in Prompts)
        {
            JsonElement classified = await JsonOf(await client.PostAsJsonAsync("/router/api/classify", new { prompt }));
            scores[prompt] = classified.GetProperty("routes").EnumerateArray().Select(r => r.GetProperty("similarity").GetSingle()).DefaultIfEmpty(0).Max();
        }

        float[] matched = [.. scores.Values.Where(s => s >= 0.3f).Order()];
        matched.Should().HaveCountGreaterThan(1, "the test needs prompts on both sides of the new threshold");
        float threshold = (matched[0] + matched[^1]) / 2;
        string[] crossing = [.. scores.Where(s => s.Value >= 0.3f && s.Value < threshold).Select(s => s.Key)];
        crossing.Should().NotBeEmpty();
        Services.GetRequiredService<RoutingActivityLog>().Purge(DateTimeOffset.MinValue, DateTimeOffset.MaxValue);
        Record(Prompts);

        JsonObject candidate = await ConfigOf(client);
        candidate["DefaultSimilarityThreshold"] = threshold;
        JsonElement report = await JsonOf(await client.PostAsync("/router/api/replay?evaluation=false", Text(candidate.ToJsonString())));

        report.GetProperty("moved").GetInt32().Should().Be(crossing.Length);
        report.GetProperty("moves").EnumerateArray().Select(m => m.GetProperty("prompt").GetString()).Should().BeEquivalentTo(crossing);
        foreach (JsonElement move in report.GetProperty("moves").EnumerateArray())
        {
            move.GetProperty("before").GetProperty("route").GetString().Should().NotBeNull();
            move.GetProperty("after").GetProperty("reason").GetString().Should().Be(nameof(RoutingReason.NoMatchFallback));
        }

        report.TryGetProperty("evaluation", out JsonElement evaluation).Should().BeTrue();
        evaluation.ValueKind.Should().Be(JsonValueKind.Null, "the evaluation set was not asked for");

        // A million input tokens moved from the free local model to the $3/M cloud one costs $3 each.
        JsonElement cost = report.GetProperty("cost");
        decimal expected = report.GetProperty("moves").EnumerateArray()
            .Sum(m => m.GetProperty("costAfterUsd").GetDecimal() - m.GetProperty("costBeforeUsd").GetDecimal());
        cost.GetProperty("deltaUsd").GetDecimal().Should().Be(expected);
        JsonElement[] tiers = [.. report.GetProperty("tiers").EnumerateArray()];
        tiers.Sum(t => t.GetProperty("before").GetInt32()).Should().Be(Prompts.Length);
        tiers.Sum(t => t.GetProperty("after").GetInt32()).Should().Be(Prompts.Length);
        report.GetProperty("connectors").EnumerateArray().Sum(c => c.GetProperty("after").GetInt32()).Should().Be(Prompts.Length);
    }

    [Fact]
    public async Task The_running_configuration_is_left_alone()
    {
        HttpClient client = await StartRealAsync();
        Record(Prompts);
        int version = Services.GetRequiredService<RouterRuntimeProvider>().Current.Version;

        JsonObject candidate = await ConfigOf(client);
        candidate["DefaultSimilarityThreshold"] = 0.99f;
        (await client.PostAsync("/router/api/replay", Text(candidate.ToJsonString()))).EnsureSuccessStatusCode();

        RouterRuntime runtime = Services.GetRequiredService<RouterRuntimeProvider>().Current;
        runtime.Version.Should().Be(version);
        runtime.Configuration.DefaultSimilarityThreshold.Should().Be(0.3f);
    }

    [Fact]
    public async Task Refuses_an_invalid_candidate_or_window()
    {
        HttpClient client = await StartRealAsync();
        JsonObject candidate = await ConfigOf(client);

        (await client.PostAsync("/router/api/replay?days=0", Text(candidate.ToJsonString()))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsync($"/router/api/replay?days={ReplayApi.MaxDays + 1}", Text(candidate.ToJsonString()))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsync("/router/api/replay", Text(""))).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        candidate["DefaultSimilarityThreshold"] = 5;
        HttpResponseMessage invalid = await client.PostAsync("/router/api/replay", Text(candidate.ToJsonString()));
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await invalid.Content.ReadAsStringAsync()).Should().Contain("DefaultSimilarityThreshold");
    }

    [Fact]
    public async Task Replays_through_the_candidate_embeddings_only_once_per_text()
    {
        var inner = new CountingEmbeddings();
        var memo = new MemoizingEmbeddingProvider(inner);

        await memo.GenerateEmbeddingsAsync(["a", "b", "a"]);
        await memo.GenerateEmbeddingAsync("b");
        await memo.GenerateEmbeddingAsync("c");
        IReadOnlyList<ReadOnlyMemory<float>> again = await memo.GenerateEmbeddingsAsync(["c", "a"]);

        inner.Texts.Should().Equal("a", "b", "c");
        memo.Embedded.Should().Be(3);
        again[0].ToArray().Should().Equal(CountingEmbeddings.Vector("c"));
    }

    private sealed class CountingEmbeddings : IEmbeddingProvider
    {
        public List<string> Texts { get; } = [];

        public static float[] Vector(string text) => [text[0], 1];

        public ValueTask<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default)
        {
            Texts.Add(text);
            return ValueTask.FromResult<ReadOnlyMemory<float>>(Vector(text));
        }

        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
        {
            Texts.AddRange(texts);
            return ValueTask.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>([.. texts.Select(t => (ReadOnlyMemory<float>)Vector(t))]);
        }
    }
}
