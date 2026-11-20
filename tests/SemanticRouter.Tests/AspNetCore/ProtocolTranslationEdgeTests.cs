using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Edges of protocol translation: parallel tool calls a server numbers alike, streamed refusals, images inside tool
/// results, replies cut off during a tool call, stop sequences, and an activity entry read while its request runs.
/// </summary>
public sealed class ProtocolTranslationEdgeTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static HttpResponseMessage Upstream(string body, string mediaType) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private static (DefaultHttpContext Context, MemoryStream Body) NewContext()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        return (context, body);
    }

    private static string[] Events(MemoryStream body) =>
        [.. Encoding.UTF8.GetString(body.ToArray()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Split('\n').Single(l => l.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..])];

    [Fact]
    public async Task Parallel_calls_a_server_sends_all_at_index_0_stay_separate_calls()
    {
        const string sse = """
            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_a","type":"function","function":{"name":"Read","arguments":""}}]}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"path\":\"a\"}"}}]}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_b","type":"function","function":{"name":"Read","arguments":"{\"path\":"}}]}}]}

            data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_b","function":{"arguments":"\"b\"}"}}]}}]}

            data: {"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}

            data: [DONE]

            """;
        var writer = new RecordingWriter();

        await OpenAIResponseReader.RelayAsync(Upstream(sse, "text/event-stream"), writer, CancellationToken.None);

        writer.Events.Should().Equal(
            "tool:call_a:Read", "args:{\"path\":\"a\"}", "end",
            "tool:call_b:Read", "args:{\"path\":", "args:\"b\"}", "end",
            "done:ToolUse");
    }

    [Fact]
    public async Task A_streamed_responses_refusal_reaches_the_client()
    {
        const string sse = """
            event: response.refusal.delta
            data: {"type":"response.refusal.delta","item_id":"msg_1","output_index":0,"content_index":0,"delta":"I can't help "}

            event: response.refusal.delta
            data: {"type":"response.refusal.delta","item_id":"msg_1","output_index":0,"content_index":0,"delta":"with that."}

            event: response.completed
            data: {"type":"response.completed","response":{"status":"completed","output":[],"usage":{"input_tokens":5,"output_tokens":4}}}

            """;
        var writer = new RecordingWriter();

        await ResponsesResponseReader.RelayAsync(Upstream(sse, "text/event-stream"), writer, CancellationToken.None);

        writer.Events.Should().Equal("text:I can't help ", "text:with that.", "done:EndTurn");
    }

    [Theory]
    [InlineData(ChatProtocol.Anthropic, """
        {"model":"auto","max_tokens":100,"messages":[
          {"role":"user","content":"look"},
          {"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"screenshot","input":{}}]},
          {"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":[{"type":"text","text":"done"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AAAA"}}]}]}]}
        """)]
    [InlineData(ChatProtocol.OpenAI, """
        {"model":"auto","messages":[
          {"role":"user","content":"look"},
          {"role":"assistant","content":null,"tool_calls":[{"id":"t1","type":"function","function":{"name":"screenshot","arguments":"{}"}}]},
          {"role":"tool","tool_call_id":"t1","content":[{"type":"text","text":"done"},{"type":"image_url","image_url":{"url":"data:image/png;base64,AAAA"}}]}]}
        """)]
    [InlineData(ChatProtocol.Responses, """
        {"model":"auto","input":[
          {"role":"user","content":"look"},
          {"type":"function_call","call_id":"t1","name":"screenshot","arguments":"{}"},
          {"type":"function_call_output","call_id":"t1","output":[{"type":"input_text","text":"done"},{"type":"input_image","image_url":"data:image/png;base64,AAAA"}]}]}
        """)]
    public void Images_inside_tool_results_are_reported_as_dropped_when_translated(ChatProtocol client, string body)
    {
        ChatCompletionRequest request = client switch
        {
            ChatProtocol.Anthropic => ChatCompletionRequest.ParseAnthropic(Json(body)),
            ChatProtocol.Responses => ChatCompletionRequest.ParseResponses(Json(body)),
            _ => ChatCompletionRequest.Parse(Json(body)),
        };

        request.HasImages.Should().BeTrue();
        request.ProtocolOnlyFeatures.Should().Contain(DroppedFeatures.ToolResultImages);
        foreach (ChatProtocol target in Enum.GetValues<ChatProtocol>().Where(p => p != client))
        {
            DroppedFeatures.ForTranslation(request, target, reasoning: false).Should().Contain(DroppedFeatures.ToolResultImages, $"{client} → {target}");
        }

        DroppedFeatures.ForTranslation(request, client, reasoning: false).Should().BeEmpty("a pass-through body keeps them");
        DroppedFeatures.ForCli(request).Should().Contain(DroppedFeatures.ToolResultImages);
    }

    [Fact]
    public void A_tool_result_without_images_reports_nothing()
    {
        ChatCompletionRequest request = ChatCompletionRequest.Parse(Json("""
            {"model":"auto","messages":[{"role":"user","content":[{"type":"text","text":"see"},{"type":"image_url","image_url":{"url":"https://example.com/a.png"}}]},
              {"role":"assistant","content":null,"tool_calls":[{"id":"t1","type":"function","function":{"name":"f","arguments":"{}"}}]},
              {"role":"tool","tool_call_id":"t1","content":"ok"}]}
            """));

        request.HasImages.Should().BeTrue("the user's own image is carried as an image block");
        request.ProtocolOnlyFeatures.Should().NotContain(DroppedFeatures.ToolResultImages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_reply_cut_off_in_a_tool_call_keeps_length_for_openai(bool stream)
    {
        (DefaultHttpContext context, MemoryStream body) = NewContext();
        var writer = new OpenAIResponseWriter(context, "m", stream);
        await writer.StartToolCallAsync("call_1", "Write", CancellationToken.None);
        await writer.WriteToolCallArgumentsAsync("{\"content\":\"half", CancellationToken.None);
        await writer.EndToolCallAsync(CancellationToken.None);
        await writer.CompleteAsync(ChatStopReason.MaxTokens, default, CancellationToken.None);

        JsonElement last = stream ? Json(Events(body)[^2]) : Json(Encoding.UTF8.GetString(body.ToArray()));
        last.GetProperty("choices")[0].GetProperty("finish_reason").GetString().Should().Be("length", "the arguments may be incomplete JSON");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_reply_cut_off_in_a_tool_call_keeps_max_tokens_for_anthropic(bool stream)
    {
        (DefaultHttpContext context, MemoryStream body) = NewContext();
        var writer = new AnthropicResponseWriter(context, "m", stream);
        await writer.StartToolCallAsync("toolu_1", "Write", CancellationToken.None);
        await writer.WriteToolCallArgumentsAsync("{\"content\":\"half", CancellationToken.None);
        await writer.EndToolCallAsync(CancellationToken.None);
        await writer.CompleteAsync(ChatStopReason.MaxTokens, default, CancellationToken.None);

        string stop = stream
            ? Events(body).Select(Json).Single(e => e.GetProperty("type").GetString() == "message_delta").GetProperty("delta").GetProperty("stop_reason").GetString()!
            : Json(Encoding.UTF8.GetString(body.ToArray())).GetProperty("stop_reason").GetString()!;
        stop.Should().Be("max_tokens");
    }

    [Fact]
    public async Task A_responses_reply_cut_off_in_a_tool_call_ends_with_max_tokens()
    {
        var writer = new RecordingWriter();

        await ResponsesResponseReader.RelayAsync(Upstream("""
            {"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},
             "output":[{"type":"function_call","call_id":"c1","name":"Write","arguments":"{\"content\":\"half"}]}
            """, "application/json"), writer, CancellationToken.None);

        writer.Events[^1].Should().Be("done:MaxTokens");
    }

    [Theory]
    [InlineData(false, """{"choices":[{"message":{"role":"assistant","content":"one two"},"finish_reason":"stop","stop_reason":"THREE"}]}""")]
    [InlineData(true, "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"one two\"}}]}\n\ndata: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\",\"matched_stop\":\"THREE\"}]}\n\ndata: [DONE]\n\n")]
    public async Task The_stop_sequence_a_server_names_reaches_an_anthropic_client(bool stream, string reply)
    {
        (DefaultHttpContext context, MemoryStream body) = NewContext();
        IChatResponseWriter writer = new UsageRecordingWriter(new AnthropicResponseWriter(context, "m", stream), new UsageCapture());

        await OpenAIResponseReader.RelayAsync(Upstream(reply, stream ? "text/event-stream" : "application/json"), writer, CancellationToken.None);

        JsonElement end = stream
            ? Events(body).Select(Json).Single(e => e.GetProperty("type").GetString() == "message_delta").GetProperty("delta")
            : Json(Encoding.UTF8.GetString(body.ToArray()));
        end.GetProperty("stop_reason").GetString().Should().Be("stop_sequence");
        end.GetProperty("stop_sequence").GetString().Should().Be("THREE");
    }

    [Fact]
    public async Task Without_a_named_sequence_an_anthropic_reply_has_a_null_stop_sequence()
    {
        (DefaultHttpContext context, MemoryStream body) = NewContext();

        await OpenAIResponseReader.RelayAsync(
            Upstream("""{"choices":[{"message":{"role":"assistant","content":"hi"},"finish_reason":"stop","stop_reason":32007}]}""", "application/json"),
            new AnthropicResponseWriter(context, "m", stream: false),
            CancellationToken.None);

        JsonElement json = Json(Encoding.UTF8.GetString(body.ToArray()));
        json.GetProperty("stop_reason").GetString().Should().Be("end_turn", "a token id is not a sequence the client gave");
        json.GetProperty("stop_sequence").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task An_activity_entry_can_be_serialised_while_the_dispatcher_adds_attempts()
    {
        string[] models = ["local", "cloud", "claude-cli"];
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var current = new RoutingActivityEntry { Id = 0 };
        current.SentTo.Add("local");
        _ = JsonSerializer.Serialize(current, options); // warm up, so reading overlaps the adding below
        using var stop = new CancellationTokenSource();

        // Many short-lived entries, each read while attempts are added to it: a plain list fails within a few rounds.
        Task adding = Task.Run(() =>
        {
            for (int round = 1; round <= 1000 && !stop.IsCancellationRequested; round++)
            {
                var entry = new RoutingActivityEntry { Id = round };
                Volatile.Write(ref current, entry);
                for (int i = 0; i < 100; i++)
                {
                    entry.SentTo.Add(models[i % models.Length]);
                    Thread.SpinWait(20);
                }
            }
        });

        Action serialise = () =>
        {
            while (!adding.IsCompleted)
            {
                RoutingActivityEntry entry = Volatile.Read(ref current);
                _ = JsonSerializer.Serialize(entry, options);
                _ = entry.SentTo.Distinct(StringComparer.OrdinalIgnoreCase).Count();
            }
        };

        try
        {
            serialise.Should().NotThrow();
        }
        finally
        {
            await stop.CancelAsync();
            await adding;
        }

        RoutingActivityEntry last = Volatile.Read(ref current);
        RoutingActivityEntry copy = JsonSerializer.Deserialize<RoutingActivityEntry>(JsonSerializer.Serialize(last, options), options)!;
        copy.SentTo.Should().Equal(last.SentTo).And.HaveCount(100);
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
            Events.Add($"done:{stopReason}");
            return Task.CompletedTask;
        }
    }
}
