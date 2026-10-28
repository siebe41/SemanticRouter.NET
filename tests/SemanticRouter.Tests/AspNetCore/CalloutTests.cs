using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
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
    public async Task With_the_init_templates_aliases_claude_codes_default_opus_name_is_routed_and_opus_is_a_choice()
    {
        HttpClient client = await StartDispatchAsync(o =>
        {
            // The globs first: an exact name is matched before them wherever it stands (a dashboard edit appends it).
            o.ModelAliases.Clear();
            foreach ((string pattern, string target) in RouterConfiguration.FromJson(SemanticRouter.Host.RouterHost.Template()).Middleware.ModelAliases
                .OrderBy(a => !a.Key.Contains('*', StringComparison.Ordinal)))
            {
                o.ModelAliases[pattern] = target;
            }
        });

        HttpResponseMessage byDefault = await client.PostAsync("/v1/chat/completions", Chat("claude-opus-5-5", "hello there"));
        HttpResponseMessage callout = await client.PostAsync("/v1/chat/completions", Chat("claude-opus-5-5", "@opus why does this deadlock?"));
        HttpResponseMessage pinned = await client.PostAsync("/v1/chat/completions", Chat("opus-pinned", "hello there"));
        HttpResponseMessage router = await client.PostAsync("/v1/chat/completions", Chat("semantic-router", "hello there"));
        HttpResponseMessage cheapest = await client.PostAsync("/v1/chat/completions", Chat("claude-semantic-router-cheapest", "hello there"));
        HttpResponseMessage best = await client.PostAsync("/v1/chat/completions", Chat("claude-semantic-router-best", "hello there"));

        (Header(byDefault, "Model"), Header(byDefault, "Reason")).Should().Be(("local", "SemanticMatch"), "Claude Code sends its default Opus name whether or not you chose Opus");
        (Header(callout, "Model"), Header(callout, "Reason")).Should().Be(("claude-opus", "ExplicitCallout"));
        (Header(pinned, "Model"), Header(pinned, "Reason")).Should().Be(("claude-opus", "ModelOverride"), "the exact key beats the *opus* glob");
        Header(router, "Model").Should().Be("local");
        Header(cheapest, "Model").Should().Be("local");
        Header(best, "Model").Should().Be("cloud", "claude-* does not remap the router's own names: this is auto:best");
    }

    [Fact]
    public async Task A_property_named_twice_does_not_stop_the_callout_being_removed()
    {
        HttpClient client = await StartDispatchAsync();

        // Fuzz seed 3 (20000 iterations) found the router answering 500 here: the edited tree threw on the repeated name.
        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", new StringContent("""
            {"model":"auto","messages":[{"role":"user","name":"a","name":"b","content":"@opus why does this deadlock?"}]}
            """, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Header(response, "Callout").Should().Be("@opus");
        string forwarded = _sentToCloud.Should().ContainSingle().Subject;
        forwarded.Should().NotContain("@opus").And.Contain("why does this deadlock?");
        forwarded.Should().Contain("\"name\":\"b\"", "the last value wins, as the router's own reading of the body does").And.NotContain("\"name\":\"a\"");
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
    public async Task Callouts_work_past_the_copilot_apps_context_blocks()
    {
        HttpClient client = await StartDispatchAsync();
        string prompt = "<current_datetime>2026-10-12T11:19:05-05:00</current_datetime>\n<copilot_tauri_workspace>\nproject_session_id: 30fa\n</copilot_tauri_workspace>\n@opus why is the build slow?";

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Chat("auto", prompt));

        (Header(response, "Model"), Header(response, "Reason")).Should().Be(("claude-opus", "ExplicitCallout"));
        string forwarded = _sentToCloud.Should().ContainSingle().Subject;
        forwarded.Should().NotContain("@opus").And.Contain("why is the build slow?").And.Contain("copilot_tauri_workspace");
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

    [Theory]
    [InlineData("/router/api/classify")]
    [InlineData("/semantic-router/classify")]
    public async Task Classifying_a_prompt_applies_its_callout_as_the_gateway_does(string endpoint)
    {
        HttpClient client = await StartDispatchAsync();
        ClassifyAs(tier: 2, route: "simple-code");

        JsonElement opus = await ClassifyAsync(client, endpoint, "@opus write a regex that matches email addresses");
        JsonElement tier = await ClassifyAsync(client, endpoint, "@tier3 summarize the incident report");
        JsonElement local = await ClassifyAsync(client, endpoint, "@local refactor the patient import");
        JsonElement auto = await ClassifyAsync(client, endpoint, "@auto hello there");
        JsonElement plain = await ClassifyAsync(client, endpoint, "write a regex that matches email addresses");

        (opus.GetProperty("reason").GetString(), opus.GetProperty("callout").GetString(), opus.GetProperty("models")[0].GetProperty("model").GetString())
            .Should().Be(("ExplicitCallout", "@opus", "claude-opus"), "the gateway sends this prompt to Opus, so the tester says so");
        (tier.GetProperty("requiredTier").GetInt32(), tier.GetProperty("callout").GetString()).Should().Be((3, "@tier3"));
        (local.GetProperty("localOnly").GetBoolean(), local.GetProperty("models")[0].GetProperty("model").GetString()).Should().Be((true, "local"));
        (auto.GetProperty("callout").GetString(), auto.GetProperty("reason").GetString()).Should().Be(("@auto", "SemanticMatch"));
        (plain.GetProperty("requiredTier").GetInt32(), plain.GetProperty("callout").ValueKind).Should().Be((2, JsonValueKind.Null));
        Classifier.Verify(c => c.ClassifyAsync("summarize the incident report", It.IsAny<CancellationToken>()), Times.Once(), "the tag is not part of what is classified");
        Classifier.Verify(c => c.ClassifyAsync(It.Is<string>(p => p.Contains('@')), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Theory]
    [InlineData("/router/api/classify")]
    [InlineData("/semantic-router/classify")]
    public async Task Classifying_finds_a_callout_after_a_configured_injected_block(string endpoint)
    {
        HttpClient client = await StartDispatchAsync(o => o.InjectedTags.Add("ide-context"));

        JsonElement opus = await ClassifyAsync(client, endpoint, "<ide-context>open file: a.cs</ide-context>\n@opus fix this");

        (opus.GetProperty("callout").GetString(), opus.GetProperty("models")[0].GetProperty("model").GetString())
            .Should().Be(("@opus", "claude-opus"), "the gateway skips the configured block before looking for the tag, so the tester does too");
    }

    [Fact]
    public async Task A_model_name_with_spaces_around_it_matches_an_alias_pattern()
    {
        await StartDispatchAsync(o => o.ModelAliases["gpt-*"] = "auto:best");
        RouterRuntime runtime = Services.GetRequiredService<RouterRuntimeProvider>().Current;

        PromptRoutingMiddleware.MapModel(runtime, " gpt-4o ").Should().Be("auto:best");
    }

    [Fact]
    public async Task Classifying_ignores_callouts_when_they_are_off_and_remembers_none()
    {
        HttpClient client = await StartDispatchAsync(o => o.Callouts.Enabled = false);

        JsonElement off = await ClassifyAsync(client, "/router/api/classify", "@opus write a regex");

        (off.GetProperty("reason").GetString(), off.GetProperty("callout").ValueKind).Should().Be(("SemanticMatch", JsonValueKind.Null));

        await DisposeAsync();
        client = await StartDispatchAsync();
        await ClassifyAsync(client, "/router/api/classify", "@opus write a regex");
        JsonElement next = await ClassifyAsync(client, "/router/api/classify", "write a regex");
        next.GetProperty("reason").GetString().Should().Be("SemanticMatch", "each prompt is classified as a first turn: there is no conversation to remember a callout in");
    }

    private static async Task<JsonElement> ClassifyAsync(HttpClient client, string endpoint, string prompt)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync(endpoint, new { prompt });
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
