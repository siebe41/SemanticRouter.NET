using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// The optional judge (#48): a stand-in judge model grades a sample of cheap answers; the sample rate, the daily limit
/// and the local-only rule hold, nothing is graded when it is off, and a cloud judge's calls show in usage.
/// </summary>
public sealed class JudgeTests : RouterHostTest
{
    private const string Prompt = "what is the capital of France?";
    private const string Answer = "The capital of France is Lyon.";

    private readonly ManualTimeProvider _time = new();

    public JudgeTests()
    {
        LocalHttp = Backend("judge-32b", grade: 2);
        CloudHttp = Backend("cloud-judge-1", grade: 4);
    }

    /// <summary>A backend that answers the judge's requests (by upstream model name) with a grade and others with <see cref="Answer"/>.</summary>
    private static StubHttpMessageHandler Backend(string judge, int grade) => new((_, body) =>
    {
        string content = body.Contains($"\"model\":\"{judge}\"", StringComparison.Ordinal)
            ? $$"""{\"grade\": {{grade}}, \"reason\": \"names the wrong city\"}"""
            : Answer;
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $$$"""{"id":"x","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"{{{content}}}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1000,"completion_tokens":20}}""",
                Encoding.UTF8,
                "application/json"),
        };
    });

    private static int JudgeCalls(StubHttpMessageHandler backend, string judge) =>
        backend.Requests.Count(r => r.Body.Contains($"\"model\":\"{judge}\"", StringComparison.Ordinal));

    private AnswerJudge Judge => Services.GetRequiredService<AnswerJudge>();

    private Task<HttpClient> StartJudgedAsync(Action<JudgeOptions> judge, Action<PromptRouterMiddlewareOptions>? middleware = null) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
                o.Judge.Model = "judge";
                judge(o.Judge);
                middleware?.Invoke(o);
            },
            routing: o =>
            {
                ModelDefinition local = Model("judge", 3, local: true, connector: "lmstudio");
                local.Model = "judge-32b";
                ModelDefinition cloud = Model("cloud-judge", 4, costPerMillion: 3m, connector: "anthropic");
                cloud.Model = "cloud-judge-1";
                o.Models.Add(local);
                o.Models.Add(cloud);
            },
            services: s => s.AddSingleton<TimeProvider>(_time));

    private async Task AskAsync(HttpClient client, string prompt = Prompt, string model = "auto")
    {
        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions", Json($$"""{"model":"{{model}}","messages":[{"role":"user","content":"{{prompt}}"}]}"""));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain(Answer, "the client gets its answer whether or not it is graded");
        await Judge.WhenIdleAsync(TimeSpan.FromSeconds(10));
    }

    private RoutingActivityEntry Latest => Services.GetRequiredService<RoutingActivityLog>().Recent(1).Single();

    [Fact]
    public async Task A_sampled_answer_is_graded_and_a_low_grade_becomes_feedback()
    {
        HttpClient client = await StartJudgedAsync(j => (j.Enabled, j.SampleRate) = (true, 1));

        await AskAsync(client);

        RoutingActivityEntry entry = Latest;
        (entry.ServedBy, entry.Grade).Should().Be(("local", 2));
        entry.GradeNote.Should().Be("judge: names the wrong city");
        string request = LocalHttp.Requests.Single(r => r.Body.Contains("\"model\":\"judge-32b\"", StringComparison.Ordinal)).Body;
        request.Should().Contain(Prompt).And.Contain(Answer);

        RoutingFeedback feedback = Services.GetRequiredService<RoutingFeedbackLog>().Recent().Should().ContainSingle().Subject;
        (feedback.Kind, feedback.EntryId, feedback.Model, feedback.Tier, feedback.NeededTier).Should().Be((FeedbackKind.Judged, entry.Id, "local", 1, 2));
        feedback.Detail.Should().Contain("graded 2/5");
    }

    [Fact]
    public async Task No_judge_calls_when_it_is_off()
    {
        HttpClient client = await StartJudgedAsync(j => (j.Enabled, j.SampleRate) = (false, 1));

        for (int i = 0; i < 5; i++)
        {
            await AskAsync(client);
        }

        JudgeCalls(LocalHttp, "judge-32b").Should().Be(0);
        LocalHttp.Requests.Should().HaveCount(5);
        Services.GetRequiredService<RoutingActivityLog>().Recent().Should().OnlyContain(e => e.Grade == null);
    }

    [Fact]
    public async Task The_sample_rate_holds()
    {
        const int Requests = 200;
        HttpClient client = await StartJudgedAsync(j => (j.Enabled, j.SampleRate, j.DailyLimit) = (true, 0.25, 1000));
        Judge.Random = new Random(48);
        var replay = new Random(48);
        int expected = Enumerable.Range(0, Requests).Count(_ => replay.NextDouble() < 0.25);

        for (int i = 0; i < Requests; i++)
        {
            await AskAsync(client, $"question number {i}");
        }

        JudgeCalls(LocalHttp, "judge-32b").Should().Be(expected, "one draw per eligible answer");
        ((double)expected / Requests).Should().BeInRange(0.15, 0.35);
        Judge.Today.Should().Be(expected);
    }

    [Fact]
    public async Task The_daily_limit_holds_and_starts_again_the_next_day()
    {
        HttpClient client = await StartJudgedAsync(j => (j.Enabled, j.SampleRate, j.DailyLimit) = (true, 1, 3));

        for (int i = 0; i < 6; i++)
        {
            await AskAsync(client, $"question number {i}");
        }

        JudgeCalls(LocalHttp, "judge-32b").Should().Be(3);
        Judge.Today.Should().Be(3);

        _time.Advance(TimeSpan.FromDays(1));
        await AskAsync(client, "one more question");
        JudgeCalls(LocalHttp, "judge-32b").Should().Be(4);
        Judge.Today.Should().Be(1);
    }

    [Fact]
    public async Task The_daily_limit_holds_across_a_restart_with_a_state_store()
    {
        var store = new MemoryStateValues();
        Task<HttpClient> Start() => StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
                (o.Judge.Enabled, o.Judge.Model, o.Judge.SampleRate, o.Judge.DailyLimit) = (true, "judge", 1, 2);
            },
            routing: o =>
            {
                ModelDefinition judge = Model("judge", 3, local: true, connector: "lmstudio");
                judge.Model = "judge-32b";
                o.Models.Add(judge);
            },
            services: s => s.AddSingleton<TimeProvider>(_time).AddSingleton<IStateValueStore>(store));

        HttpClient client = await Start();
        await AskAsync(client, "first question");
        await AskAsync(client, "second question");
        JudgeCalls(LocalHttp, "judge-32b").Should().Be(2);

        await DisposeAsync();
        client = await Start();
        await AskAsync(client, "a question after the restart");

        JudgeCalls(LocalHttp, "judge-32b").Should().Be(2, "today's two calls were remembered");
        store.Values[AnswerJudge.StoreKey].Should().Be("2026-10-07 2");
    }

    private sealed class MemoryStateValues : IStateValueStore
    {
        public Dictionary<string, string> Values { get; } = [];

        public string? Load(string key) => Values.GetValueOrDefault(key);

        public void Save(string key, string value) => Values[key] = value;
    }

        [Fact]
    public async Task A_local_only_request_is_never_sent_to_a_cloud_judge()
    {
        HttpClient client = await StartJudgedAsync(j => (j.Enabled, j.SampleRate, j.Model) = (true, 1, "cloud-judge"));
        ClassifyAs(tier: 1, localOnly: true);

        await AskAsync(client);

        CloudHttp.Requests.Should().BeEmpty("the conversation must stay on local models");
        Latest.Grade.Should().BeNull();

        ClassifyAs(tier: 1);
        await AskAsync(client, "and the capital of Spain?");
        JudgeCalls(CloudHttp, "cloud-judge-1").Should().Be(1);
        Latest.Grade.Should().Be(4);
        Services.GetRequiredService<RoutingFeedbackLog>().Recent().Should().BeEmpty("a good grade is not feedback");
    }

    [Fact]
    public async Task A_local_judge_may_grade_local_only_requests()
    {
        HttpClient client = await StartJudgedAsync(j => (j.Enabled, j.SampleRate) = (true, 1));
        ClassifyAs(tier: 1, localOnly: true);

        await AskAsync(client);

        Latest.Grade.Should().Be(2);
    }

    [Fact]
    public async Task A_cloud_judges_calls_show_in_usage_with_their_cost()
    {
        HttpClient client = await StartJudgedAsync(j => (j.Enabled, j.SampleRate, j.Model) = (true, 1, "cloud-judge"));

        await AskAsync(client);

        IUsageLedger ledger = Services.GetRequiredService<IUsageLedger>();
        DateTimeOffset now = _time.GetUtcNow();
        UsageTotals judged = ledger.Total(now.AddHours(-1), now.AddHours(1), model: "cloud-judge");
        judged.Requests.Should().Be(1);
        judged.CostUsd.Should().Be(1020 * 3m / 1_000_000);
        ledger.Summarize(now.AddHours(-1), now.AddHours(1), UsageGrouping.Route).Select(g => g.Key).Should().Contain(AnswerJudge.UsageLabel);
        ledger.Summarize(now.AddHours(-1), now.AddHours(1), UsageGrouping.Client).Single(g => g.Key == AnswerJudge.UsageLabel).Totals.Requests.Should().Be(1);
    }

    [Fact]
    public async Task Only_cheap_answers_to_the_routers_own_decisions_are_graded()
    {
        HttpClient client = await StartJudgedAsync(j => (j.Enabled, j.SampleRate) = (true, 1));

        ClassifyAs(tier: 4, route: "agentic-coding");
        CloudHttp = Backend("none", grade: 0);
        await AskAsync(client);
        Latest.ServedBy.Should().Be("cloud");

        ClassifyAs(tier: 1);
        await AskAsync(client, model: "local");

        JudgeCalls(LocalHttp, "judge-32b").Should().Be(0, "a tier-4 answer and an explicitly chosen model are not graded");
        Services.GetRequiredService<RoutingActivityLog>().Recent().Should().OnlyContain(e => e.Grade == null);
    }

    [Fact]
    public async Task The_judge_must_name_a_catalog_model()
    {
        Func<Task> start = () => StartJudgedAsync(j => (j.Enabled, j.Model) = (true, "nope"));

        await start.Should().ThrowAsync<Exception>().Where(e => e.ToString().Contains("Middleware:Judge:Model names 'nope'"));
    }

    [Theory]
    [InlineData("""{"grade": 2, "reason": "misses the second question"}""", 2, "misses the second question")]
    [InlineData("```json\n{\"grade\": \"4\"}\n```", 4, null)]
    [InlineData("Grade: 5. Clear and correct.", 5, null)]
    public void Reads_the_judges_grade(string reply, int grade, string? reason) =>
        AnswerJudge.ParseGrade(reply).Should().Be((grade, reason));

    [Theory]
    [InlineData("""{"grade": 9}""")]
    [InlineData("I cannot grade this.")]
    public void A_reply_without_a_grade_gives_none(string reply) =>
        AnswerJudge.ParseGrade(reply).Should().BeNull();

    [Fact]
    public void Reads_the_answer_in_every_protocol_streamed_or_not()
    {
        static (string, bool) Read(ChatProtocol protocol, string reply) => AnswerText.Extract(protocol, Encoding.UTF8.GetBytes(reply));

        Read(ChatProtocol.OpenAI, """{"choices":[{"message":{"content":"Paris."}}]}""").Should().Be(("Paris.", false));
        Read(ChatProtocol.OpenAI, "data: {\"choices\":[{\"delta\":{\"content\":\"Pa\"}}]}\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"ris.\"}}]}\n\ndata: [DONE]\n\n").Should().Be(("Paris.", false));
        Read(ChatProtocol.OpenAI, """{"choices":[{"message":{"content":null,"tool_calls":[{"id":"1"}]}}]}""").Should().Be(("", true));
        Read(ChatProtocol.Anthropic, """{"content":[{"type":"text","text":"Paris."}]}""").Should().Be(("Paris.", false));
        Read(ChatProtocol.Anthropic, "event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Paris.\"}}\n\n").Should().Be(("Paris.", false));
        Read(ChatProtocol.Anthropic, "data: {\"type\":\"content_block_start\",\"content_block\":{\"type\":\"tool_use\"}}\n\n").Should().Be(("", true));
        Read(ChatProtocol.Responses, """{"output":[{"type":"message","content":[{"type":"output_text","text":"Paris."}]}]}""").Should().Be(("Paris.", false));
        Read(ChatProtocol.Responses, "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Paris.\"}\n\n").Should().Be(("Paris.", false));
        Read(ChatProtocol.Responses, """{"output":[{"type":"function_call","name":"ls"}]}""").Should().Be(("", true));
    }
}
