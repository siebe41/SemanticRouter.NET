using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// Replies that end early (#230, #231): a backend that stalls or drops the connection, and a client that goes away. Each
/// is recorded, a backend that broke off is cooled down, and the client learns what happened.
/// </summary>
public sealed class EarlyEndTests : RouterHostTest
{
    private const string StreamJson = """{"model":"auto","stream":true,"messages":[{"role":"user","content":"hello there"}]}""";
    private const string Chunk = """data: {"id":"1","object":"chat.completion.chunk","choices":[{"index":0,"delta":{"content":"Hel"}}]}""" + "\n\n";

    private readonly CapturingLoggerProvider _logs = new();

    private Task<HttpClient> StartDispatchAsync(TimeSpan? stall = null, bool withCli = false) =>
        StartAsync(
            o =>
            {
                o.Mode = PromptRouterMode.Dispatch;
                o.PathPrefixes.Add("/v1/chat/completions");
                o.PathPrefixes.Add("/v1/messages");
            },
            services: s =>
            {
                s.AddLogging(b => b.AddProvider(_logs));
                if (stall is { } limit)
                {
                    s.PostConfigure<SemanticRouterConnectorsOptions>(c =>
                    {
                        foreach (ConnectorOptions connector in c.Connectors.Values)
                        {
                            connector.StallTimeout = limit;
                        }
                    });
                }
            },
            withCli: withCli);

