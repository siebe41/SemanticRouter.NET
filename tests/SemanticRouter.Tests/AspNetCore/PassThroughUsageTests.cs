using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// A streamed chat-completions request passed through to an OpenAI-compatible backend: the router asks for the usage
/// chunk the client may not have, so budgets and the usage ledger see the request, and keeps that chunk from a client
/// that did not ask for it.
/// </summary>
public sealed class PassThroughUsageTests : RouterHostTest
{
    private const string Streamed = """{"model":"local","stream":true,"messages":[{"role":"user","content":"hello there"}]}""";

    private const string UsageChunk = """data: {"id":"c1","object":"chat.completion.chunk","choices":[],"usage":{"prompt_tokens":12,"completion_tokens":3}}""";

    // A server that, like OpenAI's, sends the usage chunk only when asked.
    private static StubHttpMessageHandler Upstream() => new((_, body) =>
    {
        bool asked = JsonNode.Parse(body)?["stream_options"]?["include_usage"]?.GetValue<bool>() == true;
        string usage = asked ? ",\"usage\":null" : string.Empty;
        string sse =
            """data: {"id":"c1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"role":"assistant","content":"Hi"}}]""" + usage + "}\n\n" +
            """data: {"id":"c1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]""" + usage + "}\n\n" +
            (asked ? UsageChunk + "\n\n" : string.Empty) +
            "data: [DONE]\n\n";
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") };
    });

    private Task<HttpClient> StartDispatchAsync() => StartAsync(o =>
    {
        o.Mode = PromptRouterMode.Dispatch;
        o.PathPrefixes.Add("/v1/chat/completions");
    });

    [Fact]
    public async Task A_streamed_request_without_include_usage_is_counted_and_the_client_gets_no_usage_chunk()
    {
        StubHttpMessageHandler upstream = Upstream();
        LocalHttp = upstream;
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(Streamed));
        string relayed = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode sent = JsonNode.Parse(upstream.Requests.Single().Body)!;
        ((bool)sent["stream_options"]!["include_usage"]!).Should().BeTrue("without it the server reports no usage");
        ((string?)sent["model"]).Should().Be("qwen3-8b");
        relayed.Should().NotContain("\"choices\":[]", "the client did not ask for the usage chunk");
        relayed.Should().Contain("\"content\":\"Hi\"").And.EndWith("data: [DONE]\n\n");
        relayed.Should().NotContain("\n\n\n", "the event that is held back leaves no stray blank line");

