using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Automatic tuning (#45) through the router: changes are applied as configuration versions, listed with what, why and
/// when, linked from the decisions they change, and undone one at a time, all at once, or paused.
/// </summary>
public sealed class TuningTests : RouterHostTest
{
    private readonly Embeddings _embeddings = new();

    /// <summary>
    /// Small talk ("hello") uses the default threshold, 0.80; reasoning ("design a system") has its own, 0.90.
    /// Small-talk prompts sit at 0.765 (right from 0.76), reasoning prompts at 0.825 (right from 0.82).
    /// </summary>
    private async Task<HttpClient> StartTunedAsync(Action<TuningOptions>? tuning = null, int smallTalk = 3, int reasoning = 3, bool localSmallTalk = false)
    {
        var cases = new List<EvaluationCase> { new(_embeddings.At("what is the weather in paris", 0, 0.3f), 3) };
        for (int i = 0; i < smallTalk; i++)
        {
            cases.Add(new EvaluationCase(_embeddings.At($"hi there {i}", 0, 0.765f), 1) { ExpectedRoute = "smalltalk" });
        }

        for (int i = 0; i < reasoning; i++)
        {
            cases.Add(new EvaluationCase(_embeddings.At($"design the ledger {i}", 2, 0.825f), 4) { ExpectedRoute = "reasoning" });
        }

        HttpClient client = await StartAsync(
            mockClassifier: false,
            routing: o =>
            {
                o.DefaultSimilarityThreshold = 0.80f;
                o.FallbackTier = 3;
                RouteDefinition smallTalkRoute = Route("smalltalk", 1, "hello");
                smallTalkRoute.LocalOnly = localSmallTalk;
                o.Routes.Add(smallTalkRoute);
                RouteDefinition reasoningRoute = Route("reasoning", 4, "design a system");
                reasoningRoute.SimilarityThreshold = 0.90f;
                o.Routes.Add(reasoningRoute);
            },
            services: s =>
            {
                s.AddSingleton<IEmbeddingProvider>(_embeddings);
                s.Configure<TuningOptions>(t =>
                {
                    t.MinimumPrompts = 4;
                    t.Utterances = false;
                    tuning?.Invoke(t);
                });
            });
        Services.GetRequiredService<IEvaluationSetStore>().Save(cases);
        return client;
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<JsonElement> RunAsync(HttpClient client) => await JsonOf(await client.PostAsync("/router/api/tuning/run", null));

    private static async Task<(float Default, float? Reasoning)> ThresholdsAsync(HttpClient client)
    {
        JsonElement config = (await JsonOf(await client.GetAsync("/router/api/config"))).GetProperty("config");
        JsonElement reasoning = config.GetProperty("Routes").EnumerateArray().Single(r => r.GetProperty("Name").GetString() == "reasoning");
        return (
            config.GetProperty("DefaultSimilarityThreshold").GetSingle(),
            reasoning.TryGetProperty("SimilarityThreshold", out JsonElement own) && own.ValueKind == JsonValueKind.Number ? own.GetSingle() : null);
    }

    private static JsonElement[] Changes(JsonElement tuning) => [.. tuning.GetProperty("changes").EnumerateArray()];

    [Fact]
    public async Task Each_change_is_a_configuration_version_and_is_listed_with_what_why_and_when()
    {
        HttpClient client = await StartTunedAsync();

        JsonElement run = await RunAsync(client);

        run.GetProperty("changes").GetArrayLength().Should().Be(2, run.GetProperty("result").GetString());
        (await ThresholdsAsync(client)).Should().Be((0.78f, 0.88f), "one bounded step each");

        JsonElement tuning = await JsonOf(await client.GetAsync("/router/api/tuning"));
        tuning.GetProperty("mode").GetString().Should().Be("Apply");
        tuning.GetProperty("thisWeek").GetInt32().Should().Be(2);
        JsonElement[] changes = [.. Changes(tuning)];
        changes.Select(c => c.GetProperty("summary").GetString()).Should().Equal("threshold of reasoning 0.90 → 0.88", "default threshold 0.80 → 0.78");
        JsonElement first = changes[1];
        first.GetProperty("state").GetString().Should().Be("Applied");
        first.GetProperty("target").GetSingle().Should().Be(0.76f);
        first.GetProperty("why").GetString().Should()
            .Contain("At 0.76, 3 labelled prompts route to the tier they need that do not now (3 from the evaluation set)")
            .And.Contain("none would route lower than they need")
            .And.Contain("moves 0.02 per run");
        first.GetProperty("examples").GetArrayLength().Should().Be(3);

        JsonElement history = await JsonOf(await client.GetAsync("/router/api/config/history"));
        JsonElement[] versions = [.. history.GetProperty("versions").EnumerateArray()];
        versions[0].GetProperty("source").GetString().Should().Be("auto-tune");
        versions[0].GetProperty("actor").GetString().Should().Be("automatic tuning");
        versions[0].GetProperty("note").GetString().Should().StartWith("change 2: threshold of reasoning 0.90 → 0.88. At 0.82");
        versions[1].GetProperty("note").GetString().Should().StartWith("change 1: default threshold 0.80 → 0.78.");
        first.GetProperty("version").GetInt64().Should().Be(versions[1].GetProperty("id").GetInt64());

        // It keeps stepping until it reaches calibration's values, then stops.
        for (int i = 0; i < 6; i++)
        {
            await RunAsync(client);
        }

        (await ThresholdsAsync(client)).Should().Be((0.76f, 0.82f));
        JsonElement done = await RunAsync(client);
        done.GetProperty("changes").GetArrayLength().Should().Be(0);
        done.GetProperty("result").GetString().Should().StartWith("Nothing to change: 7 of 7 prompts");
    }

    [Fact]
    public async Task A_change_counts_local_only_prompts_but_does_not_quote_them()
    {
        HttpClient client = await StartTunedAsync(smallTalk: 1, reasoning: 0, localSmallTalk: true);
        IEvaluationSetStore labels = Services.GetRequiredService<IEvaluationSetStore>();
        const string Secret = "LOCAL-SECRET";
        labels.Save([.. labels.Load(), .. Enumerable.Range(1, 2).Select(i => new EvaluationCase(_embeddings.At($"{Secret} hi {i}", 0, 0.765f), 1) { ExpectedRoute = "smalltalk", LocalOnly = true })]);

        JsonElement change = Changes(await RunAsync(client)).Single();

        change.GetProperty("why").GetString().Should()
            .Contain("3 labelled prompts route to the tier they need that do not now (3 from the evaluation set), 2 local-only prompts not quoted, e.g. \"hi there 0\"");
        change.GetProperty("examples").EnumerateArray().Select(x => x.GetString()).Should().Equal("hi there 0");
        // The text is never stored, so no role reads it: not the learned list, nor the applied change's history note.
        (await client.GetStringAsync("/router/api/tuning")).Should().NotContain(Secret);
        (await client.GetStringAsync("/router/api/config/history")).Should().NotContain(Secret).And.Contain("2 local-only prompts not quoted");
    }

    [Fact]
    public async Task Undo_restores_only_that_setting_and_it_is_not_applied_again_within_the_hold()
    {
        HttpClient client = await StartTunedAsync();
        await RunAsync(client);
        await RunAsync(client);
        (await ThresholdsAsync(client)).Should().Be((0.76f, 0.86f), "the default reaches 0.76 in two steps");
        long[] ids = [.. Changes(await JsonOf(await client.GetAsync("/router/api/tuning"))).Select(c => c.GetProperty("id").GetInt64())];
        ids.Should().Equal(4, 3, 2, 1);

        // Undo the first default step: the later step of the same threshold goes too; reasoning stays where it is.
        JsonElement undone = await JsonOf(await client.PostAsync("/router/api/tuning/changes/1/undo", null));

        undone.GetProperty("state").GetString().Should().Be("Undone");
        (await ThresholdsAsync(client)).Should().Be((0.80f, 0.86f));
        JsonElement[] after = [.. Changes(await JsonOf(await client.GetAsync("/router/api/tuning")))];
        after.Select(c => (c.GetProperty("id").GetInt64(), c.GetProperty("state").GetString()))
            .Should().Equal((4L, "Applied"), (3L, "Undone"), (2L, "Applied"), (1L, "Undone"));
        JsonElement version = (await JsonOf(await client.GetAsync("/router/api/config/history"))).GetProperty("versions")[0];
        (version.GetProperty("source").GetString(), version.GetProperty("note").GetString())
            .Should().Be(("auto-tune undo", "undo of auto-tune change 1: default threshold 0.80 → 0.78; not saved"), "the test host has no configuration file");

        // The next run keeps tuning reasoning but leaves the default alone.
        JsonElement run = await RunAsync(client);

        run.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("route").GetString()).Should().Equal("reasoning");
        (await ThresholdsAsync(client)).Should().Be((0.80f, 0.84f));
        (await client.PostAsync("/router/api/tuning/changes/1/undo", null)).StatusCode.Should().Be(HttpStatusCode.Conflict, "it is undone already");
        (await client.PostAsync("/router/api/tuning/changes/99/undo", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_setting_someone_changed_since_is_not_overwritten_by_an_undo()
    {
        HttpClient client = await StartTunedAsync();
        await RunAsync(client);
        JsonNode config = JsonNode.Parse((await JsonOf(await client.GetAsync("/router/api/config"))).GetProperty("config").GetRawText())!;
        config["DefaultSimilarityThreshold"] = 0.7f;
        await JsonOf(await client.PutAsync("/router/api/config", Body(config.ToJsonString())));

        HttpResponseMessage undo = await client.PostAsync("/router/api/tuning/changes/1/undo", null);

        undo.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await undo.Content.ReadAsStringAsync()).Should().Contain("changed since (it is 0.70)");
        (await ThresholdsAsync(client)).Default.Should().Be(0.70f);
    }

    [Fact]
    public async Task Undo_all_and_pause_work_from_the_api()
    {
        HttpClient client = await StartTunedAsync();
        await RunAsync(client);

        JsonElement paused = await JsonOf(await client.PostAsync("/router/api/tuning/pause", null));
        JsonElement idle = await RunAsync(client);
        paused.GetProperty("paused").GetBoolean().Should().BeTrue();
        idle.GetProperty("result").GetString().Should().Be("Learning is paused.");
        (await ThresholdsAsync(client)).Should().Be((0.78f, 0.88f));
        (await JsonOf(await client.GetAsync("/router/api/tuning"))).GetProperty("nextRun").ValueKind.Should().Be(JsonValueKind.Null);

        JsonElement all = await JsonOf(await client.PostAsync("/router/api/tuning/undo-all", null));

        all.GetProperty("undone").GetInt32().Should().Be(2);
        (await ThresholdsAsync(client)).Should().Be((0.80f, 0.90f));
        Changes(await JsonOf(await client.GetAsync("/router/api/tuning"))).Should().OnlyContain(c => c.GetProperty("state").GetString() == "Undone");
        JsonElement version = (await JsonOf(await client.GetAsync("/router/api/config/history"))).GetProperty("versions")[0];
        version.GetProperty("note").GetString().Should().Be("undo of all automatic changes (2); not saved");

        await JsonOf(await client.PostAsync("/router/api/tuning/resume", null));
        (await RunAsync(client)).GetProperty("changes").GetArrayLength().Should().Be(0, "both settings are held after the undo");
    }

    [Fact]
    public async Task In_suggest_mode_changes_wait_until_someone_applies_or_dismisses_them()
    {
        HttpClient client = await StartTunedAsync(t => t.Mode = TuningMode.Suggest);

        JsonElement run = await RunAsync(client);

        run.GetProperty("changes").EnumerateArray().Should().OnlyContain(c => c.GetProperty("state").GetString() == "Suggested").And.HaveCount(2);
        (await ThresholdsAsync(client)).Should().Be((0.80f, 0.90f));
        (await RunAsync(client)).GetProperty("changes").GetArrayLength().Should().Be(0, "the same suggestions are not made twice");

        JsonElement applied = await JsonOf(await client.PostAsync("/router/api/tuning/changes/1/apply", null));
        JsonElement dismissed = await JsonOf(await client.PostAsync("/router/api/tuning/changes/2/dismiss", null));

        applied.GetProperty("state").GetString().Should().Be("Applied");
        dismissed.GetProperty("state").GetString().Should().Be("Dismissed");
        (await ThresholdsAsync(client)).Should().Be((0.78f, 0.90f));
        JsonElement tuning = await JsonOf(await client.GetAsync("/router/api/tuning"));
        tuning.GetProperty("suggestions").GetInt32().Should().Be(0);
        JsonElement next = await RunAsync(client);
        next.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("summary").GetString())
            .Should().Equal(["default threshold 0.78 → 0.76"], "the dismissed reasoning change is held");
    }

    [Fact]
    public async Task A_decision_a_learned_change_routed_differently_links_to_it()
    {
        HttpClient client = await StartTunedAsync(smallTalk: 3, reasoning: 0);
        await RunAsync(client);
        string near = _embeddings.At("hi friend", 0, 0.785f), clear = _embeddings.At("hello hello", 0, 0.95f);

        await client.PostAsync("/v1/chat/completions", Body(JsonSerializer.Serialize(new { model = "auto", messages = new[] { new { role = "user", content = near } } })));
        await client.PostAsync("/v1/chat/completions", Body(JsonSerializer.Serialize(new { model = "auto", messages = new[] { new { role = "user", content = clear } } })));

        JsonElement[] entries = [.. (await JsonOf(await client.GetAsync("/router/api/activity"))).GetProperty("entries").EnumerateArray()];
        JsonElement linked = entries.Single(e => e.GetProperty("promptPreview").GetString() == near);
        linked.GetProperty("route").GetString().Should().Be("smalltalk");
        linked.GetProperty("learned").GetInt64().Should().Be(1);
        linked.GetProperty("learnedNote").GetString().Should().EndWith(": default threshold 0.80 → 0.78");
        entries.Single(e => e.GetProperty("promptPreview").GetString() == clear).GetProperty("learned").ValueKind
            .Should().Be(JsonValueKind.Null, "0.95 matched before the change too");
    }

    [Fact]
    public async Task Feedback_labels_prompts_and_the_reason_says_so()
    {
        HttpClient client = await StartTunedAsync(t => t.MinimumPrompts = 2, smallTalk: 0, reasoning: 0);
        var activity = Services.GetRequiredService<RoutingActivityLog>();
        var feedback = Services.GetRequiredService<RoutingFeedbackLog>();
        for (int i = 0; i < 3; i++)
        {
            // Reasoning prompts below reasoning's threshold fell back to tier 3, and were retried: they needed tier 4.
            string prompt = _embeddings.At($"design the payments ledger {i}", 2, 0.825f);
            RoutingActivityEntry entry = activity.Add(new RoutingActivityEntry
            {
                Id = activity.Reserve(),
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5),
                PromptPreview = prompt,
                Tier = 3,
                Reason = RoutingReason.NoMatchFallback,
            });
            feedback.Add(new RoutingFeedback { Timestamp = DateTimeOffset.UtcNow.AddMinutes(-4), EntryId = entry.Id, Kind = FeedbackKind.Retried, Tier = 3, NeededTier = 4 });
        }

        JsonElement run = await RunAsync(client);

        JsonElement change = run.GetProperty("changes").EnumerateArray().Single();
        change.GetProperty("route").GetString().Should().Be("reasoning");
        change.GetProperty("why").GetString().Should().Contain("3 labelled prompts route to the tier they need that do not now (3 from feedback: retried)");
        (await ThresholdsAsync(client)).Reasoning.Should().Be(0.88f);
    }

    /// <summary>
    /// Four graded answers whose prompts are labelled by hand (agreeing with the grade or not), and three reasoning
    /// prompts the judge graded low: they needed tier 4, which only a lower reasoning threshold gives them.
    /// </summary>
    private async Task<HttpClient> StartJudgedAsync(bool agrees)
    {
        HttpClient client = await StartTunedAsync(t => (t.MinimumPrompts, t.JudgeAgreementPairs) = (2, 4), smallTalk: 0, reasoning: 0);
        var activity = Services.GetRequiredService<RoutingActivityLog>();
        var feedback = Services.GetRequiredService<RoutingFeedbackLog>();
        IEvaluationSetStore labels = Services.GetRequiredService<IEvaluationSetStore>();
        var cases = labels.Load().ToList();
        for (int i = 0; i < 4; i++)
        {
            // "local" (tier 1) answered and the judge graded it 2: the label agrees when it asks for more than tier 1.
            string prompt = agrees ? _embeddings.At($"what happened in the outage {i}", 0, 0.3f) : _embeddings.At($"hi again {i}", 0, 0.9f);
            RoutingActivityEntry entry = activity.Add(new RoutingActivityEntry
            {
                Id = activity.Reserve(),
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-10),
                PromptPreview = prompt,
                Tier = 1,
                Reason = RoutingReason.SemanticMatch,
                ServedBy = "local",
                Grade = 2,
            });
            cases.Add(new EvaluationCase(prompt, agrees ? 3 : 1) { Note = $"labelled from activity #{entry.Id}" });
        }

        labels.Save(cases);
        for (int i = 0; i < 3; i++)
        {
            string prompt = _embeddings.At($"design the payments ledger {i}", 2, 0.825f);
            RoutingActivityEntry entry = activity.Add(new RoutingActivityEntry
            {
                Id = activity.Reserve(),
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5),
                PromptPreview = prompt,
                Tier = 3,
                Reason = RoutingReason.NoMatchFallback,
                ServedBy = "local",
                Grade = 1,
            });
            feedback.Add(new RoutingFeedback { Timestamp = DateTimeOffset.UtcNow.AddMinutes(-4), EntryId = entry.Id, Kind = FeedbackKind.Judged, Tier = 3, NeededTier = 4 });
        }

        return client;
    }

    [Fact]
    public async Task A_judge_that_agrees_with_the_hand_labels_keeps_its_weight_and_the_reason_says_so()
    {
        HttpClient client = await StartJudgedAsync(agrees: true);

        JsonElement agreement = await JsonOf(await client.GetAsync("/router/api/judge/agreement"));
        JsonElement run = await RunAsync(client);

        (agreement.GetProperty("pairs").GetInt32(), agreement.GetProperty("agreeing").GetInt32(), agreement.GetProperty("rate").GetDouble()).Should().Be((4, 4, 1.0));
        agreement.GetProperty("effectiveWeight").GetDouble().Should().Be(0.75);
        JsonElement change = run.GetProperty("changes").EnumerateArray().Single(c => c.GetProperty("route").GetString() == "reasoning");
        change.GetProperty("why").GetString().Should().Contain("graded low by the judge (counted at 0.75: its grades agree with 4 of 4 hand labels)");
        (await ThresholdsAsync(client)).Reasoning.Should().Be(0.88f);
    }

    [Fact]
    public async Task A_judge_that_disagrees_with_the_hand_labels_counts_for_less()
    {
        HttpClient client = await StartJudgedAsync(agrees: false);

        JsonElement agreement = await JsonOf(await client.GetAsync("/router/api/judge/agreement"));
        JsonElement run = await RunAsync(client);

        (agreement.GetProperty("pairs").GetInt32(), agreement.GetProperty("agreeing").GetInt32()).Should().Be((4, 0));
        (agreement.GetProperty("lowGrades").GetInt32(), agreement.GetProperty("lowAgreeing").GetInt32()).Should().Be((4, 0));
        agreement.GetProperty("effectiveWeight").GetDouble().Should().Be(0, "it agrees with none of the hand labels");
        run.GetProperty("changes").EnumerateArray().Should().NotContain(c => c.GetProperty("route").GetString() == "reasoning", "the judge's labels count for nothing");
        (await ThresholdsAsync(client)).Reasoning.Should().Be(0.90f);
    }

    [Fact]
    public async Task A_labelled_prompt_its_route_misses_becomes_an_utterance_and_undo_removes_it()
    {
        HttpClient client = await StartTunedAsync(t => t.Utterances = true, smallTalk: 0, reasoning: 0);
        IEvaluationSetStore labels = Services.GetRequiredService<IEvaluationSetStore>();
        // A prompt no utterance is near, and one close to it (0.866): as an utterance it routes both.
        const string prompt = "ship the release notes";
        _embeddings.Set(prompt, [0f, 0f, 0f, 0f, 0f, 1f]);
        _embeddings.Set("draft the release notes", [0f, 0f, 0f, 0f, 0.5f, 0.866f]);
        labels.Save([.. labels.Load(), new EvaluationCase(prompt, 1) { ExpectedRoute = "smalltalk" }, new EvaluationCase("draft the release notes", 1), new EvaluationCase(_embeddings.At("good day", 0, 0.9f), 1)]);

        JsonElement run = await RunAsync(client);

        JsonElement change = run.GetProperty("changes").EnumerateArray().Single();
        (change.GetProperty("kind").GetString(), change.GetProperty("utterance").GetString()).Should().Be(("Utterance", prompt));
        change.GetProperty("why").GetString().Should().StartWith("\"ship the release notes\" is labelled smalltalk (tier 1) but routes to no route (tier 3).")
            .And.Contain("routes 2 labelled prompts to their tier");
        string[] utterances = await UtterancesAsync(client);
        utterances.Should().Equal("hello", prompt);

        await JsonOf(await client.PostAsync($"/router/api/tuning/changes/{change.GetProperty("id").GetInt64()}/undo", null));

        (await UtterancesAsync(client)).Should().Equal("hello");
        (await RunAsync(client)).GetProperty("changes").GetArrayLength().Should().Be(0, "an undone utterance is held");
    }

    private static async Task<string[]> UtterancesAsync(HttpClient client)
    {
        JsonElement config = (await JsonOf(await client.GetAsync("/router/api/config"))).GetProperty("config");
        return [.. config.GetProperty("Routes").EnumerateArray().Single(r => r.GetProperty("Name").GetString() == "smalltalk")
            .GetProperty("Utterances").EnumerateArray().Select(u => u.GetString()!)];
    }

    /// <summary>
    /// Embeddings from a table: route utterances on their own axes ("hello" 0, "design a system" 2), prompts placed at a
    /// chosen similarity from one of them (the rest on axis 4), and anything else on axis 3, which no route uses.
    /// </summary>
    private sealed class Embeddings : IEmbeddingProvider
    {
        private const int Dimensions = 6;
        private readonly Dictionary<string, float[]> _table = new(StringComparer.Ordinal)
        {
            ["hello"] = [1f, 0f, 0f, 0f, 0f, 0f],
            ["design a system"] = [0f, 0f, 1f, 0f, 0f, 0f],
        };

        /// <summary>A prompt at <paramref name="similarity"/> from the utterance on <paramref name="axis"/>; the rest on axis 4.</summary>
        public string At(string prompt, int axis, float similarity)
        {
            var vector = new float[Dimensions];
            vector[axis] = similarity;
            vector[4] = MathF.Sqrt(1 - (similarity * similarity));
            Set(prompt, vector);
            return prompt;
        }

        public void Set(string text, float[] vector)
        {
            lock (_table)
            {
                _table[text] = vector;
            }
        }

        public ValueTask<ReadOnlyMemory<float>> GenerateEmbeddingAsync(string text, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ReadOnlyMemory<float>>(Lookup(text));

        public ValueTask<IReadOnlyList<ReadOnlyMemory<float>>> GenerateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>([.. texts.Select(t => new ReadOnlyMemory<float>(Lookup(t)))]);

        private float[] Lookup(string text)
        {
            lock (_table)
            {
                return _table.TryGetValue(text, out float[]? vector) ? vector : [0f, 0f, 0f, 1f, 0f, 0f];
            }
        }
    }
}
