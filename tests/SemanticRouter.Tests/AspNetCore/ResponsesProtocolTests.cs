using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>The OpenAI Responses API: parsing what Codex sends, writing replies, reading Responses backends.</summary>
public sealed class ResponsesProtocolTests
{
    /// <summary>A second-turn request recorded from Codex CLI 0.161.0, trimmed to a few tools and shortened texts.</summary>
    internal const string CodexBody = """
        {"model":"gpt-6.1-sol","stream":true,"store":false,"tool_choice":"auto","parallel_tool_calls":false,
         "reasoning":{"effort":"low","context":"all_turns"},"include":["reasoning.encrypted_content"],"text":{"verbosity":"low"},
         "prompt_cache_key":"01a11bd1","client_metadata":{"session_id":"01a11bd1"},
         "input":[
          {"type":"additional_tools","id":"at_1","role":"developer","tools":[
            {"type":"namespace","name":"functions","description":"","tools":[
              {"type":"custom","name":"exec","description":"Run JavaScript code","format":{"type":"grammar","syntax":"lark","definition":"start: SOURCE\nSOURCE: /[\\s\\S]+/"}},
              {"type":"function","name":"wait","strict":false,"parameters":{"type":"object","properties":{"cell_id":{"type":"string"}},"required":["cell_id"]}}]},
            {"type":"namespace","name":"collaboration","description":"Tools for spawning and managing sub-agents.","tools":[
              {"type":"function","name":"list_agents","strict":false,"parameters":{"type":"object","properties":{"path_prefix":{"type":"string"}}}}]}]},
          {"type":"message","role":"developer","content":[{"type":"input_text","text":"You are Codex, a coding agent."}]},
          {"type":"message","role":"user","content":[{"type":"input_text","text":"<environment_context>\n  <cwd>/work</cwd>\n</environment_context>"}]},
          {"type":"message","role":"user","content":[{"type":"input_text","text":"list agents"}]},
          {"type":"function_call","id":"fc_1","name":"list_agents","namespace":"collaboration","arguments":"{}","call_id":"call_la"},
          {"type":"function_call_output","call_id":"call_la","output":"[]"},
          {"type":"custom_tool_call","id":"ctc_1","name":"exec","input":"console.log(1)","call_id":"call_ex"},
          {"type":"custom_tool_call_output","call_id":"call_ex","output":"1"}]}
        """;

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static (DefaultHttpContext Context, MemoryStream Body) NewContext()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        return (context, body);
    }

    [Fact]
    public void Parses_a_recorded_codex_request()
    {
        ChatCompletionRequest request = ChatCompletionRequest.Parse(Json(CodexBody), ChatProtocol.Responses);

        request.Protocol.Should().Be(ChatProtocol.Responses);
        request.Model.Should().Be("gpt-6.1-sol");
        request.Stream.Should().BeTrue();
        request.Tools.Select(t => t.Name).Should().Equal("exec", "wait", "collaboration__list_agents");
        request.Tools[0].Kind.Should().Be(ToolKind.Custom);
        request.Tools[0].Description.Should().Contain("lark grammar");
        request.Tools[2].Namespace.Should().Be("collaboration");
        request.Tools[2].OriginalName.Should().Be("list_agents");
        request.Reasoning.Should().Be(new ReasoningRequest { Effort = "low" });
        request.ParallelToolCalls.Should().BeFalse();
        request.HostedTools.Should().BeEmpty();
        request.ProtocolOnlyFeatures.Should().BeEmpty("encrypted reasoning in include is not logprobs");

        request.Turns.Select(t => t.Role).Should().Equal("system", "user", "user", "assistant", "user", "assistant", "user");
        request.Turns[3].Blocks.Should().ContainSingle().Which.Should().Be(new ToolUseBlock
        {
            Id = "call_la",
            Name = "collaboration__list_agents",
            InputJson = "{}",
        });
        request.Turns[5].Blocks.Should().ContainSingle().Which.Should().Be(new ToolUseBlock
        {
            Id = "call_ex",
            Name = "exec",
            InputJson = """{"input":"console.log(1)"}""",
        });
        request.Turns[6].IsToolResultOnly.Should().BeTrue();
        request.IsToolResultContinuation.Should().BeTrue();
        request.AnsweredToolUseIds.Should().Equal("call_ex");
    }

    [Fact]
    public void Hosted_tools_and_server_state_are_recognised()
    {
        ChatCompletionRequest request = ChatCompletionRequest.Parse(Json("""
            {"model":"auto","instructions":"Be brief.","input":"news today?","previous_response_id":"resp_1",
             "tools":[{"type":"web_search"},{"type":"function","name":"f","parameters":{"type":"object"}}],
             "include":["message.output_text.logprobs"],"max_output_tokens":50,
             "text":{"format":{"type":"json_schema","name":"answer","schema":{"type":"object"},"strict":true}}}
            """), ChatProtocol.Responses);

        request.Turns.Select(t => (t.Role, t.Text)).Should().Equal(("system", "Be brief."), ("user", "news today?"));
        request.HostedTools.Should().Equal("web_search");
        request.Tools.Should().ContainSingle().Which.Name.Should().Be("f");
        request.ProtocolOnlyFeatures.Should().Equal("logprobs", "previous_response_id");
        request.MaxTokens.Should().Be(50);
        request.ResponseFormat.Should().Be(new ResponseFormat
        {
            Kind = ResponseFormatKind.JsonSchema,
            Name = "answer",
            SchemaJson = """{"type":"object"}""",
            Strict = true,
        });
    }

    [Fact]
    public void Builds_a_responses_request_with_custom_tools_and_tool_outputs()
    {
        ChatCompletionRequest request = ChatCompletionRequest.Parse(Json(CodexBody), ChatProtocol.Responses);

        JsonElement sent = Json(Encoding.UTF8.GetString(ResponsesRequestBuilder.Build(request, "gpt-x", reasoning: true).Span));

        sent.GetProperty("model").GetString().Should().Be("gpt-x");
        sent.GetProperty("store").GetBoolean().Should().BeFalse();
        sent.GetProperty("parallel_tool_calls").GetBoolean().Should().BeFalse();
        sent.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("low");
        JsonElement[] tools = [.. sent.GetProperty("tools").EnumerateArray()];
        tools.Select(t => t.GetProperty("type").GetString()).Should().Equal("custom", "function", "function");
        tools[2].GetProperty("name").GetString().Should().Be("collaboration__list_agents");
        string[] items = [.. sent.GetProperty("input").EnumerateArray().Select(i => i.GetProperty("type").GetString()!)];
        items.Should().ContainInOrder("function_call", "function_call_output", "custom_tool_call", "custom_tool_call_output");
    }

    [Fact]
    public async Task Writer_produces_a_response_object_with_namespaced_and_custom_tool_calls()
    {
        ChatCompletionRequest request = ChatCompletionRequest.Parse(Json(CodexBody), ChatProtocol.Responses);
        (DefaultHttpContext context, MemoryStream body) = NewContext();
        var writer = new ResponsesResponseWriter(context, "local", stream: false, request.Tools);

        await writer.WriteThinkingAsync("Thinking it over.", CancellationToken.None);
        await writer.WriteTextAsync("Listing.", CancellationToken.None);
        await writer.StartToolCallAsync("call_1", "collaboration__list_agents", CancellationToken.None);
        await writer.WriteToolCallArgumentsAsync("{}", CancellationToken.None);
        await writer.EndToolCallAsync(CancellationToken.None);
        await writer.StartToolCallAsync("call_2", "exec", CancellationToken.None);
        await writer.WriteToolCallArgumentsAsync("""{"input":"console.log(2)"}""", CancellationToken.None);
        await writer.EndToolCallAsync(CancellationToken.None);
        await writer.CompleteAsync(ChatStopReason.ToolUse, new ChatUsage { InputTokens = 10, OutputTokens = 4, CacheReadTokens = 6 }, CancellationToken.None);

        JsonElement json = Json(Encoding.UTF8.GetString(body.ToArray()));
        json.GetProperty("object").GetString().Should().Be("response");
        json.GetProperty("status").GetString().Should().Be("completed");
        JsonElement[] output = [.. json.GetProperty("output").EnumerateArray()];
        output.Select(o => o.GetProperty("type").GetString()).Should().Equal("reasoning", "message", "function_call", "custom_tool_call");
        output[0].GetProperty("summary")[0].GetProperty("text").GetString().Should().Be("Thinking it over.");
        output[1].GetProperty("content")[0].GetProperty("text").GetString().Should().Be("Listing.");
        output[2].GetProperty("name").GetString().Should().Be("list_agents");
        output[2].GetProperty("namespace").GetString().Should().Be("collaboration");
        output[2].GetProperty("call_id").GetString().Should().Be("call_1");
        output[3].GetProperty("name").GetString().Should().Be("exec");
        output[3].GetProperty("input").GetString().Should().Be("console.log(2)");
        output[3].TryGetProperty("namespace", out _).Should().BeFalse();
        json.GetProperty("usage").GetProperty("input_tokens").GetInt32().Should().Be(16);
        json.GetProperty("usage").GetProperty("input_tokens_details").GetProperty("cached_tokens").GetInt32().Should().Be(6);
        json.GetProperty("usage").GetProperty("output_tokens").GetInt32().Should().Be(4);
    }

    [Fact]
    public async Task Writer_streams_the_documented_event_sequence()
    {
        (DefaultHttpContext context, MemoryStream body) = NewContext();
        var writer = new ResponsesResponseWriter(context, "local", stream: true, []);

        await writer.WriteTextAsync("Hel", CancellationToken.None);
        await writer.WriteTextAsync("lo", CancellationToken.None);
        await writer.StartToolCallAsync("call_1", "f", CancellationToken.None);
        await writer.WriteToolCallArgumentsAsync("{\"a\":", CancellationToken.None);
        await writer.WriteToolCallArgumentsAsync("1}", CancellationToken.None);
        await writer.EndToolCallAsync(CancellationToken.None);
        await writer.CompleteAsync(ChatStopReason.ToolUse, new ChatUsage { InputTokens = 3, OutputTokens = 2 }, CancellationToken.None);

        context.Response.ContentType.Should().Be("text/event-stream");
        (string Event, JsonElement Data)[] events = [.. Encoding.UTF8.GetString(body.ToArray())
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Split('\n'))
            .Select(lines => (lines[0]["event: ".Length..], Json(lines[1]["data: ".Length..])))];

        events.Select(e => e.Event).Should().Equal(
            "response.created", "response.in_progress",
            "response.output_item.added", "response.content_part.added", "response.output_text.delta", "response.output_text.delta",
            "response.output_text.done", "response.content_part.done", "response.output_item.done",
            "response.output_item.added", "response.function_call_arguments.delta", "response.function_call_arguments.delta",
            "response.function_call_arguments.done", "response.output_item.done",
            "response.completed");
        events.Select(e => e.Data.GetProperty("type").GetString()).Should().Equal(events.Select(e => e.Event));
        events.Select(e => e.Data.GetProperty("sequence_number").GetInt32()).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        events[8].Data.GetProperty("item").GetProperty("content")[0].GetProperty("text").GetString().Should().Be("Hello");
        events[13].Data.GetProperty("item").GetProperty("arguments").GetString().Should().Be("{\"a\":1}");
        JsonElement completed = events[^1].Data.GetProperty("response");
        completed.GetProperty("status").GetString().Should().Be("completed");
        completed.GetProperty("output").GetArrayLength().Should().Be(2);
        completed.GetProperty("usage").GetProperty("total_tokens").GetInt32().Should().Be(5);
    }

    [Fact]
    public async Task Writer_reports_a_cut_off_reply_as_incomplete()
    {
        (DefaultHttpContext context, MemoryStream body) = NewContext();
        var writer = new ResponsesResponseWriter(context, "local", stream: false, []);

        await writer.WriteTextAsync("partial", CancellationToken.None);
        await writer.CompleteAsync(ChatStopReason.MaxTokens, default, CancellationToken.None);

        JsonElement json = Json(Encoding.UTF8.GetString(body.ToArray()));
        json.GetProperty("status").GetString().Should().Be("incomplete");
        json.GetProperty("incomplete_details").GetProperty("reason").GetString().Should().Be("max_output_tokens");
    }

    private static HttpResponseMessage Upstream(string body, string mediaType) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    [Fact]
    public async Task Reader_relays_a_streamed_responses_backend()
    {
        const string sse = """
            event: response.created
            data: {"type":"response.created","response":{"id":"resp_1","status":"in_progress"}}

            event: response.output_item.added
            data: {"type":"response.output_item.added","output_index":0,"item":{"type":"reasoning","id":"rs_1","summary":[]}}

            event: response.reasoning_summary_text.delta
            data: {"type":"response.reasoning_summary_text.delta","item_id":"rs_1","output_index":0,"summary_index":0,"delta":"Hmm."}

            event: response.output_item.added
            data: {"type":"response.output_item.added","output_index":1,"item":{"type":"message","id":"msg_1","role":"assistant","content":[]}}

            event: response.output_text.delta
            data: {"type":"response.output_text.delta","item_id":"msg_1","output_index":1,"content_index":0,"delta":"Running it."}

            event: response.output_item.added
            data: {"type":"response.output_item.added","output_index":2,"item":{"type":"custom_tool_call","id":"ctc_1","call_id":"call_x","name":"exec","input":""}}

            event: response.custom_tool_call_input.delta
            data: {"type":"response.custom_tool_call_input.delta","item_id":"ctc_1","output_index":2,"delta":"ls \"a\""}

            event: response.output_item.done
            data: {"type":"response.output_item.done","output_index":2,"item":{"type":"custom_tool_call","id":"ctc_1","call_id":"call_x","name":"exec","input":"ls \"a\""}}

            event: response.completed
            data: {"type":"response.completed","response":{"id":"resp_1","status":"completed","usage":{"input_tokens":30,"input_tokens_details":{"cached_tokens":10},"output_tokens":5}}}

            """;
        var writer = new RecordingWriter();

        await ResponsesResponseReader.RelayAsync(Upstream(sse, "text/event-stream"), writer, CancellationToken.None);

        writer.Events.Should().Equal("think:Hmm.", "text:Running it.", "tool:call_x:exec", """args:{"input":"ls \"a\""}""", "end", "done:ToolUse:20/5/10");
    }

    [Fact]
    public async Task Reader_relays_a_json_response()
    {
        var writer = new RecordingWriter();

        await ResponsesResponseReader.RelayAsync(Upstream("""
            {"id":"resp_1","object":"response","status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},
             "output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Hi"}]},
                       {"type":"function_call","call_id":"call_1","name":"f","arguments":"{}"}],
             "usage":{"input_tokens":4,"output_tokens":2}}
            """, "application/json"), writer, CancellationToken.None);

        writer.Events.Should().Equal("text:Hi", "tool:call_1:f", "args:{}", "end", "done:ToolUse:4/2/");
    }

    [Theory]
    [InlineData(ChatProtocol.OpenAI, false, new[] { "reasoning", "logprobs" })]
    [InlineData(ChatProtocol.OpenAI, true, new[] { "logprobs" })]
    [InlineData(ChatProtocol.Anthropic, true, new[] { "logprobs" })]
    [InlineData(ChatProtocol.Responses, false, new string[0])]
    public void Dropped_features_depend_on_the_backend(ChatProtocol backend, bool reasoning, string[] expected)
    {
        ChatCompletionRequest request = ChatCompletionRequest.Parse(Json("""
            {"model":"auto","input":"hi","reasoning":{"effort":"high"},"include":["message.output_text.logprobs"]}
            """), ChatProtocol.Responses);

        DroppedFeatures.ForTranslation(request, backend, reasoning).Should().BeEquivalentTo(expected);
    }

    private sealed class RecordingWriter : IChatResponseWriter
    {
        public List<string> Events { get; } = [];

        public bool HasStarted => Events.Count > 0;

        public Task WriteTextAsync(string delta, CancellationToken cancellationToken) { Events.Add("text:" + delta); return Task.CompletedTask; }

        public Task WriteThinkingAsync(string delta, CancellationToken cancellationToken) { Events.Add("think:" + delta); return Task.CompletedTask; }

        public Task StartToolCallAsync(string id, string name, CancellationToken cancellationToken) { Events.Add($"tool:{id}:{name}"); return Task.CompletedTask; }

        public Task WriteToolCallArgumentsAsync(string delta, CancellationToken cancellationToken) { Events.Add("args:" + delta); return Task.CompletedTask; }

        public Task EndToolCallAsync(CancellationToken cancellationToken) { Events.Add("end"); return Task.CompletedTask; }

        public Task CompleteAsync(ChatStopReason stopReason, ChatUsage usage, CancellationToken cancellationToken)
        {
            Events.Add($"done:{stopReason}:{usage.InputTokens}/{usage.OutputTokens}/{usage.CacheReadTokens}");
            return Task.CompletedTask;
        }
    }
}
