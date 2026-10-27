using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>Breaking a request's input down into its parts and flagging the parts that cost too much.</summary>
public sealed class ContextAnalyzerTests
{
    private static readonly string Words = string.Join(' ', Enumerable.Repeat("the quick brown fox jumps over the lazy dog", 60));

    /// <summary>Thresholds low enough that the test requests cross every one.</summary>
    private static ContextCostOptions Low => new()
    {
        LargeSystemPromptTokens = 50,
        LargeToolDefinitionTokens = 50,
        LargeToolResultTokens = 50,
        InjectedContextTokens = 50,
        RepeatedContentTokens = 50,
        LongHistoryTokens = 50,
        FixedOverheadTokens = 50,
    };

    private static ChatCompletionRequest Anthropic(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return ChatCompletionRequest.ParseAnthropic(document.RootElement);
    }

    private static string AgenticRequest(string toolResult) => JsonSerializer.Serialize(new
    {
        model = "claude-sonnet-5-5",
        system = "You are a coding agent. " + Words,
        tools = new object[]
        {
            new { name = "Read", description = "Reads a file. " + Words, input_schema = new { type = "object", properties = new { path = new { type = "string" } } } },
            new { name = "Bash", description = "Runs a command.", input_schema = new { type = "object" } },
        },
        messages = new object[]
        {
            new { role = "user", content = "Fix the failing test." },
            new { role = "assistant", content = new object[] { new { type = "text", text = "Reading the test." }, new { type = "tool_use", id = "t1", name = "Read", input = new { path = "a.cs" } } } },
            new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = "t1", content = toolResult } } },
            new { role = "assistant", content = "Found it." },
            new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "text", text = "<system-reminder>" + Words + "</system-reminder>\nNow run it please." },
                    new { type = "image", source = new { type = "base64", media_type = "image/png", data = "AAAA" } },
                },
            },
        },
    });

    [Fact]
    public void Each_part_of_the_input_is_counted_once()
    {
        ContextReport report = ContextAnalyzer.Analyze(Anthropic(AgenticRequest("short file")), new ContextCostOptions());
        ContextBreakdown b = report.Breakdown;

        b.SystemTokens.Should().BeGreaterThan(400);
        b.ToolCount.Should().Be(2);
        b.ToolDefinitionTokens.Should().Be(report.LargestTools.Sum(t => t.Tokens));
        report.LargestTools[0].Name.Should().Be("Read");
        b.InjectedTokens.Should().BeGreaterThan(400, "the <system-reminder> is injected context, not the ask");
        b.PromptTokens.Should().BeInRange(4, 20, "the ask is \"Now run it please.\"");
        b.ToolResultTokens.Should().BeGreaterThan(0);
        b.ImageTokens.Should().Be(1000);
        b.HistoryTokens.Should().BeGreaterThan(0);
        b.Turns.Should().Be(6);
        b.TotalTokens.Should().Be(b.SystemTokens + b.ToolDefinitionTokens + b.InjectedTokens + b.HistoryTokens + b.ToolResultTokens + b.ImageTokens + b.PromptTokens);
        b.Findings.Should().BeEmpty("nothing reaches the default thresholds");
        report.LatestTurnTokens.Should().Be(b.InjectedTokens + b.PromptTokens + b.ImageTokens);
    }

    [Fact]
    public void Each_threshold_flags_its_part_with_its_numbers_and_advice()
    {
        ContextReport report = ContextAnalyzer.Analyze(Anthropic(AgenticRequest(Words)), Low);

        report.Breakdown.Findings.Should().BeEquivalentTo(
            ContextAnalyzer.FixedOverhead, ContextAnalyzer.LargeSystemPrompt, ContextAnalyzer.LargeToolDefinitions,
            ContextAnalyzer.InjectedContext, ContextAnalyzer.LargeToolResult, ContextAnalyzer.LongHistory);
        report.Findings.Single(f => f.Code == ContextAnalyzer.LargeToolResult).Detail.Should().Contain("(Read)", "a result names the tool that produced it");
        report.Findings.Single(f => f.Code == ContextAnalyzer.LargeToolDefinitions).Detail.Should().StartWith("2 tools take");
        report.Findings.Should().OnlyContain(f => f.Advice.Length > 0 && f.Title.Length > 0 && f.Tokens > 0);
    }

    [Fact]
    public void Content_sent_twice_counts_as_repeated()
    {
        string request = JsonSerializer.Serialize(new
        {
            model = "m",
            messages = new object[]
            {
                new { role = "user", content = "Read it." },
                new { role = "assistant", content = new object[] { new { type = "tool_use", id = "a", name = "Read", input = new { } } } },
                new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = "a", content = Words } } },
                new { role = "assistant", content = new object[] { new { type = "tool_use", id = "b", name = "Read", input = new { } } } },
                new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = "b", content = Words } } },
            },
        });

        ContextReport report = ContextAnalyzer.Analyze(Anthropic(request), Low);

        report.Breakdown.RepeatedTokens.Should().Be(report.LargestToolResults[0].Tokens - 8, "the second copy is repeated, the first is not");
        report.Breakdown.Findings.Should().Contain(ContextAnalyzer.RepeatedContent);
    }

    [Fact]
    public void Tools_never_called_count_as_unused_once_the_conversation_is_under_way()
    {
        object Turns(int assistantTurns) => Enumerable.Range(0, assistantTurns).SelectMany(i => new object[]
        {
            new { role = "user", content = $"step {i}" },
            new { role = "assistant", content = new object[] { new { type = "tool_use", id = $"t{i}", name = "Bash", input = new { } } } },
        }).Append(new { role = "user", content = "next" }).ToArray();
        string Request(int assistantTurns) => JsonSerializer.Serialize(new
        {
            model = "m",
            tools = new object[]
            {
                new { name = "Bash", input_schema = new { type = "object" } },
                new { name = "WebSearch", description = Words, input_schema = new { type = "object" } },
            },
            messages = Turns(assistantTurns),
        });

        ContextAnalyzer.Analyze(Anthropic(Request(2)), Low).UnusedTools.Should().BeEmpty("two assistant turns are too few to tell");
        ContextReport report = ContextAnalyzer.Analyze(Anthropic(Request(3)), Low);
        report.UnusedTools.Select(t => t.Name).Should().Equal("WebSearch");
        report.Breakdown.UnusedToolDefinitionTokens.Should().Be(report.UnusedTools[0].Tokens);
        report.Findings.Single(f => f.Code == ContextAnalyzer.LargeToolDefinitions).Detail.Should().Contain("1 of them");
    }

    [Fact]
    public void Configured_injected_tags_count_as_injected()
    {
        ChatCompletionRequest chat = Anthropic(JsonSerializer.Serialize(new
        {
            model = "m",
            messages = new[] { new { role = "user", content = $"<environment_details>{Words}</environment_details>\nhello" } },
        }));

        ContextAnalyzer.Analyze(chat, new ContextCostOptions()).Breakdown.InjectedTokens.Should().Be(0);
        ContextAnalyzer.Analyze(chat, new ContextCostOptions(), ["environment_details"]).Breakdown.InjectedTokens.Should().BeGreaterThan(400);
    }

    [Fact]
    public void The_served_request_is_priced_and_checked_against_the_cache_and_the_context_window()
    {
        var options = new ContextCostOptions { UncachedTokens = 1000 };
        ModelDefinition sonnet = Model("sonnet", 4, costPerMillion: 3m);
        sonnet.CacheReadCostPerMillionTokens = 0.3m;
        sonnet.ContextWindow = 2000;
        var breakdown = new ContextBreakdown { TotalTokens = 1500, SystemTokens = 1500 };

        ContextBreakdown uncached = ContextAnalyzer.Complete(breakdown, new RoutingActivityEntry { InputTokens = 1700, OutputTokens = 10 }, sonnet, options);
        uncached.Findings.Should().BeEquivalentTo(ContextAnalyzer.Uncached, ContextAnalyzer.NearContextWindow);
        uncached.InputCostUsd.Should().Be(1700 * 3m / 1_000_000m, "input only: the output is not context");

        ContextBreakdown cached = ContextAnalyzer.Complete(breakdown, new RoutingActivityEntry { InputTokens = 100, CacheReadTokens = 1600 }, sonnet, options);
        cached.Findings.Should().Equal(ContextAnalyzer.NearContextWindow);
        cached.InputCostUsd.Should().Be(((100 * 3m) + (1600 * 0.3m)) / 1_000_000m);

        ModelDefinition local = Model("local", 1, local: true);
        ContextAnalyzer.Complete(breakdown, new RoutingActivityEntry { InputTokens = 1700 }, local, options).Findings
            .Should().BeEmpty("a model without cheaper cache reads has no cache to miss, and no known window");
        ContextAnalyzer.Complete(breakdown, new RoutingActivityEntry(), null, options).Should().BeSameAs(breakdown);
    }

    [Fact]
    public void A_subscription_records_what_the_input_would_have_cost_at_API_prices()
    {
        ModelDefinition claude = Model("claude-cli", 4);
        (claude.ApiInputCostPerMillionTokens, claude.ApiOutputCostPerMillionTokens, claude.ApiCacheReadCostPerMillionTokens) = (3m, 15m, 0.3m);

        ContextBreakdown completed = ContextAnalyzer.Complete(new ContextBreakdown { TotalTokens = 30_000 }, new RoutingActivityEntry { InputTokens = 30_000 }, claude, new ContextCostOptions());

        completed.InputCostUsd.Should().Be(0);
        completed.InputApiValueUsd.Should().Be(30_000 * 3m / 1_000_000m);
        completed.Findings.Should().Contain(ContextAnalyzer.Uncached, "the subscription's API cache reads are cheaper");
    }

    [Fact]
    public void Each_model_in_the_catalog_is_priced_for_the_request()
    {
        ContextReport report = ContextAnalyzer.Analyze(Anthropic(AgenticRequest(Words)), new ContextCostOptions());
        ModelDefinition opus = Model("opus", 5, costPerMillion: 15m);
        opus.CacheReadCostPerMillionTokens = 1.5m;
        opus.ContextWindow = report.Breakdown.TotalTokens + 100;
        ModelDefinition haiku = Model("haiku", 2, costPerMillion: 1m);
        ModelDefinition off = Model("off", 5, costPerMillion: 100m);
        off.Enabled = false;

        ContextReport priced = ContextAnalyzer.Price(report, [haiku, opus, off], new ContextCostOptions());

        priced.Models.Select(m => m.Model).Should().Equal("opus", "haiku");
        ContextModelCost first = priced.Models[0];
        first.InputCostUsd.Should().Be(report.Breakdown.TotalTokens * 15m / 1_000_000m);
        first.CachedInputCostUsd.Should().BeLessThan(first.InputCostUsd);
        priced.Models[1].CachedInputCostUsd.Should().BeNull();
        priced.Breakdown.Findings.Should().Contain(ContextAnalyzer.NearContextWindow);
        priced.Findings.Single(f => f.Code == ContextAnalyzer.NearContextWindow).Detail.Should().Contain("opus");
    }

    [Theory]
    [InlineData("""{"model":"m","messages":[{"role":"system","content":"x"},{"role":"user","content":"hi"}]}""", ChatProtocol.OpenAI)]
    [InlineData("""{"model":"m","system":"x","messages":[{"role":"user","content":"hi"}]}""", ChatProtocol.Anthropic)]
    [InlineData("""{"model":"m","messages":[{"role":"user","content":[{"type":"tool_result","tool_use_id":"a","content":"x"}]}]}""", ChatProtocol.Anthropic)]
    [InlineData("""{"model":"m","tools":[{"name":"a","input_schema":{}}],"messages":[]}""", ChatProtocol.Anthropic)]
    [InlineData("""{"model":"m","instructions":"x","input":"hi"}""", ChatProtocol.Responses)]
    public void A_pasted_request_is_read_in_its_protocol(string json, ChatProtocol expected)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        ContextProtocol.Detect(document.RootElement).Should().Be(expected);
    }

    [Fact]
    public void A_period_splits_each_request_cost_over_its_parts()
    {
        RoutingActivityEntry Entry(long id, ContextBreakdown context) => new() { Id = id, Timestamp = DateTimeOffset.UtcNow, ServedBy = "sonnet", Context = context };
        RoutingActivityEntry[] activity =
        [
            Entry(1, new ContextBreakdown { TotalTokens = 1000, SystemTokens = 750, PromptTokens = 250, InputCostUsd = 1m, Findings = [ContextAnalyzer.LargeSystemPrompt] }),
            Entry(2, new ContextBreakdown { TotalTokens = 1000, ToolDefinitionTokens = 500, PromptTokens = 500, InputCostUsd = 2m, Findings = [ContextAnalyzer.LargeSystemPrompt, ContextAnalyzer.Uncached] }),
            Entry(3, new ContextBreakdown { TotalTokens = 10, PromptTokens = 10, InputApiValueUsd = 0.5m }),
            new() { Id = 4, Timestamp = DateTimeOffset.UtcNow },
        ];

        ContextSummary summary = ContextSummary.Build(activity, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

        summary.Requests.Should().Be(3, "an entry without a breakdown is left out");
        summary.TotalTokens.Should().Be(2010);
        summary.CostUsd.Should().Be(3m);
        summary.ApiValueUsd.Should().Be(0.5m);
        summary.Components.Single(c => c.Key == "system").CostUsd.Should().Be(0.75m);
        summary.Components.Single(c => c.Key == "tools").CostUsd.Should().Be(1m);
        summary.Components.Single(c => c.Key == "prompt").CostUsd.Should().Be(1.25m);
        summary.Components.Sum(c => c.CostUsd).Should().Be(summary.CostUsd);
        summary.Components.Single(c => c.Key == "prompt").ApiValueUsd.Should().Be(0.5m);
        summary.Findings.Select(f => (f.Code, f.Requests, f.CostUsd, f.Example)).Should().Equal(
            (ContextAnalyzer.LargeSystemPrompt, 2, 3m, 2L), (ContextAnalyzer.Uncached, 1, 2m, 2L));
        summary.Heaviest.Select(r => r.Id).Should().Equal(2, 1, 3);
    }

    [Fact]
    public void Negative_thresholds_and_a_share_outside_zero_to_one_are_refused()
    {
        var configuration = new RouterConfiguration();
        configuration.Usage.Context.LongHistoryTokens = -1;
        configuration.Usage.Context.ContextWindowShare = 1.5;

        configuration.GetDocumentValidationErrors().Should().Contain(
            ["Usage:Context thresholds must not be negative.", "Usage:Context:ContextWindowShare must be greater than 0 and at most 1."]);
    }
}

