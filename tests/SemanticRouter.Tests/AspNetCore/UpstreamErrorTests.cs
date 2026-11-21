using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// A backend that answers 200 and then reports an error instead of (the rest of) an answer (#389): an error event in the
/// stream, an error body, a CLI that fails after it began to answer. None of these is a finished answer.
/// </summary>
public sealed class UpstreamErrorTests : RouterHostTest
{
    private const string StreamJson = """{"model":"auto","stream":true,"messages":[{"role":"user","content":"hello there"}]}""";
    private const string AnthropicStreamJson = """{"model":"auto","max_tokens":10,"stream":true,"messages":[{"role":"user","content":"hello there"}]}""";
    private const string Chunk = """data: {"id":"1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"Hel"}}]}""" + "\n\n";
    private const string Overloaded = """data: {"error":{"message":"The server is overloaded","type":"server_error"}}""" + "\n\n";
    private const string RateLimited = """data: {"error":{"message":"Rate limit reached for requests","type":"requests","code":"rate_limit_exceeded"}}""" + "\n\n";

    private static StubHttpMessageHandler Streams(string body) =>
        new((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") });

    private Task<HttpClient> StartDispatchAsync(bool withCli = false) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
                o.PathPrefixes.Add("/v1/messages");
            },
            withCli: withCli);

    private RoutingActivityEntry Entry(HttpResponseMessage response) =>
        Services.GetRequiredService<RoutingActivityLog>().Recent().Single(e => e.Id == long.Parse(response.Headers.GetValues("X-Prompt-Route-Id").Single(), System.Globalization.CultureInfo.InvariantCulture));

    private bool Available(string model) =>
        Services.GetRequiredService<IModelHealth>().IsAvailable(Services.GetRequiredService<RouterRuntimeProvider>().Current.Router.Models.Single(m => m.Name == model));

    [Theory]
    [InlineData("/v1/chat/completions", StreamJson)] // passed through
    [InlineData("/v1/messages", AnthropicStreamJson)] // translated
    public async Task An_error_event_before_any_output_fails_over_to_the_next_model(string path, string request)
    {
        LocalHttp = Streams(Overloaded + "data: [DONE]\n\n");
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync(path, Json(request));
        string body = await response.Content.ReadAsStringAsync();

        response.Headers.GetValues("X-Prompt-Route-Attempt").Should().Equal(["2"], "nothing had reached the client, so the next model answered");
        response.Headers.GetValues("X-Prompt-Route-Model").Should().Equal("cloud");
        body.Should().NotContain("overloaded");
        RoutingActivityEntry entry = Entry(response);
        (entry.Outcome, entry.ServedBy).Should().Be(("served", "cloud"));
        entry.Detail.Should().Contain("local: error event (server_error: The server is overloaded)");
        Available("local").Should().BeFalse("the model that failed cools down");
    }

