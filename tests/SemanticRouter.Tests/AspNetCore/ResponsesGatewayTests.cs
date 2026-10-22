using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// The Responses front door (what Codex CLI talks to) served by every kind of backend, and the cross-protocol
/// features of #12: reasoning, structured output, parallel tool calls and stop reasons.
/// </summary>
public sealed class ResponsesGatewayTests : RouterHostTest
{
    private StubHttpMessageHandler ResponsesHttp { get; set; } = StubHttpMessageHandler.Json("""
        {"id":"resp_up","object":"response","status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"from responses"}]}],"usage":{"input_tokens":3,"output_tokens":2}}
        """);

    private Task<HttpClient> StartDispatchAsync(
        Action<SemanticRouterOptions>? routing = null,
        Action<IServiceCollection>? services = null,
        bool withCli = false) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
            },
            routing,
            s =>
            {
                s.AddHttpClient(OpenAICompatibleConnector.HttpClientNamePrefix + "responses").ConfigurePrimaryHttpMessageHandler(() => ResponsesHttp);
                services?.Invoke(s);
            },
            withCli: withCli);

    private static void AddNative(IServiceCollection services, string name, string type) =>
        services.Configure<SemanticRouterConnectorsOptions>(o => o.Connectors[name] = new ConnectorOptions
        {
            Type = type,
            BaseAddress = new Uri("https://api.example.net"),
            ApiKey = "sk-test",
        });

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static List<JsonElement> Events(string sse) =>
        [.. sse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries).Select(e => JsonDocument.Parse(e.Split('\n')[1]["data: ".Length..]).RootElement.Clone())];

    private static HttpResponseMessage Sse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    [Fact]
    public async Task Codex_is_served_by_an_openai_compatible_backend_with_namespaced_tools()
    {
        const string sse = """
            data: {"choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"Check agents."}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_9","type":"function","function":{"name":"collaboration__list_agents","arguments":"{}"}}]}}]}

            data: {"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":40,"completion_tokens":6}}

            data: [DONE]

            """;
        LocalHttp = new StubHttpMessageHandler((_, _) => Sse(sse));
        HttpClient client = await StartDispatchAsync(routing: o => o.Models.First(m => m.Name == "local").Capabilities.Add("tools"));

        HttpResponseMessage response = await client.PostAsync("/v1/responses", Json(ResponsesProtocolTests.CodexBody));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("local");
        response.Headers.GetValues("X-Prompt-Route-Dropped").Should().Equal("reasoning");
        List<JsonElement> events = Events(await response.Content.ReadAsStringAsync());
        events[0].GetProperty("type").GetString().Should().Be("response.created");
        JsonElement completed = events[^1];
        completed.GetProperty("type").GetString().Should().Be("response.completed");
        JsonElement[] output = [.. completed.GetProperty("response").GetProperty("output").EnumerateArray()];
        output.Select(o => o.GetProperty("type").GetString()).Should().Equal("reasoning", "function_call");
        output[1].GetProperty("name").GetString().Should().Be("list_agents");
        output[1].GetProperty("namespace").GetString().Should().Be("collaboration");
        output[1].GetProperty("call_id").GetString().Should().Be("call_9");
        completed.GetProperty("response").GetProperty("usage").GetProperty("input_tokens").GetInt32().Should().Be(40);

        JsonElement sent = JsonDocument.Parse(LocalHttp.Requests.Single().Body).RootElement;
        sent.GetProperty("model").GetString().Should().Be("qwen3-8b");
        sent.TryGetProperty("reasoning_effort", out _).Should().BeFalse("the model does not declare reasoning");
        sent.GetProperty("parallel_tool_calls").GetBoolean().Should().BeFalse();
        sent.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("function").GetProperty("name").GetString())
            .Should().Equal("exec", "wait", "collaboration__list_agents");
        JsonElement[] messages = [.. sent.GetProperty("messages").EnumerateArray()];
        messages[0].GetProperty("role").GetString().Should().Be("system");
        messages.Should().Contain(m => m.GetProperty("role").GetString() == "tool" && m.GetProperty("tool_call_id").GetString() == "call_ex");
        messages.Single(m => m.TryGetProperty("tool_calls", out JsonElement calls) && calls[0].GetProperty("id").GetString() == "call_la")
            .GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString().Should().Be("collaboration__list_agents");
    }

    [Fact]
    public async Task Codex_is_served_by_an_anthropic_backend_with_thinking_and_custom_tools()
    {
        const string sse = """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","content":[],"model":"claude","usage":{"input_tokens":50,"output_tokens":1}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Run ls."}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"exec","input":{}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"input_json_delta","partial_json":"{\"input\":\"ls -la\"}"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":1}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":9}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        AnthropicHttp = new StubHttpMessageHandler((_, _) => Sse(sse));
        ClassifyAs(tier: 5);
        HttpClient client = await StartDispatchAsync(
            routing: o => o.Models.Add(Model("opus-api", 5, costPerMillion: 15m, connector: "anthropic-native", capabilities: ["tools", "reasoning"])),
            services: s => AddNative(s, "anthropic-native", ConnectorTypes.Anthropic));

        HttpResponseMessage response = await client.PostAsync("/v1/responses", Json(ResponsesProtocolTests.CodexBody));

        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("opus-api");
        response.Headers.Contains("X-Prompt-Route-Dropped").Should().BeFalse();
        List<JsonElement> events = Events(await response.Content.ReadAsStringAsync());
        events.Select(e => e.GetProperty("type").GetString()).Should().Contain("response.reasoning_summary_text.delta").And.Contain("response.custom_tool_call_input.done");
        JsonElement[] output = [.. events[^1].GetProperty("response").GetProperty("output").EnumerateArray()];
        output.Select(o => o.GetProperty("type").GetString()).Should().Equal("reasoning", "custom_tool_call");
        output[0].GetProperty("summary")[0].GetProperty("text").GetString().Should().Be("Run ls.");
        output[1].GetProperty("name").GetString().Should().Be("exec");
        output[1].GetProperty("input").GetString().Should().Be("ls -la");
        output[1].GetProperty("call_id").GetString().Should().Be("toolu_1");

        JsonElement sent = JsonDocument.Parse(AnthropicHttp.Requests.Single().Body).RootElement;
        sent.GetProperty("thinking").GetProperty("budget_tokens").GetInt32().Should().Be(2048, "effort low");
        sent.GetProperty("tool_choice").GetProperty("disable_parallel_tool_use").GetBoolean().Should().BeTrue();
        sent.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).Should().Equal("exec", "wait", "collaboration__list_agents");
        sent.GetProperty("tools")[0].GetProperty("input_schema").GetProperty("required")[0].GetString().Should().Be("input");
        sent.GetProperty("system").GetString().Should().StartWith("You are Codex");
    }

    [Fact]
    public async Task Codex_is_served_by_a_cli_with_prompted_tool_calling()
    {
        ClassifyAs(tier: 4);
        string reply = "Listing.\n```tool_call\n{\"name\": \"collaboration__list_agents\", \"input\": {}}\n```";
        Cli = FakeCliProcessRunner.Replying(ClaudeStream.Success(reply.Replace("\n", "\\n").Replace("\"", "\\\"")));
        HttpClient client = await StartDispatchAsync(
            withCli: true,
            routing: o => o.Models.First(m => m.Name == "claude-cli").Capabilities.Add("tools"),
            services: s => s.Configure<SemanticRouterConnectorsOptions>(o => o.Connectors["claude"].ToolCalling = CliToolCalling.Prompted));

        string body = ResponsesProtocolTests.CodexBody.Replace("\"stream\":true", "\"stream\":false", StringComparison.Ordinal);
        HttpResponseMessage response = await client.PostAsync("/v1/responses", Json(body));

        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("claude-cli");
        response.Headers.GetValues("X-Prompt-Route-Dropped").Should().Equal("reasoning, parallel_tool_calls");
        JsonElement json = await ReadJsonAsync(response);
        JsonElement[] output = [.. json.GetProperty("output").EnumerateArray()];
        output.Select(o => o.GetProperty("type").GetString()).Should().Equal("message", "function_call");
        output[1].GetProperty("name").GetString().Should().Be("list_agents");
        output[1].GetProperty("namespace").GetString().Should().Be("collaboration");
        Cli.Invocations.Single().Arguments.Should().Contain(a => a.Contains("### exec", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hosted_tools_pass_through_a_responses_backend()
    {
        ClassifyAs(tier: 3);
        HttpClient client = await StartDispatchAsync(
            routing: o => o.Models.Add(Model("gpt-api", 3, costPerMillion: 2m, connector: "responses")),
            services: s => AddNative(s, "responses", ConnectorTypes.Responses));
        const string request = """{"model":"auto","input":"news today?","tools":[{"type":"web_search"}],"store":true}""";

        HttpResponseMessage response = await client.PostAsync("/v1/responses", Json(request));

        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("gpt-api");
        response.Headers.Contains("X-Prompt-Route-Dropped").Should().BeFalse();
        (await ReadJsonAsync(response)).GetProperty("id").GetString().Should().Be("resp_up");
        (HttpRequestMessage upstream, string sentBody) = ResponsesHttp.Requests.Single();
        upstream.RequestUri.Should().Be(new Uri("https://api.example.net/v1/responses"));
        JsonElement sent = JsonDocument.Parse(sentBody).RootElement;
        sent.GetProperty("tools")[0].GetProperty("type").GetString().Should().Be("web_search");
        sent.GetProperty("store").GetBoolean().Should().BeTrue("pass-through keeps the body");
    }

    [Fact]
    public async Task Other_backends_answer_without_hosted_tools_and_say_so()
    {
        LocalHttp = StubHttpMessageHandler.Json("""{"choices":[{"message":{"role":"assistant","content":"no search here"},"finish_reason":"stop"}]}""");
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/responses", Json("""
            {"model":"auto","input":"news today?","tools":[{"type":"web_search"}]}
            """));

        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("local");
        response.Headers.GetValues("X-Prompt-Route-Dropped").Should().Equal("web_search");
        (await ReadJsonAsync(response)).GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString().Should().Be("no search here");
        JsonDocument.Parse(LocalHttp.Requests.Single().Body).RootElement.TryGetProperty("tools", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Openai_clients_can_be_served_by_a_responses_backend()
    {
        ClassifyAs(tier: 3);
        HttpClient client = await StartDispatchAsync(
            routing: o => o.Models.Add(Model("gpt-api", 3, costPerMillion: 2m, connector: "responses")),
            services: s => AddNative(s, "responses", ConnectorTypes.Responses));

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("gpt-api");
        JsonElement json = await ReadJsonAsync(response);
        json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString().Should().Be("from responses");
        JsonElement sent = JsonDocument.Parse(ResponsesHttp.Requests.Single().Body).RootElement;
        sent.GetProperty("input")[0].GetProperty("role").GetString().Should().Be("user");
        sent.GetProperty("store").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Only_the_responses_path_itself_is_routed()
    {
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/responses/resp_1/cancel", Json("{}"));

        response.Headers.Contains("X-Prompt-Route-Model").Should().BeFalse();
        LocalHttp.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Anthropic_thinking_becomes_reasoning_effort_and_comes_back_as_a_signed_thinking_block()
    {
        LocalHttp = StubHttpMessageHandler.Json("""
            {"choices":[{"message":{"role":"assistant","content":"42","reasoning_content":"6 times 7."},"finish_reason":"stop"}]}
            """);
        HttpClient client = await StartDispatchAsync(routing: o => o.Models.First(m => m.Name == "local").Capabilities.Add("reasoning"));

        HttpResponseMessage response = await client.PostAsync("/v1/messages", Json("""
            {"model":"claude-sonnet-9","max_tokens":20000,"thinking":{"type":"enabled","budget_tokens":16000},
             "messages":[{"role":"user","content":"6*7?"}]}
            """));

        response.Headers.Contains("X-Prompt-Route-Dropped").Should().BeFalse();
        JsonElement json = await ReadJsonAsync(response);
        json.GetProperty("content")[0].GetProperty("type").GetString().Should().Be("thinking");
        json.GetProperty("content")[0].GetProperty("thinking").GetString().Should().Be("6 times 7.");
        json.GetProperty("content")[0].GetProperty("signature").GetString().Should().Be(ThinkingBlock.RouterSignature);
        json.GetProperty("content")[1].GetProperty("text").GetString().Should().Be("42");
        JsonDocument.Parse(LocalHttp.Requests.Single().Body).RootElement.GetProperty("reasoning_effort").GetString().Should().Be("high");
    }

    [Fact]
    public async Task Relayed_thinking_is_stripped_before_a_native_anthropic_backend_sees_it()
    {
        ClassifyAs(tier: 5);
        HttpClient client = await StartDispatchAsync(
            routing: o => o.Models.Add(Model("opus-api", 5, costPerMillion: 15m, connector: "anthropic-native")),
            services: s => AddNative(s, "anthropic-native", ConnectorTypes.Anthropic));

        await client.PostAsync("/v1/messages", Json($$"""
            {"model":"claude-sonnet-9","max_tokens":100,"messages":[
              {"role":"user","content":"6*7?"},
              {"role":"assistant","content":[{"type":"thinking","thinking":"6 times 7.","signature":"{{ThinkingBlock.RouterSignature}}"},
                                             {"type":"thinking","thinking":"real","signature":"sig-from-anthropic"},
                                             {"type":"text","text":"42"}]},
              {"role":"user","content":"and 6*8?"}]}
            """));

        JsonElement assistant = JsonDocument.Parse(AnthropicHttp.Requests.Single().Body).RootElement.GetProperty("messages")[1];
        assistant.GetProperty("content").EnumerateArray().Select(b => b.GetProperty("type").GetString()).Should().Equal("thinking", "text");
        assistant.GetProperty("content")[0].GetProperty("signature").GetString().Should().Be("sig-from-anthropic");
    }

    [Fact]
    public async Task Json_schema_replies_from_an_anthropic_backend_arrive_as_text()
    {
        AnthropicHttp = StubHttpMessageHandler.Json("""
            {"type":"message","role":"assistant","content":[{"type":"tool_use","id":"toolu_s","name":"structured_output","input":{"answer":42}}],"stop_reason":"tool_use","usage":{"input_tokens":9,"output_tokens":4}}
            """);
        ClassifyAs(tier: 5);
        HttpClient client = await StartDispatchAsync(
            routing: o => o.Models.Add(Model("opus-api", 5, costPerMillion: 15m, connector: "anthropic-native", capabilities: ["reasoning"])),
            services: s => AddNative(s, "anthropic-native", ConnectorTypes.Anthropic));

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json("""
            {"model":"auto","reasoning_effort":"high","messages":[{"role":"user","content":"6*7?"}],
             "response_format":{"type":"json_schema","json_schema":{"name":"answer","schema":{"type":"object","properties":{"answer":{"type":"integer"}}}}}}
            """));

        response.Headers.GetValues("X-Prompt-Route-Dropped").Should().Equal("reasoning");
        JsonElement choice = (await ReadJsonAsync(response)).GetProperty("choices")[0];
        choice.GetProperty("finish_reason").GetString().Should().Be("stop");
        JsonDocument.Parse(choice.GetProperty("message").GetProperty("content").GetString()!).RootElement.GetProperty("answer").GetInt32().Should().Be(42);
        choice.GetProperty("message").TryGetProperty("tool_calls", out _).Should().BeFalse();

        JsonElement sent = JsonDocument.Parse(AnthropicHttp.Requests.Single().Body).RootElement;
        sent.GetProperty("tools")[0].GetProperty("name").GetString().Should().Be(AnthropicRequestBuilder.StructuredOutputTool);
        sent.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("tool");
        sent.TryGetProperty("thinking", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Serial_tool_calls_and_refusals_cross_protocols()
    {
        AnthropicHttp = StubHttpMessageHandler.Json("""
            {"type":"message","role":"assistant","content":[{"type":"text","text":"I can't help with that."}],"stop_reason":"refusal","usage":{"input_tokens":9,"output_tokens":4}}
            """);
        ClassifyAs(tier: 5);
        HttpClient client = await StartDispatchAsync(
            routing: o => o.Models.Add(Model("opus-api", 5, costPerMillion: 15m, connector: "anthropic-native", capabilities: ["tools"])),
            services: s => AddNative(s, "anthropic-native", ConnectorTypes.Anthropic));

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json("""
            {"model":"auto","parallel_tool_calls":false,"messages":[{"role":"user","content":"hi"}],
             "tools":[{"type":"function","function":{"name":"f","parameters":{"type":"object"}}}]}
            """));

        (await ReadJsonAsync(response)).GetProperty("choices")[0].GetProperty("finish_reason").GetString().Should().Be("content_filter");
        JsonElement sent = JsonDocument.Parse(AnthropicHttp.Requests.Single().Body).RootElement;
        sent.GetProperty("tool_choice").GetProperty("type").GetString().Should().Be("auto");
        sent.GetProperty("tool_choice").GetProperty("disable_parallel_tool_use").GetBoolean().Should().BeTrue();
    }

    private const string ClaudeCodeThinkingBody = """
        {"model":"claude-sonnet-9","max_tokens":4000,"thinking":{"type":"enabled","budget_tokens":2000},
         "system":[{"type":"text","text":"You are Claude Code.","cache_control":{"type":"ephemeral"}}],
         "messages":[{"role":"user","content":"hi"}]}
        """;

    [Fact]
    public async Task Claude_code_thinking_and_cache_markers_pass_through_an_anthropic_backend()
    {
        ClassifyAs(tier: 5);
        HttpClient client = await StartDispatchAsync(
            routing: o => o.Models.Add(Model("opus-api", 5, costPerMillion: 15m, connector: "anthropic-native", capabilities: ["reasoning"])),
            services: s => AddNative(s, "anthropic-native", ConnectorTypes.Anthropic));

        HttpResponseMessage response = await client.PostAsync("/v1/messages", Json(ClaudeCodeThinkingBody));

        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("opus-api");
        response.Headers.Contains("X-Prompt-Route-Dropped").Should().BeFalse();
        JsonElement sent = JsonDocument.Parse(AnthropicHttp.Requests.Single().Body).RootElement;
        sent.GetProperty("thinking").GetProperty("budget_tokens").GetInt32().Should().Be(2000);
        sent.GetProperty("system")[0].GetProperty("cache_control").GetProperty("type").GetString().Should().Be("ephemeral");
    }

    [Fact]
    public async Task Claude_code_thinking_and_cache_markers_are_reported_when_translated()
    {
        LocalHttp = StubHttpMessageHandler.Json("""{"choices":[{"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}]}""");
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/messages", Json(ClaudeCodeThinkingBody));

        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("local");
        response.Headers.GetValues("X-Prompt-Route-Dropped").Should().Equal("cache_control, reasoning");
        JsonDocument.Parse(LocalHttp.Requests.Single().Body).RootElement.GetProperty("messages")[0].GetProperty("content").GetString().Should().Be("You are Claude Code.");
    }

    [Fact]
    public async Task Clis_report_the_structured_output_they_cannot_enforce()
    {
        ClassifyAs(tier: 4);
        HttpClient client = await StartDispatchAsync(withCli: true);

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json("""
            {"model":"claude-cli","messages":[{"role":"user","content":"hi"}],"response_format":{"type":"json_object"}}
            """));

        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("claude-cli");
        response.Headers.GetValues("X-Prompt-Route-Dropped").Should().Equal("response_format");
    }

    [Fact]
    public void Anthropic_thinking_budget_fits_below_max_tokens_and_drops_sampling_settings()
    {
        ChatCompletionRequest request = ChatCompletionRequest.Parse(JsonDocument.Parse("""
            {"model":"auto","reasoning_effort":"high","max_tokens":4000,"temperature":0.3,"top_p":0.9,"messages":[{"role":"user","content":"hi"}]}
            """).RootElement);

        JsonElement sent = JsonDocument.Parse(AnthropicRequestBuilder.Build(request, "claude", reasoning: true)).RootElement;

        sent.GetProperty("thinking").GetProperty("budget_tokens").GetInt32().Should().Be(3999);
        sent.TryGetProperty("temperature", out _).Should().BeFalse();
        sent.TryGetProperty("top_p", out _).Should().BeFalse();

        JsonElement without = JsonDocument.Parse(AnthropicRequestBuilder.Build(request, "claude", reasoning: false)).RootElement;
        without.TryGetProperty("thinking", out _).Should().BeFalse();
        without.GetProperty("temperature").GetDouble().Should().Be(0.3);
    }
}