/// <summary>Context analysis through the running router and the dashboard.</summary>
public sealed class ContextCostGatewayTests : RouterHostTest
{
    private const string OpenAIReply = """{"id":"c1","object":"chat.completion","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":30000,"completion_tokens":5}}""";
    private static readonly string Instructions = string.Join(' ', Enumerable.Repeat("Always follow the house style for every file you touch.", 1200));

    private static StringContent Request(string model) => new(JsonSerializer.Serialize(new
    {
        model,
        messages = new object[] { new { role = "system", content = Instructions }, new { role = "user", content = "hello there" } },
    }), Encoding.UTF8, "application/json");

    private Task<HttpClient> StartDispatchAsync() =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
            },
            routing: o => o.Models.Single(m => m.Name == "cloud").CacheReadCostPerMillionTokens = 0.3m);

    [Fact]
    public async Task A_served_request_keeps_its_breakdown_and_cost_and_the_dashboard_adds_them_up()
    {
        CloudHttp = StubHttpMessageHandler.Json(OpenAIReply);
        HttpClient client = await StartDispatchAsync();

        (await client.PostAsync("/v1/chat/completions", Request("cloud"))).StatusCode.Should().Be(HttpStatusCode.OK);

        RoutingActivityEntry entry = Services.GetRequiredService<RoutingActivityLog>().Recent().Single();
        ContextBreakdown context = entry.Context!;
        context.SystemTokens.Should().BeGreaterThan(8_000);
        context.PromptTokens.Should().BeLessThan(20);
        context.Findings.Should().BeEquivalentTo(ContextAnalyzer.LargeSystemPrompt, ContextAnalyzer.Uncached);
        context.InputCostUsd.Should().Be(30_000 * 3m / 1_000_000m);

        JsonNode summary = JsonNode.Parse(await client.GetStringAsync("/router/api/context/summary?days=1"))!;
        summary["requests"]!.GetValue<int>().Should().Be(1);
        summary["costUsd"]!.GetValue<decimal>().Should().Be(context.InputCostUsd!.Value);
        summary["components"]!.AsArray().Single(c => c!["key"]!.GetValue<string>() == "system")!["share"]!.GetValue<double>().Should().BeGreaterThan(0.95);
        summary["findings"]!.AsArray().Select(f => f!["code"]!.GetValue<string>()).Should().Contain("uncached");
        summary["heaviest"]![0]!["id"]!.GetValue<long>().Should().Be(entry.Id);
    }

    [Fact]
    public async Task Analysis_can_be_turned_off()
    {
        CloudHttp = StubHttpMessageHandler.Json(OpenAIReply);
        HttpClient client = await StartDispatchAsync();
        Services.GetRequiredService<RouterRuntimeProvider>().Current.Configuration.Usage.Context.Enabled = false;

        (await client.PostAsync("/v1/chat/completions", Request("cloud"))).StatusCode.Should().Be(HttpStatusCode.OK);

        Services.GetRequiredService<RoutingActivityLog>().Recent().Single().Context.Should().BeNull();
    }

    [Fact]
    public async Task A_pasted_request_is_broken_down_and_priced_on_every_model_without_being_sent()
    {
        HttpClient client = await StartDispatchAsync();
        string body = JsonSerializer.Serialize(new
        {
            model = "claude-sonnet-5-5",
            system = Instructions,
            messages = new[] { new { role = "user", content = "hello there" } },
        });

        HttpResponseMessage response = await client.PostAsync("/router/api/context/analyze", new StringContent(body, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode result = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        result["protocol"]!.GetValue<string>().Should().Be("Anthropic");
        JsonNode report = result["report"]!;
        report["breakdown"]!["systemTokens"]!.GetValue<int>().Should().BeGreaterThan(8_000);
        report["findings"]!.AsArray().Select(f => f!["code"]!.GetValue<string>()).Should().Contain("large-system-prompt");
        report["models"]!.AsArray().Select(m => m!["model"]!.GetValue<string>()).Should().Equal("cloud", "local");
        report["models"]![0]!["cachedInputCostUsd"]!.GetValue<decimal>().Should().BeLessThan(report["models"]![0]!["inputCostUsd"]!.GetValue<decimal>());
        CloudHttp.Requests.Should().BeEmpty("analyzing sends nothing to a model");
        Services.GetRequiredService<RoutingActivityLog>().Recent().Should().BeEmpty();

        (await client.PostAsync("/router/api/context/analyze", new StringContent("not json", Encoding.UTF8, "application/json"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsync("/router/api/context/analyze?protocol=gopher", new StringContent(body, Encoding.UTF8, "application/json"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        HttpResponseMessage openAI = await client.PostAsJsonAsync("/router/api/context/analyze?protocol=openai", new { messages = new[] { new { role = "user", content = "hi" } } });
        JsonNode asOpenAI = JsonNode.Parse(await openAI.Content.ReadAsStringAsync())!;
        asOpenAI["protocol"]!.GetValue<string>().Should().Be("OpenAI");
    }
}
