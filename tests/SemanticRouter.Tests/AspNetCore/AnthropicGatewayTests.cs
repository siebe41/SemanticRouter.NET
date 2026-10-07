using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;
using static SemanticRouter.Tests.Support.TestEmbeddings;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>The Anthropic Messages front door (what Claude Code talks to) and cross-protocol dispatch.</summary>
public sealed class AnthropicGatewayTests : RouterHostTest
{
    private const string MessagesJson = """{"model":"claude-sonnet-9","max_tokens":128,"messages":[{"role":"user","content":"hello there"}]}""";

    private Task<HttpClient> StartDispatchAsync(
        Action<SemanticRouterOptions>? routing = null,
        Action<PromptRouterMiddlewareOptions>? middleware = null,
        Action<IServiceCollection>? services = null,
        bool withCli = false) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
                middleware?.Invoke(o);
            },
            routing,
            services,
            withCli: withCli);

    private static StringContent Anthropic(string json)
    {
        var content = new StringContent(json, Encoding.UTF8, "application/json");
        return content;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    [Fact]
    public async Task Serves_messages_requests_through_an_openai_backend_in_anthropic_format()
    {
        LocalHttp = StubHttpMessageHandler.Json("""{"choices":[{"message":{"role":"assistant","content":"Hi from qwen"},"finish_reason":"stop"}],"usage":{"prompt_tokens":5,"completion_tokens":3}}""");
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/messages", Anthropic(MessagesJson));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("local");
        JsonElement json = await ReadJsonAsync(response);
        json.GetProperty("type").GetString().Should().Be("message");
        json.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("Hi from qwen");
        json.GetProperty("stop_reason").GetString().Should().Be("end_turn");
        json.GetProperty("usage").GetProperty("input_tokens").GetInt32().Should().Be(5);

        (HttpRequestMessage upstream, string body) = LocalHttp.Requests.Single();
        upstream.RequestUri.Should().Be(new Uri("http://localhost:1234/v1/chat/completions"));
        JsonElement sent = JsonDocument.Parse(body).RootElement;
        sent.GetProperty("model").GetString().Should().Be("qwen3-8b");
        sent.GetProperty("messages")[0].GetProperty("content").GetString().Should().Be("hello there");
        sent.GetProperty("max_tokens").GetInt32().Should().Be(128);
        Classifier.Verify(c => c.ClassifyAsync("hello there", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Streams_openai_tool_calls_back_as_anthropic_events_and_pins_the_follow_up()
    {
        const string sse = """
            data: {"choices":[{"index":0,"delta":{"role":"assistant","content":"Let me look. "}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_7","type":"function","function":{"name":"Read","arguments":"{\"path\":\"a\"}"}}]}}]}

            data: {"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;
        LocalHttp = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        HttpClient client = await StartDispatchAsync(routing: o => o.Models.First(m => m.Name == "local").Capabilities.Add("tools"));

        HttpResponseMessage first = await client.PostAsync("/v1/messages", Anthropic("""
            {"model":"claude-sonnet-9","max_tokens":128,"stream":true,
             "tools":[{"name":"Read","input_schema":{"type":"object"}}],
             "messages":[{"role":"user","content":"read a"}]}
            """));

        first.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        string stream = await first.Content.ReadAsStringAsync();
        stream.Should().Contain("event: message_start").And.Contain("\"text_delta\",\"text\":\"Let me look. \"")
            .And.Contain("\"type\":\"tool_use\",\"id\":\"call_7\",\"name\":\"Read\"").And.Contain("\"stop_reason\":\"tool_use\"").And.EndWith("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");

        // The tool result comes back: a hard classification would normally pick "cloud", but the turn stays on "local".
        ClassifyAs(tier: 4, route: "reasoning");
        HttpResponseMessage second = await client.PostAsync("/v1/messages", Anthropic("""
            {"model":"claude-sonnet-9","max_tokens":128,
             "tools":[{"name":"Read","input_schema":{"type":"object"}}],
             "messages":[{"role":"user","content":"read a"},
                         {"role":"assistant","content":[{"type":"text","text":"Let me look. "},{"type":"tool_use","id":"call_7","name":"Read","input":{"path":"a"}}]},
                         {"role":"user","content":[{"type":"tool_result","tool_use_id":"call_7","content":"file contents"}]}]}
            """));

        second.Headers.GetValues("X-Prompt-Route-Reason").Should().Equal("ConversationAffinity");
        second.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("local");
        CloudHttp.Requests.Should().BeEmpty();
        JsonElement sent = JsonDocument.Parse(LocalHttp.Requests[1].Body).RootElement;
        sent.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()).Should().Equal("user", "assistant", "tool");
    }

    [Fact]
    public async Task Model_aliases_map_vendor_names_to_strategies()
    {
        HttpClient client = await StartDispatchAsync(middleware: o => o.ModelAliases["*opus*"] = "auto:best");

        HttpResponseMessage response = await client.PostAsync("/v1/messages",
            Anthropic("""{"model":"claude-opus-9","max_tokens":10,"messages":[{"role":"user","content":"hi"}]}"""));

        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("cloud");
        CloudHttp.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Catalog_names_and_routing_aliases_beat_alias_globs()
    {
        HttpClient client = await StartDispatchAsync(middleware: o =>
        {
            o.ModelAliases["c*"] = "auto:best";
            o.ModelAliases["auto*"] = "cloud";
        });

        HttpResponseMessage explicitModel = await client.PostAsync("/v1/messages",
            Anthropic("""{"model":"cloud","max_tokens":10,"messages":[{"role":"user","content":"hi"}]}"""));
        HttpResponseMessage alias = await client.PostAsync("/v1/messages",
            Anthropic("""{"model":"auto:cheapest","max_tokens":10,"messages":[{"role":"user","content":"hi"}]}"""));

        explicitModel.Headers.GetValues("X-Prompt-Route-Reason").Should().Equal("ModelOverride");
        explicitModel.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("cloud");
        alias.Headers.GetValues("X-Prompt-Route-Model").Should().Equal(["local"], "auto:* is never remapped");
    }

    [Fact]
    public async Task Unknown_vendor_names_route_as_auto()
    {
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/messages", Anthropic(MessagesJson));

        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("local");
        response.Headers.GetValues("X-Prompt-Route-Reason").Should().Equal("SemanticMatch");
    }

    [Fact]
    public async Task Errors_use_the_anthropic_format_on_messages_paths()
    {
        LocalHttp = StubHttpMessageHandler.Throws();
        CloudHttp = StubHttpMessageHandler.Json("{}", HttpStatusCode.InternalServerError);
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage failed = await client.PostAsync("/v1/messages", Anthropic(MessagesJson));
        HttpResponseMessage empty = await client.PostAsync("/v1/messages", Anthropic("""{"model":"x","max_tokens":1}"""));

        failed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        JsonElement error = await ReadJsonAsync(failed);
        error.GetProperty("type").GetString().Should().Be("error");
        error.GetProperty("error").GetProperty("type").GetString().Should().Be("overloaded_error");
        error.GetProperty("error").GetProperty("message").GetString().Should().Contain("local: unreachable");
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadJsonAsync(empty)).GetProperty("error").GetProperty("type").GetString().Should().Be("invalid_request_error");
    }

    [Fact]
    public async Task Count_tokens_is_served_without_routing()
    {
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/messages/count_tokens", Anthropic(MessagesJson));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(response)).GetProperty("input_tokens").GetInt32().Should().BePositive();
        VerifyNotClassified();
    }

    [Fact]
    public async Task Openai_clients_can_be_served_by_a_native_anthropic_connector()
    {
        ClassifyAs(tier: 5);
        HttpClient client = await StartDispatchAsync(
            routing: o => o.Models.Add(Model("opus-api", 5, costPerMillion: 15m, connector: "anthropic-native", capabilities: "tools")),
            services: s => s.Configure<SemanticRouterConnectorsOptions>(o => o.Connectors["anthropic-native"] = new ConnectorOptions
            {
                Type = ConnectorType.Anthropic,
                BaseAddress = new Uri("https://api.anthropic.example"),
                ApiKey = "sk-ant-test",
            }));

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("opus-api");
        JsonElement json = await ReadJsonAsync(response);
        json.GetProperty("object").GetString().Should().Be("chat.completion");
        json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString().Should().Be("from anthropic");

        (HttpRequestMessage upstream, string body) = AnthropicHttp.Requests.Single();
        upstream.RequestUri.Should().Be(new Uri("https://api.anthropic.example/v1/messages"));
        upstream.Headers.GetValues("x-api-key").Should().Equal("sk-ant-test");
        upstream.Headers.GetValues("anthropic-version").Should().Equal("2023-06-01");
        upstream.Headers.Authorization.Should().BeNull();
        JsonElement sent = JsonDocument.Parse(body).RootElement;
        sent.GetProperty("max_tokens").GetInt32().Should().Be(AnthropicRequestBuilder.DefaultMaxTokens);
        sent.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString().Should().Be("hello there");
    }

    [Fact]
    public async Task Anthropic_clients_pass_through_a_native_anthropic_connector_untouched()
    {
        ClassifyAs(tier: 5);
        HttpClient client = await StartDispatchAsync(
            routing: o => o.Models.Add(Model("opus-api", 5, costPerMillion: 15m, connector: "anthropic-native") is { } m ? Set(m, "claude-opus-5-5") : null!),
            services: s => s.Configure<SemanticRouterConnectorsOptions>(o => o.Connectors["anthropic-native"] = new ConnectorOptions
            {
                Type = ConnectorType.Anthropic,
                BaseAddress = new Uri("https://api.anthropic.example"),
                ApiKey = "sk-ant-test",
            }));

        HttpResponseMessage response = await client.PostAsync("/v1/messages",
            Anthropic("""{"model":"claude-sonnet-9","max_tokens":128,"metadata":{"user_id":"u1"},"messages":[{"role":"user","content":"hello there"}]}"""));

        (await response.Content.ReadAsStringAsync()).Should().Contain("from anthropic");
        JsonElement sent = JsonDocument.Parse(AnthropicHttp.Requests.Single().Body).RootElement;
        sent.GetProperty("model").GetString().Should().Be("claude-opus-5-5", "only the model is rewritten");
        sent.GetProperty("metadata").GetProperty("user_id").GetString().Should().Be("u1", "everything else passes through");
    }

    private static ModelDefinition Set(ModelDefinition model, string providerModel)
    {
        model.Model = providerModel;
        return model;
    }

    [Fact]
    public async Task Prompted_tool_calling_lets_a_cli_serve_agentic_turns()
    {
        ClassifyAs(tier: 4);
        string reply = "I'll check the file.\n```tool_call\n{\"name\": \"Read\", \"input\": {\"path\": \"README.md\"}}\n```";
        Cli = FakeCliProcessRunner.Replying(ClaudeStream.Delta("I'll check"), ClaudeStream.Success(reply.Replace("\n", "\\n").Replace("\"", "\\\"")));
        HttpClient client = await StartDispatchAsync(
            withCli: true,
            routing: o => o.Models.First(m => m.Name == "claude-cli").Capabilities.Add("tools"),
            services: s => s.Configure<SemanticRouterConnectorsOptions>(o => o.Connectors["claude"].ToolCalling = CliToolCalling.Prompted));

        HttpResponseMessage response = await client.PostAsync("/v1/messages", Anthropic("""
            {"model":"claude-sonnet-9","max_tokens":128,"system":"You are a coding agent.",
             "tools":[{"name":"Read","description":"Reads a file","input_schema":{"type":"object","properties":{"path":{"type":"string"}}}}],
             "messages":[{"role":"user","content":"summarize the readme"}]}
            """));

        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("claude-cli");
        JsonElement json = await ReadJsonAsync(response);
        json.GetProperty("stop_reason").GetString().Should().Be("tool_use");
        json.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("I'll check the file.");
        json.GetProperty("content")[1].GetProperty("type").GetString().Should().Be("tool_use");
        json.GetProperty("content")[1].GetProperty("name").GetString().Should().Be("Read");
        json.GetProperty("content")[1].GetProperty("input").GetProperty("path").GetString().Should().Be("README.md");

        CliInvocation invocation = Cli.Invocations.Single();
        int systemIndex = invocation.Arguments.ToList().IndexOf("--system-prompt");
        systemIndex.Should().BePositive();
        invocation.Arguments[systemIndex + 1].Should().StartWith("You are a coding agent.").And.Contain("### Read").And.Contain("```tool_call");
        CloudHttp.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Prompted_tool_calling_answers_openai_clients_with_tool_calls()
    {
        ClassifyAs(tier: 4);
        Cli = FakeCliProcessRunner.Replying(ClaudeStream.Success("```tool_call\\n{\\\"name\\\": \\\"f\\\", \\\"input\\\": {}}\\n```"));
        HttpClient client = await StartDispatchAsync(
            withCli: true,
            routing: o => o.Models.First(m => m.Name == "claude-cli").Capabilities.Add("tools"),
            services: s => s.Configure<SemanticRouterConnectorsOptions>(o => o.Connectors["claude"].ToolCalling = CliToolCalling.Prompted));

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json("""
            {"model":"auto","messages":[{"role":"user","content":"hello there"}],
             "tools":[{"type":"function","function":{"name":"f","parameters":{"type":"object"}}}]}
            """));

        JsonElement json = await ReadJsonAsync(response);
        json.GetProperty("choices")[0].GetProperty("finish_reason").GetString().Should().Be("tool_calls");
        json.GetProperty("choices")[0].GetProperty("message").GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString().Should().Be("f");
    }

    [Fact]
    public async Task Clis_without_prompted_tool_calling_still_skip_tool_requests()
    {
        ClassifyAs(tier: 4);
        HttpClient client = await StartDispatchAsync(withCli: true, routing: o => o.Models.First(m => m.Name == "claude-cli").Capabilities.Add("tools"));

        await client.PostAsync("/v1/messages", Anthropic("""
            {"model":"x","max_tokens":1,"tools":[{"name":"Read","input_schema":{"type":"object"}}],"messages":[{"role":"user","content":"hi"}]}
            """));

        Cli.Invocations.Should().BeEmpty();
        CloudHttp.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task Classifies_the_latest_human_message_without_system_reminders()
    {
        HttpClient client = await StartDispatchAsync();

        await client.PostAsync("/v1/messages", Anthropic("""
            {"model":"x","max_tokens":1,"messages":[
              {"role":"user","content":[{"type":"text","text":"<system-reminder>be careful</system-reminder>"},{"type":"text","text":"fix the bug"}]},
              {"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Read","input":{}}]},
              {"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"data"}]}]}
            """));

        Classifier.Verify(c => c.ClassifyAsync("fix the bug", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Large_agentic_requests_are_accepted_by_default()
    {
        HttpClient client = await StartDispatchAsync();
        string big = new('x', 2 * 1024 * 1024);

        HttpResponseMessage response = await client.PostAsync("/v1/messages",
            Anthropic($$"""{"model":"x","max_tokens":1,"system":"{{big}}","messages":[{"role":"user","content":"hello there"}]}"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the default body limit is 8 MiB");
    }
}
