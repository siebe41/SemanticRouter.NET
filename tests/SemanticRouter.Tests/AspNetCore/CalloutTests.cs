using System.Net;
using System.Text;
using System.Text.Json;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>Explicit choices win, and only explicit ones (#42): inline callouts, sticky per conversation, family aliases.</summary>
public sealed class CalloutTests : RouterHostTest
{
    private const string Reply = """{"id":"r","object":"chat.completion","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1}}""";

    private readonly List<string> _sentToCloud = [];
    private readonly List<string> _sentToLocal = [];

    public CalloutTests()
    {
        CloudHttp = new StubHttpMessageHandler((_, body) => Capture(_sentToCloud, body));
        LocalHttp = new StubHttpMessageHandler((_, body) => Capture(_sentToLocal, body));
    }

    private static HttpResponseMessage Capture(List<string> sent, string body)
    {
        lock (sent)
        {
            sent.Add(body);
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Reply, Encoding.UTF8, "application/json") };
    }

    /// <summary>Catalog: local (1), cloud (4), plus a tier-4 Opus and a tier-3 Sonnet behind the cloud connector.</summary>
    private Task<HttpClient> StartDispatchAsync(Action<PromptRouterMiddlewareOptions>? configure = null) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
                o.ModelAliases["*haiku*"] = "auto:cheapest";
                o.ModelAliases["*opus*"] = "family:opus";
                configure?.Invoke(o);
            },
            routing: o =>
            {
                ModelDefinition opus = TestEmbeddings.Model("claude-opus", 4, 15m, false, "anthropic", "tools");
                opus.Model = "claude-opus-4-1";
                ModelDefinition sonnet = TestEmbeddings.Model("claude-sonnet", 3, 3m, false, "anthropic", "tools");
                sonnet.Model = "claude-sonnet-4-5";
                o.Models.Add(opus);
                o.Models.Add(sonnet);
            });

    private static StringContent Chat(string model, params string[] userTurns)
    {
        var messages = new List<object>();
        for (int i = 0; i < userTurns.Length; i++)
        {
            messages.Add(new { role = "user", content = userTurns[i] });
            if (i < userTurns.Length - 1)
            {
                messages.Add(new { role = "assistant", content = "answer " + i });
            }
        }

        return new StringContent(JsonSerializer.Serialize(new { model, messages }), Encoding.UTF8, "application/json");
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("X-Prompt-Route-" + name, out IEnumerable<string>? values) ? values.Single() : "(none)";

    [Fact]
    public async Task A_callout_picks_the_model_and_never_reaches_it()
    {
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Chat("auto", "@opus why does this deadlock when I call .Result?"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (Header(response, "Model"), Header(response, "Reason"), Header(response, "Callout")).Should().Be(("claude-opus", "ExplicitCallout", "@opus"));
        string forwarded = _sentToCloud.Should().ContainSingle().Subject;
        forwarded.Should().NotContain("@opus").And.Contain("why does this deadlock when I call .Result?");
        forwarded.Should().Contain("claude-opus-4-1", "the backend model id of the family match");
    }

    [Fact]
    public async Task The_choice_holds_for_the_conversation_until_auto()
    {
        HttpClient client = await StartDispatchAsync();
        string first = "@opus review this design for the ledger";

        await client.PostAsync("/v1/chat/completions", Chat("auto", first));
        HttpResponseMessage next = await client.PostAsync("/v1/chat/completions", Chat("auto", first, "and the retry logic?"));
        HttpResponseMessage back = await client.PostAsync("/v1/chat/completions", Chat("auto", first, "and the retry logic?", "@auto thanks, that is all"));
        HttpResponseMessage after = await client.PostAsync("/v1/chat/completions", Chat("auto", first, "and the retry logic?", "@auto thanks, that is all", "bye"));

        (Header(next, "Model"), Header(next, "Callout")).Should().Be(("claude-opus", "@opus (earlier in the conversation)"));
        Header(back, "Model").Should().Be("local", "@auto hands routing back (the mock classifies everything as tier 1)");
        Header(back, "Callout").Should().Be("@auto");
        (Header(after, "Model"), Header(after, "Callout")).Should().Be(("local", "(none)"));

        // History is resent with its tags: every one is taken out before forwarding.
        _sentToLocal.Should().OnlyContain(body => !body.Contains("@opus") && !body.Contains("@auto"));
    }

    [Fact]
    public async Task Tiers_strategies_and_local_only_can_be_called_out()
    {
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage tier = await client.PostAsync("/v1/chat/completions", Chat("auto", "@tier3 summarize the incident report"));
        HttpResponseMessage best = await client.PostAsync("/v1/chat/completions", Chat("auto", "@best hello there"));
        ClassifyAs(tier: 4, route: "complex-code");
        HttpResponseMessage local = await client.PostAsync("/v1/chat/completions", Chat("auto", "@local refactor the patient import"));

        (Header(tier, "Tier"), Header(tier, "Model"), Header(tier, "Reason")).Should().Be(("3", "claude-sonnet", "ExplicitCallout"));
        Header(best, "Model").Should().Be("cloud", "the most capable tier (4), and the cheaper of its two models");
        (Header(local, "Model"), Header(local, "Local-Only")).Should().Be(("local", "true"));
    }

    [Fact]
    public async Task Client_model_names_prefer_the_family_and_side_tasks_stay_cheap()
    {
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage opus = await client.PostAsync("/v1/chat/completions", Chat("claude-opus-4-1-20250805", "hello there"));
        HttpResponseMessage haiku = await client.PostAsync("/v1/chat/completions", Chat("claude-haiku-4-5", "write a title for this conversation"));
        HttpResponseMessage sonnet = await client.PostAsync("/v1/chat/completions", Chat("claude-sonnet-4-5-20250929", "hello there"));

        Header(opus, "Model").Should().Be("claude-opus", "/model opus means Opus, not whatever is strongest");
        Header(haiku, "Model").Should().Be("local");
        Header(sonnet, "Model").Should().Be("local", "a client's default model name means: router decides");
    }

    [Fact]
    public async Task Only_a_leading_known_tag_counts_and_unknown_tags_are_left_alone()
    {
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage midway = await client.PostAsync("/v1/chat/completions", Chat("auto", "explain what @opus would say about this"));
        HttpResponseMessage file = await client.PostAsync("/v1/chat/completions", Chat("auto", "@src/Program.cs explain this file"));
        HttpResponseMessage alone = await client.PostAsync("/v1/chat/completions", Chat("auto", "@opus"));

        (Header(midway, "Model"), Header(midway, "Callout")).Should().Be(("local", "(none)"));
        (Header(file, "Model"), Header(file, "Callout")).Should().Be(("local", "(none)"));
        _sentToLocal.Should().Contain(body => body.Contains("@src/Program.cs explain this file"), "not a callout, so the message is untouched");
        Header(alone, "Model").Should().Be("claude-opus", "a bare tag still chooses");
        _sentToCloud.Last().Should().Contain("@opus", "removing it would leave an empty message");
    }

    [Fact]
    public async Task Callouts_work_through_the_anthropic_door_past_injected_reminders()
    {
        HttpClient client = await StartDispatchAsync();
        string body = JsonSerializer.Serialize(new
        {
            model = "claude-sonnet-4-5",
            max_tokens = 100,
            system = "You are Claude Code.",
            messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = "<system-reminder>Today is Thursday.</system-reminder>" },
                        new { type = "text", text = "@opus why is the build slow?" },
                    },
                },
            },
        });

        HttpResponseMessage response = await client.PostAsync("/v1/messages", new StringContent(body, Encoding.UTF8, "application/json"));

        (Header(response, "Model"), Header(response, "Reason")).Should().Be(("claude-opus", "ExplicitCallout"));
        string forwarded = _sentToCloud.Should().ContainSingle().Subject;
        forwarded.Should().NotContain("@opus").And.Contain("why is the build slow?");
    }

    [Fact]
    public async Task Own_short_names_and_switching_callouts_off()
    {
        HttpClient client = await StartDispatchAsync(o => o.Callouts.Names["deep"] = "family:opus");
        HttpResponseMessage named = await client.PostAsync("/v1/chat/completions", Chat("auto", "@deep think this through"));
        (Header(named, "Model"), Header(named, "Callout")).Should().Be(("claude-opus", "@deep"));

        await DisposeAsync();
        _sentToLocal.Clear();
        client = await StartDispatchAsync(o => o.Callouts.Enabled = false);
        HttpResponseMessage off = await client.PostAsync("/v1/chat/completions", Chat("auto", "@opus think this through"));
        Header(off, "Model").Should().Be("local");
        _sentToLocal.Should().ContainSingle().Which.Should().Contain("@opus think this through");
    }
}
