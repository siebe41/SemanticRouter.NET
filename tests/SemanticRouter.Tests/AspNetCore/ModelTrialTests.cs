using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// The cheapest good-enough model per route (#69): a cheaper model is tried on a share of a route's plain matches, takes
/// the route over when its turns draw no more feedback than the current model's, and is dropped when they draw more.
/// Each trial and switch is listed under Router learned and can be undone.
/// </summary>
public sealed class ModelTrialTests : RouterHostTest
{
    private const string Good = """{"id":"r","object":"chat.completion","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":3}}""";
    private const string Empty = """{"id":"r","object":"chat.completion","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":""},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":0}}""";

    private bool _midAnswersWell = true;

    public ModelTrialTests()
    {
        // "cloud" (tier 4) and "mid" (tier 3, cheaper) share a backend; "mid" answers well, or with nothing.
        CloudHttp = new StubHttpMessageHandler((_, body) => Reply(body.Contains("mid-model", StringComparison.Ordinal) && !_midAnswersWell ? Empty : Good));
        Classifier.Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string prompt, CancellationToken _) => new PromptClassificationResult
            {
                RequiredTier = 4,
                Reason = RoutingReason.SemanticMatch,
                RouteName = "agentic",
                LocalOnly = prompt.Contains("secret", StringComparison.Ordinal),
                Similarity = 0.9f,
                Complexity = new ComplexityAnalysis { Score = 0.1, EstimatedTokens = 3 },
            });
    }

    private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private Task<HttpClient> StartTrialsAsync() =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
                o.Reliability.MinimumTurns = 4;
            },
            routing: o =>
            {
                ModelDefinition mid = Model("mid", 3, costPerMillion: 1m, connector: "anthropic");
                mid.Model = "mid-model";
                o.Models.Add(mid);
                o.Routes.Add(Route("agentic", 4, "implement this feature"));
            },
            services: s => s.Configure<TuningOptions>(t =>
            {
                t.ModelTrialRate = 1;
                t.ModelTrialTurns = 4;
                t.MinimumPrompts = 1000;
            }));

    private static StringContent Chat(string prompt, string model = "auto") =>
        new(JsonSerializer.Serialize(new { model, messages = new[] { new { role = "user", content = prompt } } }), Encoding.UTF8, "application/json");

    private static async Task<string> ModelForAsync(HttpClient client, string prompt, string model = "auto")
    {
        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Chat(prompt, model));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return response.Headers.GetValues("X-Prompt-Route-Model").Single();
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> RunAsync(HttpClient client) => await JsonOf(await client.PostAsync("/router/api/tuning/run", null));

    private static async Task<JsonElement> ChangeAsync(HttpClient client, long id) =>
        (await JsonOf(await client.GetAsync("/router/api/tuning/changes"))).EnumerateArray().Single(c => c.GetProperty("id").GetInt64() == id);

    private static async Task<string?> PreferredModelAsync(HttpClient client) =>
        (await JsonOf(await client.GetAsync("/router/api/config"))).GetProperty("config").GetProperty("Routes").EnumerateArray()
            .Single(r => r.GetProperty("Name").GetString() == "agentic") is var route && route.TryGetProperty("PreferredModel", out JsonElement model) && model.ValueKind == JsonValueKind.String
                ? model.GetString()
                : null;

    private RoutingActivityLog Activity => Services.GetRequiredService<RoutingActivityLog>();

    /// <summary>Four turns on the route's current model, then a tuning run that starts the trial.</summary>
    private static async Task<long> StartTrialAsync(HttpClient client)
    {
        for (int i = 0; i < 4; i++)
        {
            (await ModelForAsync(client, $"task {i}")).Should().Be("cloud", "no trial runs yet");
        }

        JsonElement run = await RunAsync(client);
        JsonElement trial = run.GetProperty("changes").EnumerateArray().Single();
        (trial.GetProperty("kind").GetString(), trial.GetProperty("state").GetString()).Should().Be(("RouteModel", "Trying"));
        (trial.GetProperty("route").GetString(), trial.GetProperty("incumbent").GetString(), trial.GetProperty("newModel").GetString()).Should().Be(("agentic", "cloud", "mid"));
        trial.GetProperty("summary").GetString().Should().Be("model for agentic: cloud → mid");
        trial.GetProperty("why").GetString().Should().Contain("mid (tier 3) costs less than cloud (tier 4)").And.Contain("0 of 4 turns drew feedback").And.Contain("up to 4 turns");
        return trial.GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task A_cheaper_model_that_does_as_well_takes_the_route_over_and_undo_puts_it_back()
    {
        HttpClient client = await StartTrialsAsync();
        long id = await StartTrialAsync(client);

        for (int i = 0; i < 4; i++)
        {
            (await ModelForAsync(client, $"task trial {i}")).Should().Be("mid", "the trial tries every plain match here");
        }

        Activity.Recent(4).Should().OnlyContain(e => e.Trial == id && e.ServedBy == "mid");
        (await ModelForAsync(client, "task after the trial ran its turns")).Should().Be("cloud", "the trial has its turns");

        await RunAsync(client);

        JsonElement adopted = await ChangeAsync(client, id);
        adopted.GetProperty("state").GetString().Should().Be("Applied");
        adopted.GetProperty("why").GetString().Should().Contain("It did as well: mid: 0 of 4 trial turns drew feedback; cloud: 0 of 5 turns drew feedback.");
        adopted.GetProperty("version").GetInt64().Should().BePositive();
        (await PreferredModelAsync(client)).Should().Be("mid");
        (await ModelForAsync(client, "task on the learned model")).Should().Be("mid");
        RoutingActivityEntry learned = Activity.Recent(1).Single();
        (learned.Trial, learned.Learned).Should().Be((null, id), "the learned model, not a trial, and the Activity page links the change");
        (await ModelForAsync(client, "@cloud task with an explicit model")).Should().Be("cloud", "an explicit choice is not the route's to decide");

        (await client.PostAsync($"/router/api/tuning/changes/{id}/undo", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ChangeAsync(client, id)).GetProperty("state").GetString().Should().Be("Undone");
        (await PreferredModelAsync(client)).Should().BeNull();
        (await ModelForAsync(client, "task after undo")).Should().Be("cloud");
    }

    [Fact]
    public async Task A_cheaper_model_that_does_worse_is_dropped_and_not_tried_again()
    {
        _midAnswersWell = false;
        HttpClient client = await StartTrialsAsync();
        long id = await StartTrialAsync(client);
        for (int i = 0; i < 4; i++)
        {
            (await ModelForAsync(client, $"task trial {i}")).Should().Be("mid");
        }

        JsonElement run = await RunAsync(client);

        JsonElement dropped = await ChangeAsync(client, id);
        dropped.GetProperty("state").GetString().Should().Be("Dropped");
        dropped.GetProperty("why").GetString().Should().Contain("Dropped: mid: 4 of 4 trial turns drew feedback (4 empty replies); cloud: 0 of 4 turns drew feedback.");
        run.GetProperty("changes").EnumerateArray().Should().ContainSingle("no new trial of the dropped model");
        (await PreferredModelAsync(client)).Should().BeNull();
        (await ModelForAsync(client, "task after the trial")).Should().Be("cloud");
    }

    [Fact]
    public async Task Explicit_choices_follow_ups_and_local_only_requests_are_never_tried()
    {
        HttpClient client = await StartTrialsAsync();
        await StartTrialAsync(client);

        (await ModelForAsync(client, "task asking for a model", model: "cloud")).Should().Be("cloud");
        (await ModelForAsync(client, "@cloud task with a callout")).Should().Be("cloud");
        (await ModelForAsync(client, "task with a secret in it")).Should().Be("local", "a local-only request never goes to a cloud model on trial");

        StringContent followUp = new(
            JsonSerializer.Serialize(new { model = "auto", messages = new object[] { new { role = "user", content = "task one" }, new { role = "assistant", content = "done" }, new { role = "user", content = "task two" } } }),
            Encoding.UTF8,
            "application/json");
        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", followUp);
        response.Headers.GetValues("X-Prompt-Route-Model").Single().Should().Be("cloud", "a conversation's later turns are not tried");

        Activity.Recent(4).Should().OnlyContain(e => e.Trial == null);
    }

    [Fact]
    public async Task A_trial_can_be_stopped()
    {
        HttpClient client = await StartTrialsAsync();
        long id = await StartTrialAsync(client);

        (await client.PostAsync($"/router/api/tuning/changes/{id}/undo", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await ChangeAsync(client, id)).GetProperty("state").GetString().Should().Be("Undone");
        (await ModelForAsync(client, "task after stopping")).Should().Be("cloud");
        (await RunAsync(client)).GetProperty("changes").EnumerateArray().Should().BeEmpty("a stopped trial is not started again for a while");
    }
}
