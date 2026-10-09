using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>Routing by the conversation (#41): a floor that holds through short follow-ups, decays, and sticks to a model.</summary>
public sealed class ConversationRoutingTests : RouterHostTest
{
    private const string Task = "refactor the payment module so retries are idempotent and the ledger stays consistent";

    public ConversationRoutingTests()
    {
        // Anything about the refactor needs the frontier tier; everything else classifies as small talk (tier 1).
        Classifier.Setup(c => c.ClassifyAsync(It.Is<string>(p => p.Contains("refactor", StringComparison.Ordinal)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromptClassificationResult
            {
                RequiredTier = 4,
                Reason = RoutingReason.SemanticMatch,
                RouteName = "complex-code",
                Complexity = new ComplexityAnalysis { Score = 0.7, EstimatedTokens = 20 },
            });
    }

    private Task<HttpClient> StartDispatchAsync(Action<PromptRouterMiddlewareOptions>? configure = null, Action<SemanticRouterOptions>? routing = null, Action<IServiceCollection>? services = null) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
                configure?.Invoke(o);
            },
            routing,
            services);

    /// <summary>A chat body whose user turns are <paramref name="userTurns"/>, each answered by the assistant except the last.</summary>
    private static StringContent Conversation(string model, params string[] userTurns)
    {
        var messages = new List<object> { new { role = "system", content = "You are a coding agent." } };
        for (int i = 0; i < userTurns.Length; i++)
        {
            messages.Add(new { role = "user", content = userTurns[i] });
            if (i < userTurns.Length - 1)
            {
                messages.Add(new { role = "assistant", content = "Done: " + i });
            }
        }

        return new StringContent(JsonSerializer.Serialize(new { model, messages }), Encoding.UTF8, "application/json");
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("X-Prompt-Route-" + name, out IEnumerable<string>? values) ? values.Single() : "(none)";

    [Fact]
    public async Task A_short_follow_up_keeps_the_task_on_its_tier_and_model()
    {
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage first = await client.PostAsync("/v1/chat/completions", Conversation("auto", Task));
        HttpResponseMessage followUp = await client.PostAsync("/v1/chat/completions", Conversation("auto", Task, "ok, go ahead"));

        (Header(first, "Tier"), Header(first, "Model")).Should().Be(("4", "cloud"));
        Header(followUp, "Reason").Should().Be("ConversationFloor", "on its own, \"ok, go ahead\" is small talk");
        (Header(followUp, "Tier"), Header(followUp, "Model")).Should().Be(("4", "cloud"));
    }

    [Fact]
    public async Task The_floor_decays_as_the_conversation_moves_on_to_simpler_work()
    {
        HttpClient client = await StartDispatchAsync(o => o.Conversation.DecayTurns = 1);
        string simpler = "now write a friendly two line note to the team saying the release went out fine today";
        var turns = new List<string> { Task };
        var tiers = new List<string>();

        await client.PostAsync("/v1/chat/completions", Conversation("auto", [.. turns]));
        for (int i = 0; i < 4; i++)
        {
            turns.Add(simpler + " " + i);
            tiers.Add(Header(await client.PostAsync("/v1/chat/completions", Conversation("auto", [.. turns])), "Tier"));
        }

        tiers.Should().Equal("4", "3", "2", "1");
    }

    [Fact]
    public async Task A_conversation_moves_up_when_the_work_gets_harder_and_short_follow_ups_do_not_decay()
    {
        HttpClient client = await StartDispatchAsync(o => o.Conversation.DecayTurns = 1);

        await client.PostAsync("/v1/chat/completions", Conversation("auto", "hello there"));
        HttpResponseMessage harder = await client.PostAsync("/v1/chat/completions", Conversation("auto", "hello there", Task));
        HttpResponseMessage yes = await client.PostAsync("/v1/chat/completions", Conversation("auto", "hello there", Task, "yes"));
        HttpResponseMessage again = await client.PostAsync("/v1/chat/completions", Conversation("auto", "hello there", Task, "yes", "continue"));

        Header(harder, "Tier").Should().Be("4");
        Header(yes, "Tier").Should().Be("4");
        Header(again, "Tier").Should().Be("4", "short follow-ups keep the full floor, however many there are");
    }

    [Fact]
    public async Task A_first_turn_always_routes_on_its_own()
    {
        HttpClient client = await StartDispatchAsync();

        // Two conversations open with the same message: the second starts afresh, whatever the first went on to need.
        await client.PostAsync("/v1/chat/completions", Conversation("auto", "hello there"));
        await client.PostAsync("/v1/chat/completions", Conversation("auto", "hello there", Task));
        HttpResponseMessage reopened = await client.PostAsync("/v1/chat/completions", Conversation("auto", "hello there"));
        HttpResponseMessage thanks = await client.PostAsync("/v1/chat/completions", Conversation("auto", "hello there", "thanks!"));

        (Header(reopened, "Reason"), Header(reopened, "Model")).Should().Be(("SemanticMatch", "local"));
        (Header(thanks, "Reason"), Header(thanks, "Model")).Should().Be(("SemanticMatch", "local"));
    }

    [Fact]
    public async Task Cheap_side_tasks_and_a_disabled_setting_are_left_alone()
    {
        HttpClient client = await StartDispatchAsync(o => o.ModelAliases["*haiku*"] = "auto:cheapest");

        await client.PostAsync("/v1/chat/completions", Conversation("auto", Task));
        HttpResponseMessage sideTask = await client.PostAsync("/v1/chat/completions", Conversation("claude-haiku-4-5", Task, "summarize this conversation in five words"));
        sideTask.Headers.GetValues("X-Prompt-Route-Reason").Should().NotContain("ConversationFloor", "a client's own cheap side task stays cheap");

        await DisposeAsync();
        client = await StartDispatchAsync(o => o.Conversation.Enabled = false);
        await client.PostAsync("/v1/chat/completions", Conversation("auto", Task));
        HttpResponseMessage followUp = await client.PostAsync("/v1/chat/completions", Conversation("auto", Task, "ok, go ahead"));
        (Header(followUp, "Reason"), Header(followUp, "Model")).Should().Be(("SemanticMatch", "local"));
    }

    [Fact]
    public async Task The_conversation_stays_on_the_model_that_answered_while_it_fits()
    {
        // "cloud-b" is a cheaper tier-4 model, so the ranking prefers it; it fails on the first turn, so "cloud" answers.
        bool failing = true;
        LocalHttp = new StubHttpMessageHandler((_, _) => failing
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"from":"local"}""", Encoding.UTF8, "application/json") });
        HttpClient client = await StartDispatchAsync(routing: o =>
        {
            ModelDefinition cheaper = TestEmbeddings.Model("cloud-b", 4, costPerMillion: 1m, connector: "lmstudio");
            o.Models.Add(cheaper);
        });

        HttpResponseMessage first = await client.PostAsync("/v1/chat/completions", Conversation("auto", Task));
        Header(first, "Model").Should().Be("cloud", "cloud-b failed, so cloud answered");
        (await client.PostAsync("/router/api/health/reset", null)).EnsureSuccessStatusCode();
        failing = false;

        HttpResponseMessage followUp = await client.PostAsync("/v1/chat/completions", Conversation("auto", Task, "ok, go ahead"));
        HttpResponseMessage elsewhere = await client.PostAsync("/v1/chat/completions", Conversation("auto", Task + " in the billing service"));

        Header(followUp, "Model").Should().Be("cloud", "it answered the conversation's last turn and still fits");
        Header(followUp, "Sticky").Should().Be("true");
        Header(elsewhere, "Model").Should().Be("cloud-b", "a new conversation takes the ranking's first choice");
        Header(elsewhere, "Sticky").Should().Be("(none)");
    }

    [Fact]
    public async Task An_idle_conversation_is_forgotten()
    {
        var time = new ManualTimeProvider();
        HttpClient client = await StartDispatchAsync(services: s => s.AddSingleton(new ConversationTracker(time)));

        await client.PostAsync("/v1/chat/completions", Conversation("auto", Task));
        time.Advance(TimeSpan.FromMinutes(31));
        HttpResponseMessage late = await client.PostAsync("/v1/chat/completions", Conversation("auto", Task, "ok, go ahead"));

        (Header(late, "Reason"), Header(late, "Model")).Should().Be(("SemanticMatch", "local"));
    }

    [Fact]
    public void A_conversation_is_recognised_across_turns_and_protocols_without_keeping_its_text()
    {
        ChatCompletionRequest Chat(string json) => ChatCompletionRequest.Parse(JsonDocument.Parse(json).RootElement);
        ChatCompletionRequest Anthropic(string json) => ChatCompletionRequest.ParseAnthropic(JsonDocument.Parse(json).RootElement);

        string? turn1 = ConversationTracker.Identify("laptop", Chat("""{"messages":[{"role":"system","content":"S"},{"role":"user","content":"fix the bug"}]}"""));
        string? turn2 = ConversationTracker.Identify("laptop", Chat("""{"messages":[{"role":"system","content":"S"},{"role":"user","content":"fix the bug"},{"role":"assistant","content":"done"},{"role":"user","content":"thanks"}]}"""));
        string? anthropic = ConversationTracker.Identify("laptop", Anthropic("""{"system":"S","max_tokens":10,"messages":[{"role":"user","content":"fix the bug"},{"role":"assistant","content":"done"},{"role":"user","content":"thanks"}]}"""));
        string? otherClient = ConversationTracker.Identify("desktop", Chat("""{"messages":[{"role":"system","content":"S"},{"role":"user","content":"fix the bug"}]}"""));

        turn1.Should().NotBeNull().And.Be(turn2).And.Be(anthropic);
        otherClient.Should().NotBe(turn1);
        turn1.Should().NotContain("fix", "only a hash is kept");

        var options = new ConversationRoutingOptions();
        ConversationTracker.IsFollowUp("ok, go ahead", options).Should().BeTrue();
        ConversationTracker.IsFollowUp("```\nx\n```", options).Should().BeFalse("code is not a follow-up");
        ConversationTracker.IsFollowUp(new string('a', 61), options).Should().BeFalse();
    }

    [Fact]
    public void Tool_loops_keep_a_conversation_alive()
    {
        var time = new ManualTimeProvider();
        var tracker = new ConversationTracker(time);
        var options = new ConversationRoutingOptions();

        tracker.Record("c", 4, raisedByFloor: false, followUp: false, "cloud", options);
        for (int i = 0; i < 4; i++)
        {
            time.Advance(TimeSpan.FromMinutes(10));
            tracker.Touch("c", "cloud", options);
        }

        tracker.Lookup("c", "looks good, commit it", options).Should().Be(new ConversationContext(4, "cloud", FollowUp: true));
    }
}