    [Fact]
    public async Task An_error_body_in_place_of_a_json_reply_fails_over()
    {
        LocalHttp = StubHttpMessageHandler.Json("""{"error":{"message":"model not loaded","type":"server_error"}}""");
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/messages", Json("""{"model":"auto","max_tokens":10,"messages":[{"role":"user","content":"hello there"}]}"""));

        response.Headers.GetValues("X-Prompt-Route-Attempt").Should().Equal("2");
        Entry(response).Detail.Should().Contain("model not loaded");
    }

    [Fact]
    public async Task A_passed_through_error_event_mid_stream_fails_the_request_and_cools_the_model_down()
    {
        LocalHttp = Streams(Chunk + Overloaded);
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(StreamJson));
        string body = await response.Content.ReadAsStringAsync();

        body.Should().Be(Chunk + Overloaded, "the client has the backend's own error event, and gets no second one");
        RoutingActivityEntry entry = Entry(response);
        (entry.Outcome, entry.ServedBy).Should().Be(("failed", "local"));
        entry.Detail.Should().Contain("local: stopped mid-reply: error event (server_error: The server is overloaded)");
        entry.TotalMilliseconds.Should().NotBeNull();

        HttpResponseMessage next = await client.PostAsync("/v1/chat/completions", Json(ChatJson));
        next.Headers.GetValues("X-Prompt-Route-Model").Should().Equal(["cloud"], "the model that failed mid-reply is cooling down");
    }

    [Fact]
    public async Task A_translated_error_event_mid_stream_ends_the_reply_with_an_error_in_the_clients_protocol()
    {
        LocalHttp = Streams(Chunk + Overloaded);
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/messages", Json(AnthropicStreamJson));
        string body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("\"Hel\"").And.NotContain("message_stop", "the reply did not finish");
        body.Should().Contain("event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"api_error\",\"message\":\"local stopped mid-reply: error event (server_error: The server is overloaded)");
        Entry(response).Outcome.Should().Be("failed");
        Available("local").Should().BeFalse();
    }

    [Fact]
    public async Task A_rate_limit_error_event_cools_the_model_down_as_a_quota()
    {
        LocalHttp = Streams(Chunk + RateLimited);
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/messages", Json(AnthropicStreamJson));
        _ = await response.Content.ReadAsStringAsync();

        Services.GetRequiredService<IModelHealth>().GetCooldowns()["local"].Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(5), "a quota cools down for QuotaCooldown, not FailureCooldown");
    }

    [Fact]
    public async Task A_cli_that_fails_after_it_began_to_answer_ends_the_reply_with_an_error()
    {
        Cli = new FakeCliProcessRunner(_ => new FakeCliProcess([ClaudeStream.Init, ClaudeStream.Delta("Hi")], exitCode: 1, stderr: "segmentation fault"));
        HttpClient client = await StartDispatchAsync(withCli: true);

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions",
            Json("""{"model":"claude-cli","stream":true,"messages":[{"role":"user","content":"hello there"}]}"""));
        string body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("Hi").And.Contain("claude-cli stopped mid-reply: exit code 1").And.NotContain("\"finish_reason\":\"stop\"");
        RoutingActivityEntry entry = Entry(response);
        (entry.Outcome, entry.ServedBy).Should().Be(("failed", "claude-cli"));
        Available("claude-cli").Should().BeFalse("a CLI that crashed mid-reply cools down");
    }

    [Fact]
    public async Task A_cli_that_hits_its_limit_mid_reply_cools_down_as_a_quota()
    {
        Cli = new FakeCliProcessRunner(_ => new FakeCliProcess(
            [ClaudeStream.Init, ClaudeStream.Delta("Hi"), ClaudeStream.Error("Claude AI usage limit reached")], exitCode: 1));
        HttpClient client = await StartDispatchAsync(withCli: true);

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions",
            Json("""{"model":"claude-cli","stream":true,"messages":[{"role":"user","content":"hello there"}]}"""));
        string body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("claude-cli stopped mid-reply: exit code 1");
        Services.GetRequiredService<IModelHealth>().GetCooldowns()["claude-cli"].Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(5));
    }

    [Fact]
    public async Task A_cli_that_fails_after_output_on_a_reply_that_is_not_streamed_fails_over()
    {
        Cli = new FakeCliProcessRunner(_ => new FakeCliProcess([ClaudeStream.Init, ClaudeStream.Delta("Hi")], exitCode: 1, stderr: "segmentation fault"));
        HttpClient client = await StartDispatchAsync(withCli: true);

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions",
            Json("""{"model":"claude-cli","messages":[{"role":"user","content":"hello there"}]}"""));
        string body = await response.Content.ReadAsStringAsync();

        // The buffered part of the reply never reached the client: the attempt failed like one without output, and the
        // client was not handed a cut-off answer as a whole one.
        RoutingActivityEntry entry = Entry(response);
        entry.Detail.Should().Contain("claude-cli: exit code 1");
        (entry.Outcome, entry.ServedBy).Should().Be(("failed", null));
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "the request named the CLI model, so no other model was a candidate");
        body.Should().NotContain("\"Hi\"");
        Available("claude-cli").Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", ChatProtocol.Anthropic, ModelFailureKind.Unavailable, "overloaded_error: Overloaded")]
    [InlineData("""{"type":"error","error":{"type":"rate_limit_error","message":"Too many requests"}}""", ChatProtocol.Anthropic, ModelFailureKind.QuotaExceeded, "rate_limit_error")]
    [InlineData("""{"error":{"message":"busy","type":"server_error"}}""", ChatProtocol.OpenAI, ModelFailureKind.Unavailable, "server_error: busy")]
    [InlineData("""{"error":{"message":"You exceeded your current quota","type":"insufficient_quota","code":"insufficient_quota"}}""", ChatProtocol.OpenAI, ModelFailureKind.QuotaExceeded, "insufficient_quota")]
    [InlineData("""{"error":"backend crashed"}""", ChatProtocol.OpenAI, ModelFailureKind.Unavailable, "backend crashed")]
    [InlineData("""{"type":"error","code":"server_error","message":"boom","param":null}""", ChatProtocol.Responses, ModelFailureKind.Unavailable, "server_error: boom")]
    [InlineData("""{"type":"response.failed","response":{"status":"failed","error":{"code":"rate_limit_exceeded","message":"slow down"}}}""", ChatProtocol.Responses, ModelFailureKind.QuotaExceeded, "rate_limit_exceeded: slow down")]
    [InlineData("""{"object":"response","status":"failed","error":{"code":"server_error","message":"x"},"output":[]}""", ChatProtocol.Responses, ModelFailureKind.Unavailable, "server_error: x")]
    public void Error_events_are_recognised_in_each_protocol(string json, ChatProtocol protocol, ModelFailureKind kind, string detail)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        UpstreamReplyException.TryFind(document.RootElement, protocol, out UpstreamReplyException? error).Should().BeTrue();

        error!.Failure.FailureKind.Should().Be(kind);
        error.Failure.Detail.Should().Contain(detail);
    }

    [Theory]
    [InlineData("""{"choices":[],"usage":{"prompt_tokens":1}}""", ChatProtocol.OpenAI)]
    [InlineData("""{"choices":[{"delta":{"content":"x"}}],"error":null}""", ChatProtocol.OpenAI)]
    [InlineData("""{"type":"message_delta","delta":{"stop_reason":"end_turn"}}""", ChatProtocol.Anthropic)]
    [InlineData("""{"type":"response.completed","response":{"status":"completed","error":null}}""", ChatProtocol.Responses)]
    [InlineData("""{"object":"response","status":"completed","error":null,"output":[]}""", ChatProtocol.Responses)]
    public void Answers_are_not_taken_for_errors(string json, ChatProtocol protocol)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);

        UpstreamReplyException.TryFind(document.RootElement, protocol, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(ChatProtocol.Anthropic, "event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"usage\":{\"input_tokens\":3}}}\n\nevent: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Hi\"}}\n\nevent: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}\n\n")]
    [InlineData(ChatProtocol.OpenAI, "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hi\"}}]}\n\ndata: {\"error\":{\"message\":\"Overloaded\",\"type\":\"server_error\"}}\n\n")]
    [InlineData(ChatProtocol.Responses, "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"Hi\"}\n\nevent: response.failed\ndata: {\"type\":\"response.failed\",\"response\":{\"status\":\"failed\",\"error\":{\"code\":\"server_error\",\"message\":\"Overloaded\"}}}\n\n")]
    [InlineData(ChatProtocol.Responses, "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"Hi\"}\n\nevent: error\ndata: {\"type\":\"error\",\"code\":\"server_error\",\"message\":\"Overloaded\"}\n\n")]
    public async Task Readers_do_not_finish_a_reply_that_ends_in_an_error_event(ChatProtocol protocol, string sse)
    {
        var writer = new RecordingWriter();
        using var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };

        Func<Task> relay = () => protocol switch
        {
            ChatProtocol.Anthropic => AnthropicResponseReader.RelayAsync(response, writer, CancellationToken.None),
            ChatProtocol.Responses => ResponsesResponseReader.RelayAsync(response, writer, CancellationToken.None),
            _ => OpenAIResponseReader.RelayAsync(response, writer, CancellationToken.None),
        };

        (await relay.Should().ThrowAsync<UpstreamReplyException>()).Which.Message.Should().Contain("Overloaded");
        writer.Events.Should().Equal(["text:Hi"], "what came before the error was relayed, and the reply was not completed");
    }

    [Fact]
    public async Task A_passed_through_stream_that_opens_with_an_error_sends_nothing()
    {
        var downstream = new MemoryStream();
        const string sse = "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}\n\n";

        Func<Task> copy = () => PassThroughToolCallScanner.CopyAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(sse)), downstream, sse: true, ChatProtocol.Anthropic, _ => { }, CancellationToken.None, _ => { });

        (await copy.Should().ThrowAsync<UpstreamReplyException>()).Which.ClientNotified.Should().BeFalse();
        downstream.Length.Should().Be(0, "the next model can still answer");
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

        public Task CompleteAsync(ChatStopReason stopReason, ChatUsage usage, CancellationToken cancellationToken) { Events.Add($"done:{stopReason}"); return Task.CompletedTask; }
    }
}
