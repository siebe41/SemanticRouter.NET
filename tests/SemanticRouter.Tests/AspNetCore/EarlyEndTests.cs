using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
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

    private Task<HttpClient> StartDispatchAsync(
        TimeSpan? stall = null, bool withCli = false, Action<IServiceCollection>? services = null, Action<IApplicationBuilder>? beforeRouter = null) =>
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

                services?.Invoke(s);
            },
            beforeRouter: beforeRouter,
            withCli: withCli);

    /// <summary>The outcome of every entry the activity log hands on (once when added, once when complete), as it was then.</summary>
    private List<string> WatchActivity()
    {
        var outcomes = new List<string>();
        Services.GetRequiredService<RoutingActivityLog>().Updated += e =>
        {
            lock (outcomes)
            {
                outcomes.Add(e.Outcome);
            }
        };
        return outcomes;
    }

    private bool Available(string model) =>
        Services.GetRequiredService<IModelHealth>().IsAvailable(Services.GetRequiredService<RouterRuntimeProvider>().Current.Router.Models.Single(m => m.Name == model));

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

    [Fact]
    public async Task A_connector_that_fails_unexpectedly_mid_reply_ends_it_with_an_error_and_keeps_the_entry()
    {
        LocalHttp = Streams(Ending.Crash, Chunk);
        HttpClient client = await StartDispatchAsync();
        List<string> outcomes = WatchActivity();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(StreamJson));
        string body = await response.Content.ReadAsStringAsync();

        body.Should().StartWith(Chunk).And.Contain("local stopped mid-reply: the connector failed (InvalidOperationException: the stub broke)");
        RoutingActivityEntry entry = Entry(response);
        (entry.Outcome, entry.ServedBy).Should().Be(("failed", "local"));
        entry.TotalMilliseconds.Should().NotBeNull();
        outcomes.Should().Contain("failed", "the finished entry was handed to the store (#390)");
        Available("local").Should().BeTrue("the fault is the router's, not the model's");
        _logs.All.Should().Contain("failed unexpectedly");
    }

    [Fact]
    public async Task A_connector_that_fails_unexpectedly_before_any_output_fails_over_without_a_cooldown()
    {
        LocalHttp = Streams(Ending.Crash);
        HttpClient client = await StartDispatchAsync();

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(StreamJson));

        (await response.Content.ReadAsStringAsync()).Should().Be("""{"from":"cloud"}""");
        response.Headers.GetValues("X-Prompt-Route-Attempt").Should().Equal("2");
        Entry(response).Detail.Should().Contain("local: the connector failed (InvalidOperationException: the stub broke)");
        Available("local").Should().BeTrue();
    }

    [Fact]
    public async Task The_entry_is_kept_when_the_client_goes_away_before_the_final_error_is_written()
    {
        // Every model fails; the client goes away while the last one is being tried, so the 503 cannot be sent to it.
        using var gone = new CancellationTokenSource();
        LocalHttp = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        CloudHttp = new StubHttpMessageHandler((_, _) =>
        {
            gone.Cancel();
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });
        HttpClient client = await StartDispatchAsync(beforeRouter: app => app.Use((context, next) =>
        {
            context.RequestAborted = gone.Token;
            return next(context);
        }));
        List<string> outcomes = WatchActivity();

        HttpResponseMessage response = await client.PostAsync("/v1/messages",
            Json("""{"model":"auto","max_tokens":10,"messages":[{"role":"user","content":"hello there"}]}"""));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "the error is written regardless; a client that is really gone just does not read it");
        RoutingActivityEntry entry = Entry(response);
        entry.Outcome.Should().Be("failed");
        entry.TotalMilliseconds.Should().NotBeNull();
        outcomes.Should().Contain("failed", "the finished entry was handed to the store (#390)");
        _logs.All.Should().NotContainAny("unhandled", "Unhandled");
    }

    [Fact]
    public async Task An_exception_out_of_the_dispatcher_still_finishes_and_stores_the_entry()
    {
        LocalHttp = StubHttpMessageHandler.Json("""{"choices":[{"message":{"role":"assistant","content":"Hi"}}],"usage":{"prompt_tokens":3,"completion_tokens":2}}""");
        HttpClient client = await StartDispatchAsync(services: s => s.AddSingleton<IUsageLedger>(new BrokenLedger()));
        List<string> outcomes = WatchActivity();

        try
        {
            HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));
            _ = await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            // The exception reaches the host, which deals with the request as it does with any other.
        }

        RoutingActivityEntry entry = Services.GetRequiredService<RoutingActivityLog>().Recent().Single();
        entry.Outcome.Should().Be("failed");
        entry.Detail.Should().Contain("the router failed (InvalidOperationException)");
        entry.TotalMilliseconds.Should().NotBeNull();
        outcomes.Should().Contain("failed", "the finished entry was handed to the store (#390)");
    }

    /// <summary>A usage ledger whose store has failed.</summary>
    private sealed class BrokenLedger : IUsageLedger
    {
        public void Record(UsageRecord record) => throw new InvalidOperationException("the ledger is broken");

        public UsageTotals Total(DateTimeOffset since, DateTimeOffset until, string? model = null, string? connector = null, string? profile = null) =>
            new InMemoryUsageLedger().Total(since, until, model, connector, profile);

        public IReadOnlyList<UsageGroup> Summarize(DateTimeOffset since, DateTimeOffset until, UsageGrouping by, TimeZoneInfo? zone = null) => [];
    }

    private enum Ending
    {
        Stall,
        Drop,
        Crash,
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

            if (ending == Ending.Crash)
            {
                throw new InvalidOperationException("the stub broke");
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
