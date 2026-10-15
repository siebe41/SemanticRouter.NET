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
    public async Task A_conversation_stays_on_its_warm_prompt_cache_across_a_restart_until_it_expires()
    {
        // The cloud model reports 80k tokens read from its cache; "mid" is a cheaper tier-3 model without one.
        CloudHttp = StubHttpMessageHandler.Json("""{"id":"r","object":"chat.completion","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}],"usage":{"prompt_tokens":80100,"completion_tokens":10,"prompt_tokens_details":{"cached_tokens":80000}}}""");
        Classifier.Setup(c => c.ClassifyAsync(It.Is<string>(p => p.Contains("tests", StringComparison.Ordinal)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromptClassificationResult
            {
                RequiredTier = 3,
                Reason = RoutingReason.SemanticMatch,
                RouteName = "testing",
                Complexity = new ComplexityAnalysis { Score = 0.4, EstimatedTokens = 10 },
            });
        static void Catalog(SemanticRouterOptions o)
        {
            o.Models.Single(m => m.Name == "cloud").CacheReadCostPerMillionTokens = 0.3m;
            o.Models.Add(TestEmbeddings.Model("mid", 3, costPerMillion: 1m, connector: "lmstudio"));
        }

        var time = new ManualTimeProvider();
        var caches = new WarmCacheStore(time);
        void Services(IServiceCollection s) => s.AddSingleton<IWarmCacheStore>(caches).AddSingleton<TimeProvider>(time);
        HttpClient client = await StartDispatchAsync(routing: Catalog, services: Services);
        Header(await client.PostAsync("/v1/chat/completions", Conversation("auto", Task)), "Model").Should().Be("cloud");

        // A restart forgets the conversation's floor, but not its warm cache.
        await DisposeAsync();
        client = await StartDispatchAsync(routing: Catalog, services: Services);
        HttpResponseMessage tests = await client.PostAsync("/v1/chat/completions", Conversation("auto", Task, "now write the tests for it"));

        (Header(tests, "Tier"), Header(tests, "Model"), Header(tests, "Sticky")).Should().Be(("3", "cloud", "true"), "reading 80k from cloud's cache costs less than sending it to mid");
        Header(tests, "Selection").Should().StartWith("cache: stayed on cloud (80k cached); moving to mid would cost $0.05");

        // Once the cache has expired, the conversation moves to the cheaper model.
        time.Advance(TimeSpan.FromMinutes(6));
        HttpResponseMessage later = await client.PostAsync("/v1/chat/completions", Conversation("auto", Task, "now write the tests for it", "and the tests for the ledger"));
        (Header(later, "Model"), Header(later, "Sticky"), Header(later, "Selection")).Should().Be(("mid", "(none)", "(none)"));
    }

    [Fact]
    public async Task A_tool_loop_without_assistant_text_keeps_the_conversation_and_its_callout()
    {
        CloudHttp = StubHttpMessageHandler.Json("""{"id":"r","object":"chat.completion","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"done"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""");
        HttpClient client = await StartDispatchAsync();
        object toolUse = new { role = "assistant", content = new object[] { new { type = "tool_use", id = "t1", name = "Bash", input = new { command = "make" } } } };
        object toolResult = new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = "t1", content = "ok" } } };
        StringContent Anthropic(params object[] messages) =>
            new(JsonSerializer.Serialize(new { model = "claude-sonnet-4-5", max_tokens = 100, system = "You are a coding agent.", messages }), Encoding.UTF8, "application/json");

        // A task, then a tool loop whose assistant turns are tool calls only (no text), then a short follow-up.
        await client.PostAsync("/v1/messages", Anthropic(new { role = "user", content = Task }));
        await client.PostAsync("/v1/messages", Anthropic(new { role = "user", content = Task }, toolUse, toolResult));
        HttpResponseMessage followUp = await client.PostAsync("/v1/messages", Anthropic(
            new { role = "user", content = Task }, toolUse, toolResult, new { role = "assistant", content = "done" }, new { role = "user", content = "ok, go ahead" }));

        (Header(followUp, "Reason"), Header(followUp, "Tier")).Should().Be(("ConversationFloor", "4"), "the tool loop continued the conversation, it did not start a new one");
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

    private const string Customer = "look up the customer record for account 4471";

    private void CustomersStayLocal(SemanticRouterOptions o)
    {
        RouteDefinition customers = TestEmbeddings.Route("customers", 1, Customer);
        customers.LocalOnly = true;
        o.Routes.Add(customers);
        Classifier.Setup(c => c.ClassifyAsync(It.Is<string>(p => p.Contains("customer", StringComparison.Ordinal)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromptClassificationResult
            {
                RequiredTier = 1,
                Reason = RoutingReason.SemanticMatch,
                RouteName = "customers",
                LocalOnly = true,
                Complexity = new ComplexityAnalysis { Score = 0.1, EstimatedTokens = 10 },
            });
    }

    [Fact]
    public async Task A_conversation_kept_local_stays_local_on_later_turns_and_its_compaction()
    {
        HttpClient client = await StartDispatchAsync(routing: CustomersStayLocal);

        HttpResponseMessage first = await client.PostAsync("/v1/chat/completions", Conversation("auto", Customer));
        HttpResponseMessage later = await client.PostAsync("/v1/chat/completions", Conversation("auto", Customer, Task));
        string compaction = JsonSerializer.Serialize(new
        {
            model = "auto",
            messages = new object[]
            {
                new { role = "system", content = "You are a helpful AI assistant tasked with summarizing conversations." },
                new { role = "user", content = Customer },
                new { role = "assistant", content = "Done: 0" },
                new { role = "user", content = Task + ". Now summarise this conversation." },
            },
        });
        HttpResponseMessage summary = await client.PostAsync("/v1/chat/completions", new StringContent(compaction, Encoding.UTF8, "application/json"));
        HttpResponseMessage other = await client.PostAsync("/v1/chat/completions", Conversation("auto", Task));

        Header(first, "Model").Should().Be("local");
        Header(later, "Model").Should().Be("local", "the refactor would go to the cloud, but this conversation started on a local-only route");
        Header(summary, "Model").Should().Be("local", "a compaction request carries the conversation under another system prompt");
        Header(other, "Model").Should().Be("cloud", "another conversation is not affected");
        CloudHttp.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task A_first_turn_starts_afresh()
    {
        HttpClient client = await StartDispatchAsync(routing: CustomersStayLocal);
        const string Opening = "hello there";

        await client.PostAsync("/v1/chat/completions", Conversation("auto", Opening, Customer));
        HttpResponseMessage fresh = await client.PostAsync("/v1/chat/completions", Conversation("auto", Opening));
        HttpResponseMessage next = await client.PostAsync("/v1/chat/completions", Conversation("auto", Opening, Task));

        Header(fresh, "Model").Should().Be("local", "small talk is tier 1 anyway");
        Header(next, "Model").Should().Be("cloud", "a new conversation that opened the same way is not kept local");
    }

    [Fact]
    public async Task Naming_a_cloud_model_does_not_take_a_local_only_prompt_off_the_machine()
    {
        HttpClient client = await StartDispatchAsync(routing: CustomersStayLocal);

        HttpResponseMessage named = await client.PostAsync("/v1/chat/completions", Conversation("cloud", Customer));
        HttpResponseMessage allowed = await client.PostAsync("/v1/chat/completions", Conversation("cloud", Task));

        Header(named, "Model").Should().Be("local");
        Header(allowed, "Model").Should().Be("cloud", "a prompt no local-only route covers still goes to the model the client named");
        Header(allowed, "Reason").Should().Be("ModelOverride");
    }
}