        RoutingActivityEntry entry = Services.GetRequiredService<RoutingActivityLog>().Recent().Single();
        (entry.InputTokens, entry.OutputTokens).Should().Be((12, 3));
        UsageTotals totals = Services.GetRequiredService<IUsageLedger>().Total(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), model: "local");
        (totals.Requests, totals.OutputTokens).Should().Be((1, 3), "budgets read the ledger");
    }

    [Fact]
    public async Task A_client_that_asked_for_usage_gets_the_usage_chunk()
    {
        StubHttpMessageHandler upstream = Upstream();
        LocalHttp = upstream;
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            Json(Streamed.Replace("\"stream\":true", "\"stream\":true,\"stream_options\":{\"include_usage\":true}", StringComparison.Ordinal)));
        string relayed = await response.Content.ReadAsStringAsync();

        relayed.Should().Contain(UsageChunk);
        Services.GetRequiredService<RoutingActivityLog>().Recent().Single().OutputTokens.Should().Be(3);
    }

    // A strict server: rejects any field it does not know, as some Azure api-versions do with stream_options.
    private static StubHttpMessageHandler Strict(HttpStatusCode rejection) => new((request, body) =>
        JsonNode.Parse(body)!.AsObject().ContainsKey("stream_options")
            ? new HttpResponseMessage(rejection)
            {
                Content = new StringContent("""{"error":{"message":"Unrecognized request argument supplied: stream_options"}}""", Encoding.UTF8, "application/json"),
            }
            : Upstream().Respond(request, body));

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task A_backend_that_rejects_the_added_stream_options_still_answers_and_is_not_asked_again(HttpStatusCode rejection)
    {
        StubHttpMessageHandler upstream = Strict(rejection);
        LocalHttp = upstream;
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage first = await client.PostAsync("/v1/chat/completions", Json(Streamed));
        string relayed = await first.Content.ReadAsStringAsync();

        first.StatusCode.Should().Be(HttpStatusCode.OK, "the request worked before the router asked for usage");
        relayed.Should().Contain("\"content\":\"Hi\"").And.EndWith("data: [DONE]\n\n");
        upstream.Requests.Should().HaveCount(2);
        JsonNode.Parse(upstream.Requests[1].Body)!.AsObject().ContainsKey("stream_options").Should().BeFalse("the retry is the client's request");

        HttpResponseMessage second = await client.PostAsync("/v1/chat/completions", Json(Streamed));
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        upstream.Requests.Should().HaveCount(3, "the connector remembers that the backend does not accept the field");
        JsonNode.Parse(upstream.Requests[2].Body)!.AsObject().ContainsKey("stream_options").Should().BeFalse();
    }

    [Fact]
    public async Task A_rejection_the_field_did_not_cause_is_relayed_and_usage_is_still_asked_for()
    {
        StubHttpMessageHandler upstream = new((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"message":"messages is too long"}}""", Encoding.UTF8, "application/json"),
        });
        LocalHttp = upstream;
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage first = await client.PostAsync("/v1/chat/completions", Json(Streamed));
        first.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await first.Content.ReadAsStringAsync()).Should().Contain("messages is too long");
        upstream.Requests.Should().HaveCount(2, "the request is tried once without the field");

        await client.PostAsync("/v1/chat/completions", Json(Streamed));
        upstream.Requests.Should().HaveCount(4);
        JsonNode.Parse(upstream.Requests[2].Body)!["stream_options"].Should().NotBeNull("the field was not the cause, so it is still sent");
    }

    [Fact]
    public async Task A_client_that_sent_stream_options_itself_gets_the_rejection_without_a_retry()
    {
        StubHttpMessageHandler upstream = Strict(HttpStatusCode.BadRequest);
        LocalHttp = upstream;
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync(
            "/v1/chat/completions",
            Json(Streamed.Replace("\"stream\":true", "\"stream\":true,\"stream_options\":{\"include_usage\":true}", StringComparison.Ordinal)));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        upstream.Requests.Should().ContainSingle();
    }

    [Theory]
    [InlineData("""{"model":"m","stream":true,"messages":[]}""", """{"include_usage":true}""", true)]
    [InlineData("""{"model":"m","stream":true,"stream_options":{"include_usage":false,"include_obfuscation":false},"messages":[]}""", """{"include_usage":true,"include_obfuscation":false}""", true)]
    [InlineData("""{"model":"m","stream":true,"stream_options":null,"messages":[]}""", """{"include_usage":true}""", true)]
    [InlineData("""{"model":"m","stream":true,"stream_options":{"include_usage":true},"messages":[]}""", """{"include_usage":true}""", false)]
    [InlineData("""{"model":"m","messages":[]}""", null, false)]
    [InlineData("""{"model":"m","stream":false,"messages":[]}""", null, false)]
    public void Only_a_streamed_body_gets_include_usage(string body, string? expectedOptions, bool expectedAdded)
    {
        ReadOnlyMemory<byte> rewritten = OpenAICompatibleConnector.RewriteModel(
            Encoding.UTF8.GetBytes(body), "gpt-x", stripRelayedThinking: false, requestUsage: true, out bool added);

        JsonObject json = JsonNode.Parse(Encoding.UTF8.GetString(rewritten.Span))!.AsObject();
        added.Should().Be(expectedAdded);
        ((string?)json["model"]).Should().Be("gpt-x");
        if (expectedOptions is null)
        {
            json.ContainsKey("stream_options").Should().BeFalse();
        }
        else
        {
            JsonNode.DeepEquals(json["stream_options"], JsonNode.Parse(expectedOptions)).Should().BeTrue(json.ToJsonString());
        }
    }

    [Theory]
    [InlineData(UsageChunk, true)]
    [InlineData("""data: {"choices": [], "usage": {"prompt_tokens": 1}}""", true)]
    [InlineData("""data: {"choices":[],"usage":null}""", false)]
    [InlineData("""data: {"choices":[{"index":0,"delta":{"content":"[]"}}],"usage":null}""", false)]
    [InlineData("""data: {"choices":[], broken""", false)]
    [InlineData("data: [DONE]", false)]
    public void The_usage_only_chunk_is_recognised(string line, bool expected) =>
        PassThroughToolCallScanner.IsUsageOnlyChunk(line).Should().Be(expected);

    [Fact]
    public async Task The_scanner_reads_the_usage_chunk_it_holds_back()
    {
        string sse = "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hi\"}}],\"usage\":null}\n\n" + UsageChunk + "\n\ndata: [DONE]\n\n";
        using var upstream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        using var downstream = new MemoryStream();
        var usage = new List<ChatUsage>();

        await PassThroughToolCallScanner.CopyAsync(upstream, downstream, sse: true, ChatProtocol.OpenAI, _ => { }, CancellationToken.None, usage.Add, stripUsageChunk: true);

        Encoding.UTF8.GetString(downstream.ToArray()).Should().Be("data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hi\"}}],\"usage\":null}\n\ndata: [DONE]\n\n");
        usage.Should().ContainSingle().Which.Should().Be(new ChatUsage { InputTokens = 12, OutputTokens = 3 });
    }
}
