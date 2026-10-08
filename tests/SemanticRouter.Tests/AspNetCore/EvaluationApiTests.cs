using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>The routing-quality loop: evaluation sets, calibration, route health, labelling activity, retry escalation.</summary>
public sealed class EvaluationApiTests : RouterHostTest
{
    private const string Set = """
        {"prompt":"hello there, how are you","tier":1,"route":"smalltalk"}
        {"prompt":"good morning to you","tier":1,"route":"smalltalk"}
        {"prompt":"design a distributed system for payments","tier":4,"route":"reasoning"}
        {"prompt":"what is the capital of Australia","tier":3}
        {"prompt":"my patient record for John","tier":1,"localOnly":true}
        """;

    private Task<HttpClient> StartRealAsync(float threshold = 0.3f, Action<PromptRouterMiddlewareOptions>? middleware = null) =>
        StartAsync(
            middleware,
            mockClassifier: false,
            routing: o =>
            {
                o.DefaultSimilarityThreshold = threshold;
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

    [Fact]
    public async Task Evaluates_a_set_and_recommends_a_threshold_that_is_no_worse()
    {
        HttpClient client = await StartRealAsync(threshold: 0.95f);

        JsonElement report = await JsonOf(await client.PostAsync("/router/api/evaluate?calibrate=true", Text(Set)));

        report.GetProperty("total").GetInt32().Should().Be(5);
        report.GetProperty("perTier").EnumerateArray().Select(t => t.GetProperty("tier").GetInt32()).Should().Equal(1, 3, 4);
        report.GetProperty("misroutes").GetArrayLength().Should().Be(report.GetProperty("total").GetInt32() - report.GetProperty("correct").GetInt32());
        JsonElement miss = report.GetProperty("misroutes")[0];
        miss.GetProperty("reason").GetString().Should().NotBeNullOrEmpty();
        miss.GetProperty("topScores")[0].GetProperty("route").GetString().Should().NotBeNullOrEmpty();
        report.GetProperty("localOnlyMissed").GetInt32().Should().Be(1, "no route keeps the patient record local");
        report.GetProperty("embeddings").GetString().Should().StartWith("Hashing");

        JsonElement calibration = report.GetProperty("calibration");
        calibration.GetProperty("current").GetProperty("threshold").GetSingle().Should().Be(0.95f);
        calibration.GetProperty("recommended").GetProperty("correct").GetInt32()
            .Should().BeGreaterThan(calibration.GetProperty("current").GetProperty("correct").GetInt32());
        calibration.GetProperty("sweep").GetArrayLength().Should().BeGreaterThan(100);
    }

    [Fact]
    public async Task Keeps_the_set_labels_activity_and_exports_without_local_only_prompts()
    {
        HttpClient client = await StartRealAsync();

        (await client.PostAsync("/router/api/evaluate", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "the stored set is empty");
        JsonElement stored = await JsonOf(await client.PutAsync("/router/api/evaluation/set", Text(Set)));
        stored.GetProperty("count").GetInt32().Should().Be(5);

        JsonElement relabelled = await JsonOf(await client.PostAsJsonAsync("/router/api/evaluation/cases", new { prompt = "good morning to you", tier = 2 }));
        relabelled.GetProperty("count").GetInt32().Should().Be(5, "a prompt labelled again replaces its label");
        relabelled.GetProperty("cases")[4].GetProperty("tier").GetInt32().Should().Be(2);
        JsonElement report = await JsonOf(await client.PostAsync("/router/api/evaluate", null));
        report.GetProperty("total").GetInt32().Should().Be(5, "an empty body evaluates the stored set");

        string export = await client.GetStringAsync("/router/api/evaluation/set/export");
        string full = await client.GetStringAsync("/router/api/evaluation/set/export?localOnly=true");
        export.Should().NotContain("patient record");
        EvaluationSet.Parse(export).Should().HaveCount(4);
        EvaluationSet.Parse(full).Should().HaveCount(5);

        (await client.PutAsync("/router/api/evaluation/set", Text("""{"prompt":"x"}"""))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_set_is_kept_next_to_the_configuration_file()
    {
        string directory = Directory.CreateTempSubdirectory("router-eval-").FullName;
        try
        {
            string file = Path.Combine(directory, "router.json");
            HttpClient client = await StartAsync(services: s => s.AddSemanticPromptRouting(_ => { }).UseConfigurationFile(file));

            await JsonOf(await client.PutAsync("/router/api/evaluation/set", Text(Set)));

            string stored = Path.Combine(directory, "router.evaluation.jsonl");
            File.Exists(stored).Should().BeTrue();
            EvaluationSet.Parse(await File.ReadAllTextAsync(stored)).Should().HaveCount(5);
            Services.GetRequiredService<IEvaluationSetStore>().Location.Should().Be(stored);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Adding_a_prompt_as_an_utterance_changes_its_next_classification_without_a_restart()
    {
        HttpClient client = await StartRealAsync();
        const string prompt = "bake a sourdough loaf at home";

        JsonElement before = await JsonOf(await client.PostAsJsonAsync("/router/api/classify", new { prompt }));
        before.GetProperty("reason").GetString().Should().Be("NoMatchFallback");

        JsonElement added = await JsonOf(await client.PostAsJsonAsync("/router/api/routes/smalltalk/utterances", new { utterance = prompt }));
        added.GetProperty("matchedRoute").GetString().Should().Be("smalltalk");
        added.GetProperty("utterances").GetInt32().Should().Be(4);

        JsonElement after = await JsonOf(await client.PostAsJsonAsync("/router/api/classify", new { prompt }));
        after.GetProperty("route").GetString().Should().Be("smalltalk");
        after.GetProperty("requiredTier").GetInt32().Should().Be(1);

        (await client.PostAsJsonAsync("/router/api/routes/cooking/utterances", new { utterance = "a recipe for pancakes" }))
            .StatusCode.Should().Be(HttpStatusCode.NotFound, "a new route needs a tier");
        JsonElement created = await JsonOf(await client.PostAsJsonAsync("/router/api/routes/cooking/utterances", new { utterance = "a recipe for pancakes", tier = 2 }));
        created.GetProperty("route").GetString().Should().Be("cooking");
        created.GetProperty("tier").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task Reports_route_health()
    {
        HttpClient client = await StartRealAsync();

        JsonElement health = await JsonOf(await client.GetAsync("/router/api/routes/health"));

        health.GetProperty("utteranceCount").GetInt32().Should().Be(5);
        health.GetProperty("findings").EnumerateArray().Where(f => f.GetProperty("kind").GetString() == "FewUtterances")
            .Select(f => f.GetProperty("route").GetString()).Should().BeEquivalentTo("smalltalk", "reasoning");
        health.GetProperty("overlaps").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Compares_the_current_embeddings_with_another_provider()
    {
        HttpClient client = await StartRealAsync();

        JsonElement comparison = await JsonOf(await client.PostAsJsonAsync("/router/api/evaluate/compare", new
        {
            text = Set,
            embeddings = new { provider = "Hashing", dimensions = 64 },
        }));

        comparison.GetProperty("current").GetProperty("report").GetProperty("total").GetInt32().Should().Be(5);
        comparison.GetProperty("candidate").GetProperty("embeddings").GetString().Should().Be("Hashing (64 dimensions)");
        comparison.GetProperty("candidate").GetProperty("report").GetProperty("calibration").GetProperty("recommended").ValueKind.Should().Be(JsonValueKind.Object);

        JsonElement broken = await JsonOf(await client.PostAsJsonAsync("/router/api/evaluate/compare", new
        {
            text = Set,
            embeddings = new { provider = "OpenAICompatible", endpoint = "http://127.0.0.1:9", model = "nope", timeout = "00:00:02" },
        }));
        broken.GetProperty("candidate").GetProperty("error").GetString().Should().NotBeNullOrEmpty();
        broken.GetProperty("candidate").GetProperty("report").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_quick_retry_of_the_same_prompt_goes_one_tier_up_when_enabled()
    {
        HttpClient client = await StartAsync(o =>
        {
            o.Mode = PromptRouterMode.Dispatch;
            o.PathPrefixes.Add("/v1/chat/completions");
            o.RetryEscalation.Enabled = true;
        });

        HttpResponseMessage first = await client.PostAsync("/v1/chat/completions", Json(ChatJson));
        HttpResponseMessage retry = await client.PostAsync("/v1/chat/completions", Json(ChatJson));
        HttpResponseMessage other = await client.PostAsync("/v1/chat/completions", Json(ChatJson.Replace("hello there", "something else", StringComparison.Ordinal)));

        first.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("local");
        retry.Headers.GetValues("X-Prompt-Route-Reason").Should().Equal("RetryEscalation");
        retry.Headers.GetValues("X-Prompt-Route-Tier").Should().Equal("2");
        retry.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("cloud");
        other.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("local");

        // "cloud" (tier 4) is the top of the catalog: a retry of its answer has nowhere higher to go.
        HttpResponseMessage again = await client.PostAsync("/v1/chat/completions", Json(ChatJson));
        again.Headers.GetValues("X-Prompt-Route-Reason").Should().Equal("SemanticMatch");
    }

    [Fact]
    public async Task Retries_are_not_escalated_by_default()
    {
        HttpClient client = await StartAsync(o =>
        {
            o.Mode = PromptRouterMode.Dispatch;
            o.PathPrefixes.Add("/v1/chat/completions");
        });

        await client.PostAsync("/v1/chat/completions", Json(ChatJson));
        HttpResponseMessage retry = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        retry.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("local");
    }
}
