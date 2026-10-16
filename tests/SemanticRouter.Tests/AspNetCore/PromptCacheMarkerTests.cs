using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Cache markers on translated Anthropic requests (#95): OpenAI and Codex clients routed to an Anthropic backend use
/// its prompt cache, only above the model's cacheable minimum, and an Anthropic client's own markers are kept.
/// </summary>
public sealed class PromptCacheMarkerTests : RouterHostTest
{
    // Long enough to cache on current models (512 tokens), too short for the 4,096-token models.
    private static readonly string Instructions = string.Join(' ', Enumerable.Repeat("You are a coding agent. Follow the repository conventions and keep changes small.", 60));

    private const string ShellTool = """{"type":"function","name":"shell","description":"Runs a command","parameters":{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}}""";

    private static readonly string FirstTurn = CodexBody("""{"type":"message","role":"user","content":[{"type":"input_text","text":"Fix the failing test."}]}""");

    private static readonly string SecondTurn = CodexBody(
        """{"type":"message","role":"user","content":[{"type":"input_text","text":"Fix the failing test."}]}""",
        """{"type":"function_call","call_id":"call_1","name":"shell","arguments":"{\"command\":\"dotnet test\"}"}""",
        """{"type":"function_call_output","call_id":"call_1","output":"1 failed"}""");

    private static string CodexBody(params string[] input) =>
        $$"""{"model":"native","instructions":{{JsonSerializer.Serialize(Instructions)}},"prompt_cache_key":"conv-1","tools":[{{ShellTool}}],"input":[{{string.Join(',', input)}}]}""";

    private static string AnthropicReply(string usage) =>
        $$"""{"id":"msg_1","type":"message","role":"assistant","content":[{"type":"text","text":"Done."}],"stop_reason":"end_turn","usage":{{usage}}}""";

    private Task<HttpClient> StartNativeAsync(string backendModel = "claude-sonnet-4-6", Action<ConnectorOptions>? connector = null) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
            },
            routing: o =>
            {
                ModelDefinition native = Model("native", 4, costPerMillion: 3m, connector: "anthropic-native", capabilities: ["tools"]);
                native.Model = backendModel;
                o.Models.Add(native);
            },
            services: s => s.Configure<SemanticRouterConnectorsOptions>(o =>
            {
                var options = new ConnectorOptions { Type = ConnectorType.Anthropic, BaseAddress = new Uri("https://api.anthropic.example"), ApiKey = "sk-ant-test" };
                connector?.Invoke(options);
                o.Connectors["anthropic-native"] = options;
            }));

    private JsonElement SentBody(int index = 0) => JsonDocument.Parse(AnthropicHttp.Requests[index].Body).RootElement.Clone();

    private RoutingActivityEntry LastEntry => Services.GetRequiredService<RoutingActivityLog>().Recent(1).Single();

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public async Task A_codex_request_routed_to_an_anthropic_backend_carries_cache_markers_above_the_minimum()
    {
        AnthropicHttp = StubHttpMessageHandler.Json(AnthropicReply("""{"input_tokens":20,"output_tokens":2}"""));
        HttpClient client = await StartNativeAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/responses", Json(FirstTurn));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Contains("X-Prompt-Route-Dropped").Should().BeFalse();
        JsonElement sent = SentBody();
        JsonElement system = sent.GetProperty("system");
        system.ValueKind.Should().Be(JsonValueKind.Array, "a marker needs the system prompt as a block");
        system.GetArrayLength().Should().Be(1);
        system[0].GetProperty("text").GetString().Should().Be(Instructions, "the system text is left untouched");
        system[0].GetProperty("cache_control").GetRawText().Should().Be("""{"type":"ephemeral"}""");
        sent.GetProperty("cache_control").GetRawText().Should().Be("""{"type":"ephemeral"}""", "automatic caching for the conversation");
        sent.GetProperty("tools")[0].TryGetProperty("cache_control", out _).Should().BeFalse("the system breakpoint already covers the tools");
        sent.GetRawText().Should().NotContain("prompt_cache_key");
    }

    [Fact]
    public async Task A_second_turn_of_the_same_conversation_reports_cache_reads()
    {
        int call = 0;
        AnthropicHttp = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                Interlocked.Increment(ref call) == 1
                    ? AnthropicReply("""{"input_tokens":12,"output_tokens":3,"cache_creation_input_tokens":1400,"cache_read_input_tokens":0}""")
                    : AnthropicReply("""{"input_tokens":30,"output_tokens":3,"cache_creation_input_tokens":40,"cache_read_input_tokens":1400}"""),
                Encoding.UTF8,
                "application/json"),
        });
        HttpClient client = await StartNativeAsync();

        (await client.PostAsync("/v1/responses", Json(FirstTurn))).StatusCode.Should().Be(HttpStatusCode.OK);
        (LastEntry.CacheReadTokens ?? 0, LastEntry.CacheCreationTokens).Should().Be((0, 1400));

        HttpResponseMessage second = await client.PostAsync("/v1/responses", Json(SecondTurn));

        second.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonElement usage = Parse(await second.Content.ReadAsStringAsync()).GetProperty("usage");
        usage.GetProperty("input_tokens_details").GetProperty("cached_tokens").GetInt32().Should().Be(1400);
        (LastEntry.InputTokens, LastEntry.CacheReadTokens, LastEntry.CacheCreationTokens).Should().Be((30, 1400, 40));

        // The cached prefix is byte-for-byte the same on both turns.
        SentBody(1).GetProperty("system").GetRawText().Should().Be(SentBody(0).GetProperty("system").GetRawText());
        SentBody(1).GetProperty("tools").GetRawText().Should().Be(SentBody(0).GetProperty("tools").GetRawText());
        SentBody(1).GetProperty("messages")[0].GetRawText().Should().Be(SentBody(0).GetProperty("messages")[0].GetRawText());
    }

    [Fact]
    public async Task Off_sends_no_markers()
    {
        HttpClient client = await StartNativeAsync(connector: c => c.PromptCaching = PromptCaching.Off);

        (await client.PostAsync("/v1/responses", Json(FirstTurn))).StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement sent = SentBody();
        sent.GetRawText().Should().NotContain("cache_control");
        sent.GetProperty("system").GetString().Should().Be(Instructions);
    }

    [Fact]
    public async Task A_one_hour_ttl_goes_on_both_markers()
    {
        HttpClient client = await StartNativeAsync(connector: c => c.CacheTtl = "1h");

        (await client.PostAsync("/v1/responses", Json(FirstTurn))).StatusCode.Should().Be(HttpStatusCode.OK);

        JsonElement sent = SentBody();
        sent.GetProperty("system")[0].GetProperty("cache_control").GetRawText().Should().Be("""{"type":"ephemeral","ttl":"1h"}""");
        sent.GetProperty("cache_control").GetRawText().Should().Be("""{"type":"ephemeral","ttl":"1h"}""");
    }

    [Fact]
    public async Task A_prompt_below_the_models_minimum_is_not_marked()
    {
        HttpClient client = await StartNativeAsync(backendModel: "claude-haiku-4-5-20251001");

        (await client.PostAsync("/v1/responses", Json(FirstTurn))).StatusCode.Should().Be(HttpStatusCode.OK);

        SentBody().GetRawText().Should().NotContain("cache_control", "this model caches from 4,096 tokens and the prompt is shorter");
    }

    [Fact]
    public async Task A_short_chat_is_not_marked()
    {
        HttpClient client = await StartNativeAsync();

        (await client.PostAsync("/v1/chat/completions", Json("""{"model":"native","messages":[{"role":"system","content":"Be brief."},{"role":"user","content":"hi"}]}""")))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        SentBody().GetRawText().Should().NotContain("cache_control", "a marker below the minimum caches nothing");
        SentBody().GetProperty("system").GetString().Should().Be("Be brief.");
    }

    [Theory]
    [InlineData("claude-opus-4-5", 4096)]
    [InlineData("claude-opus-4-6", 4096)]
    [InlineData("claude-opus-4-5-20251101", 4096)]
    [InlineData("claude-opus-4-5@20251101", 4096)]
    [InlineData("anthropic.claude-haiku-4-5-20251001-v1:0", 4096)]
    [InlineData("claude-haiku-4.5", 4096)]
    [InlineData("claude-opus-4-7", 512)]
    [InlineData("claude-sonnet-4-5", 512)]
    [InlineData("claude-haiku-4-55", 512)]
    [InlineData("some-local-model", 512)]
    public void The_cacheable_minimum_depends_on_the_model(string model, int minimum) =>
        AnthropicRequestBuilder.CacheableMinimum(model).Should().Be(minimum);

    [Fact]
    public void Without_a_system_prompt_the_prefix_breakpoint_goes_on_the_last_tool()
    {
        string schema = """{"type":"object","properties":{"path":{"type":"string","description":"The file to read, relative to the repository root."}}}""";
        string tools = string.Join(',', Enumerable.Range(0, 40).Select(i => $$$"""{"type":"function","function":{"name":"tool_{{{i}}}","description":"Reads a file and returns its contents with line numbers.","parameters":{{{schema}}}}}"""));
        ChatCompletionRequest request = ChatCompletionRequest.Parse(Parse($$"""{"model":"auto","messages":[{"role":"user","content":"hi"}],"tools":[{{tools}}]}"""));

        JsonElement sent = Parse(Encoding.UTF8.GetString(AnthropicRequestBuilder.Build(request, "claude-sonnet-4-6", caching: PromptCaching.Auto).Span));

        sent.TryGetProperty("system", out _).Should().BeFalse();
        JsonElement[] built = [.. sent.GetProperty("tools").EnumerateArray()];
        built[^1].GetProperty("cache_control").GetRawText().Should().Be("""{"type":"ephemeral"}""");
        built[..^1].Where(t => t.TryGetProperty("cache_control", out _)).Should().BeEmpty();
        sent.GetProperty("cache_control").GetRawText().Should().Be("""{"type":"ephemeral"}""");
    }

    [Fact]
    public void An_anthropic_clients_own_markers_are_kept_and_none_are_added()
    {
        ChatCompletionRequest request = ChatCompletionRequest.ParseAnthropic(Parse($$$"""
            {"model":"claude","max_tokens":100,"cache_control":{"type":"ephemeral"},
             "system":[{"type":"text","text":{{{JsonSerializer.Serialize(Instructions)}}}},{"type":"text","text":"Project notes.","cache_control":{"type":"ephemeral","ttl":"1h"}}],
             "tools":[{"name":"Read","input_schema":{"type":"object"},"cache_control":{"type":"ephemeral","ttl":"1h"}}],
             "messages":[
               {"role":"user","content":[{"type":"text","text":"Read it.","cache_control":{"type":"ephemeral"}}]},
               {"role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"Read","input":{}}]},
               {"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"text","cache_control":{"type":"ephemeral"}}]}]}
            """));

        request.CacheMarkerCount.Should().Be(5);
        JsonElement sent = Parse(Encoding.UTF8.GetString(AnthropicRequestBuilder.Build(request, "claude-sonnet-4-6", caching: PromptCaching.Auto).Span));

        JsonElement system = sent.GetProperty("system");
        system.GetArrayLength().Should().Be(2);
        system[0].TryGetProperty("cache_control", out _).Should().BeFalse();
        system[1].GetProperty("cache_control").GetRawText().Should().Be("""{"type":"ephemeral","ttl":"1h"}""");
        sent.GetProperty("tools")[0].GetProperty("cache_control").GetRawText().Should().Be("""{"type":"ephemeral","ttl":"1h"}""");
        sent.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("cache_control").GetRawText().Should().Be("""{"type":"ephemeral"}""");
        sent.GetProperty("messages")[2].GetProperty("content")[0].GetProperty("cache_control").GetRawText().Should().Be("""{"type":"ephemeral"}""");
        sent.GetProperty("cache_control").GetRawText().Should().Be("""{"type":"ephemeral"}""");
        Encoding.UTF8.GetString(AnthropicRequestBuilder.Build(request, "claude-sonnet-4-6", caching: PromptCaching.Auto).Span)
            .Split("cache_control").Length.Should().Be(6, "the client's five markers, and none of the router's");
    }

    [Fact]
    public void Text_that_mentions_cache_control_is_not_a_marker()
    {
        ChatCompletionRequest request = ChatCompletionRequest.ParseAnthropic(Parse("""
            {"model":"claude","max_tokens":100,"messages":[{"role":"user","content":"What does \"cache_control\": {} do?"}]}
            """));

        request.HasCacheMarkers.Should().BeFalse();
        DroppedFeatures.ForTranslation(request, ChatProtocol.OpenAI, reasoning: false).Should().BeEmpty();
    }

    [Fact]
    public void Markers_are_reported_dropped_only_where_the_backend_cannot_take_them()
    {
        ChatCompletionRequest request = ChatCompletionRequest.ParseAnthropic(Parse("""
            {"model":"claude","max_tokens":100,"messages":[{"role":"user","content":[{"type":"text","text":"hi","cache_control":{"type":"ephemeral"}}]}]}
            """));

        DroppedFeatures.ForTranslation(request, ChatProtocol.OpenAI, reasoning: false).Should().Equal("cache_control");
        DroppedFeatures.ForTranslation(request, ChatProtocol.Responses, reasoning: false).Should().Equal("cache_control");
        DroppedFeatures.ForCli(request).Should().Equal("cache_control");
        DroppedFeatures.ForTranslation(request, ChatProtocol.Anthropic, reasoning: false).Should().BeEmpty();
    }

    [Fact]
    public void The_settings_bind_round_trip_and_validate()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SemanticRouter:Connectors:claude:Type"] = "Anthropic",
                ["SemanticRouter:Connectors:claude:BaseAddress"] = "https://api.anthropic.example",
                ["SemanticRouter:Connectors:claude:PromptCaching"] = "Off",
                ["SemanticRouter:Connectors:claude:CacheTtl"] = "1h",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSemanticPromptRouting(configuration.GetSection(SemanticRouterOptions.SectionName));
        using ServiceProvider provider = services.BuildServiceProvider();

        ConnectorOptions bound = provider.GetRequiredService<IOptions<SemanticRouterConnectorsOptions>>().Value.Connectors["claude"];
        (bound.PromptCaching, bound.CacheTtl).Should().Be((PromptCaching.Off, "1h"));
        new ConnectorOptions().Should().Match<ConnectorOptions>(c => c.PromptCaching == PromptCaching.Auto && c.CacheTtl == "5m");

        var document = new RouterConfiguration();
        document.Connectors["claude"] = bound;
        RouterConfiguration copy = RouterConfiguration.FromJson(document.ToJson());
        (copy.Connectors["claude"].PromptCaching, copy.Connectors["claude"].CacheTtl).Should().Be((PromptCaching.Off, "1h"));

        copy.Connectors["claude"].CacheTtl = "30m";
        copy.GetDocumentValidationErrors().Should().Contain("Connector 'claude' CacheTtl must be 5m or 1h.");
    }
}