    private static StubHttpMessageHandler Streams(Ending ending, params string[] chunks) =>
        new((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new ScriptedBody(chunks, ending)) { Headers = { ContentType = new("text/event-stream") } },
        });

    private RoutingActivityEntry Entry(HttpResponseMessage response) =>
        Services.GetRequiredService<RoutingActivityLog>().Recent().Single(e => e.Id == long.Parse(response.Headers.GetValues("X-Prompt-Route-Id").Single(), System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public async Task A_backend_that_stalls_before_any_output_fails_over()
    {
        LocalHttp = Streams(Ending.Stall);
        HttpClient client = await StartDispatchAsync(stall: TimeSpan.FromMilliseconds(300));

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(StreamJson));

        (await response.Content.ReadAsStringAsync()).Should().Be("""{"from":"cloud"}""");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json", "nothing of the stalled reply is left on the response");
        response.Headers.GetValues("X-Prompt-Route-Attempt").Should().Equal("2");
        Entry(response).Detail.Should().Contain("local: stalled: no output for 300 ms");
    }

    [Fact]
    public async Task A_backend_that_stalls_mid_stream_ends_the_reply_with_an_error_and_cools_down()
    {
        LocalHttp = Streams(Ending.Stall, Chunk);
        HttpClient client = await StartDispatchAsync(stall: TimeSpan.FromMilliseconds(300));

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(StreamJson));
        string body = await response.Content.ReadAsStringAsync();

        body.Should().StartWith(Chunk).And.Contain("""data: {"error":{"message":"local stopped mid-reply: stalled""").And.Contain("server_error");
        RoutingActivityEntry entry = Entry(response);
        (entry.Outcome, entry.ServedBy).Should().Be(("failed", "local"));
        entry.TotalMilliseconds.Should().NotBeNull();

        HttpResponseMessage next = await client.PostAsync("/v1/chat/completions", Json(ChatJson));
        (await next.Content.ReadAsStringAsync()).Should().Be("""{"from":"cloud"}""");
        next.Headers.GetValues("X-Prompt-Route-Attempt").Should().Equal(["1"], "the model that stalled is cooling down");
    }

    [Fact]
    public async Task A_backend_that_drops_the_connection_mid_stream_is_recorded_without_an_unhandled_exception()
    {
        LocalHttp = Streams(Ending.Drop, Chunk);
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(StreamJson));
        string body = await response.Content.ReadAsStringAsync();

        body.Should().StartWith(Chunk).And.Contain("local stopped mid-reply: connection dropped");
        RoutingActivityEntry entry = Entry(response);
        (entry.Outcome, entry.ServedBy).Should().Be(("failed", "local"));
        entry.InputTokens.Should().BePositive("the estimated input is recorded when the backend reported nothing");
        Services.GetRequiredService<IUsageLedger>().Total(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)).Requests.Should().Be(1);
        _logs.All.Should().NotContainAny("unhandled", "Unhandled").And.Contain("stopped mid-reply");
    }

    [Fact]
    public async Task An_anthropic_client_gets_an_error_event_when_a_translated_reply_breaks_off()
    {
        LocalHttp = Streams(Ending.Drop, Chunk);
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/messages",
            Json("""{"model":"auto","max_tokens":10,"stream":true,"messages":[{"role":"user","content":"hello there"}]}"""));
        string body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("event: content_block_delta").And.Contain("\"Hel\"");
        body.Should().Contain("""event: error""" + "\n" + """data: {"type":"error","error":{"type":"api_error","message":"local stopped mid-reply: connection dropped""");
        Entry(response).Outcome.Should().Be("failed");
    }

    [Fact]
    public async Task A_cli_that_goes_quiet_mid_reply_is_stopped_and_cooled_down()
    {
        var process = new FakeCliProcess([ClaudeStream.Init, ClaudeStream.Delta("Hi")], after: new TaskCompletionSource().Task);
        Cli = new FakeCliProcessRunner(_ => process);
        HttpClient client = await StartDispatchAsync(stall: TimeSpan.FromMilliseconds(300), withCli: true);

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions",
            Json("""{"model":"claude-cli","stream":true,"messages":[{"role":"user","content":"hello there"}]}"""));
        string body = await response.Content.ReadAsStringAsync();

        body.Should().Contain("Hi").And.Contain("claude-cli stopped mid-reply: stalled: no output for 300 ms");
        RoutingActivityEntry entry = Entry(response);
        (entry.Outcome, entry.ServedBy).Should().Be(("failed", "claude-cli"));
        process.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task A_client_that_goes_away_mid_stream_leaves_a_cancelled_entry_and_a_usage_row()
    {
        LocalHttp = Streams(Ending.Stall, Chunk);
        HttpClient client = await StartDispatchAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") { Content = Json(StreamJson) };
        using var gone = new CancellationTokenSource();
        HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, gone.Token);
        Stream body = await response.Content.ReadAsStreamAsync(gone.Token);
        byte[] first = new byte[Encoding.UTF8.GetByteCount(Chunk)];
        await body.ReadExactlyAsync(first, gone.Token);
        long id = long.Parse(response.Headers.GetValues("X-Prompt-Route-Id").Single(), System.Globalization.CultureInfo.InvariantCulture);
        await gone.CancelAsync();
        response.Dispose();

        var activity = Services.GetRequiredService<RoutingActivityLog>();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (activity.Recent().Single(e => e.Id == id).TotalMilliseconds is null && System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(10);
        }

        RoutingActivityEntry entry = activity.Recent().Single(e => e.Id == id);
        (entry.Outcome, entry.ServedBy).Should().Be(("cancelled", "local"));
        Services.GetRequiredService<IUsageLedger>().Total(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1)).Requests.Should().Be(1);
        Services.GetRequiredService<IModelHealth>().IsAvailable(Services.GetRequiredService<RouterRuntimeProvider>().Current.Router.Models.Single(m => m.Name == "local"))
            .Should().BeTrue("the client went away; the model did nothing wrong");
    }

    private enum Ending
    {
        Stall,
        Drop,
    }

    /// <summary>A reply body that sends its chunks, then stalls until cancelled or drops the connection.</summary>
    private sealed class ScriptedBody(string[] chunks, Ending ending) : Stream
    {
        private int _next;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_next < chunks.Length)
            {
                byte[] chunk = Encoding.UTF8.GetBytes(chunks[_next++]);
                chunk.CopyTo(buffer);
                return chunk.Length;
            }

            if (ending == Ending.Drop)
            {
                throw new IOException("Connection reset by peer");
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
