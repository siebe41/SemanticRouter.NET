using System.Net;
using System.Text;
using SemanticRouter.AspNetCore;
using SemanticRouter.Tests.Support;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// A broken or hostile backend cannot make the router hold its reply in memory without end (#402): error bodies, event
/// stream lines, non-streamed replies and a CLI's standard error are each read up to a limit.
/// </summary>
public sealed class UpstreamLimitsTests : RouterHostTest
{
    [Fact]
    public async Task An_error_body_is_read_up_to_its_limit_only()
    {
        var endless = new EndlessStream("""{"error":{"message":"bad request"}}""");
        using var content = new StreamContent(endless);

        string body = await UpstreamLimits.ReadErrorBodyAsync(content, CancellationToken.None);

        body.Length.Should().Be(UpstreamLimits.ErrorBodyBytes);
        body.Should().StartWith("""{"error":{"message":"bad request"}}""");
        endless.BytesRead.Should().BeLessThan(UpstreamLimits.ErrorBodyBytes + 64 * 1024);
    }

    [Fact]
    public async Task A_429_with_an_endless_body_still_gives_its_reset_time()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StreamContent(new EndlessStream("""{"error":{"message":"Rate limit reached. Please try again in 20s."}}""")),
        };

        DateTimeOffset? reset = await OpenAICompatibleConnector.ReadResetAsync(response, CancellationToken.None);

        reset.Should().BeCloseTo(DateTimeOffset.UtcNow.AddSeconds(20), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_backend_error_with_an_endless_body_is_relayed_cut_short()
    {
        LocalHttp = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StreamContent(new EndlessStream("""{"error":{"message":"no"}}""")),
        });
        HttpClient client = await StartAsync(o =>
        {
            o.Mode = PromptRouterMode.Dispatch;
            o.PathPrefixes.Add("/v1/chat/completions");
        });

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Length.Should().Be(UpstreamLimits.ErrorBodyBytes);
    }

    [Fact]
    public async Task A_429_with_an_endless_body_fails_over_to_the_next_model()
    {
        LocalHttp = new StubHttpMessageHandler((_, _) => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StreamContent(new EndlessStream("slow down ")),
        });
        HttpClient client = await StartAsync(o =>
        {
            o.Mode = PromptRouterMode.Dispatch;
            o.PathPrefixes.Add("/v1/chat/completions");
        });

        HttpResponseMessage response = await client.PostAsync("/v1/chat/completions", Json(ChatJson));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("cloud");
    }

    [Fact]
    public async Task An_event_stream_line_without_end_fails_instead_of_filling_memory()
    {
        var endless = new EndlessStream("data: ");
        Func<Task> read = async () =>
        {
            await foreach (string _ in ServerSentEvents.ReadAsync(endless, CancellationToken.None))
            {
            }
        };

        await read.Should().ThrowAsync<UpstreamLimitException>();
        endless.BytesRead.Should().BeLessThan(UpstreamLimits.MaxLineLength + (1024 * 1024));
    }

    [Fact]
    public async Task An_event_made_of_many_data_lines_is_bounded_too()
    {
        var endless = new EndlessStream("data: " + new string('x', 1000) + "\n", repeat: true);
        Func<Task> read = async () =>
        {
            await foreach (string _ in ServerSentEvents.ReadAsync(endless, CancellationToken.None))
            {
            }
        };

        await read.Should().ThrowAsync<UpstreamLimitException>().WithMessage("an event*");
    }

    [Theory]
    [InlineData("a\nb\r\nc\rd")]
    [InlineData("a\n\n\r\n\r\rb\n")]
    [InlineData("\r\n")]
    [InlineData("")]
    [InlineData("no line break")]
    public async Task Lines_are_split_as_a_text_reader_splits_them(string text)
    {
        // Also across the reader's buffer: a "\r\n" whose halves arrive in different reads.
        foreach (string input in new[] { text, new string('y', 8191) + "\r\n" + text })
        {
            var expected = new List<string>();
            using (var reader = new StringReader(input))
            {
                while (reader.ReadLine() is { } line)
                {
                    expected.Add(line);
                }
            }

            var actual = new List<string>();
            await foreach (string line in ServerSentEvents.ReadLinesAsync(new StringReader(input), 1_000_000, CancellationToken.None))
            {
                actual.Add(line);
            }

            actual.Should().Equal(expected);
        }
    }

    [Fact]
    public async Task A_translated_reply_is_read_up_to_its_limit_only()
    {
        Stream limited = UpstreamLimits.Limit(new MemoryStream(new byte[100]), 99);
        Func<Task> read = () => limited.CopyToAsync(Stream.Null);
        await read.Should().ThrowAsync<UpstreamLimitException>();

        Stream exact = UpstreamLimits.Limit(new MemoryStream(new byte[100]), 100);
        await exact.CopyToAsync(Stream.Null);
    }

    [Fact]
    public async Task A_pass_through_json_reply_too_large_to_hold_reaches_the_client_before_it_has_all_arrived()
    {
        // A JSON reply is held until it is complete, so that an error in place of the answer can go to the next model,
        // but only up to MaxReplyBytes: past that, what is held goes to the client and the rest follows as it arrives.
        var firstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new LargeThenGatedStream(UpstreamLimits.MaxReplyBytes + 1, firstWrite.Task);
        var downstream = new CountingStream(firstWrite);

        await PassThroughToolCallScanner.CopyAsync(upstream, downstream, sse: false, ChatProtocol.OpenAI, _ => { }, CancellationToken.None);

        downstream.Count.Should().Be(UpstreamLimits.MaxReplyBytes + 1 + LargeThenGatedStream.Last.Length, "the whole reply reaches the client");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(16 * 1024)]
    public async Task A_pass_through_event_stream_reaches_the_client_byte_for_byte_however_it_is_split(int readSize)
    {
        // "\r\n" line breaks, split across reads at every point: each line is relayed with the break it came with.
        const string toolCall = "event: chunk\r\ndata: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"id\":\"call_1\"}]}}]}\r\n\r\n";
        const string usage = "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":4}}\r\n\r\n";
        const string done = "data: [DONE]\r\n\r\n";
        byte[] body = Encoding.UTF8.GetBytes(toolCall + usage + done);

        foreach (bool strip in new[] { false, true })
        {
            var downstream = new MemoryStream();
            var calls = new List<string>();
            int usages = 0;

            await PassThroughToolCallScanner.CopyAsync(
                new SlowStream(body, readSize), downstream, sse: true, ChatProtocol.OpenAI, calls.Add, CancellationToken.None, _ => usages++, stripUsageChunk: strip);

            Encoding.UTF8.GetString(downstream.ToArray()).Should().Be(strip ? toolCall + done : toolCall + usage + done, "the usage chunk the router asked for is left out, the rest is unchanged");
            calls.Should().Equal("call_1");
            usages.Should().Be(1);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16 * 1024)]
    public async Task A_pass_through_event_stream_that_opens_with_an_error_sends_nothing_however_it_is_split(int readSize)
    {
        byte[] body = Encoding.UTF8.GetBytes("event: error\r\ndata: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}\r\n\r\n");
        var downstream = new MemoryStream();

        Func<Task> copy = () => PassThroughToolCallScanner.CopyAsync(
            new SlowStream(body, readSize), downstream, sse: true, ChatProtocol.Anthropic, _ => { }, CancellationToken.None, _ => { });

        (await copy.Should().ThrowAsync<UpstreamReplyException>()).Which.ClientNotified.Should().BeFalse();
        downstream.Length.Should().Be(0, "the next model can still answer");
    }

    [Fact]
    public async Task A_pass_through_event_line_too_long_to_hold_releases_what_was_held()
    {
        // The event name before the first data line is held back; a data line too long to hold sends it on, unscanned.
        string huge = "event: chunk\ndata: {\"x\":\"" + new string('a', UpstreamLimits.MaxLineLength) + "\"}\n\n";
        byte[] body = Encoding.UTF8.GetBytes(huge + "data: [DONE]\n\n");
        var downstream = new MemoryStream();

        await PassThroughToolCallScanner.CopyAsync(new MemoryStream(body), downstream, sse: true, ChatProtocol.OpenAI, _ => { }, CancellationToken.None);

        downstream.ToArray().Should().Equal(body);
    }

    [Fact]
    public async Task A_pass_through_event_line_too_long_to_scan_still_reaches_the_client_and_the_next_lines_are_scanned()
    {
        string huge = "data: {\"x\":\"" + new string('a', UpstreamLimits.MaxLineLength) + "\"}\r\n\r\n";
        string toolCall = """data: {"choices":[{"delta":{"tool_calls":[{"id":"call_after"}]}}]}""" + "\n\n";
        byte[] body = Encoding.UTF8.GetBytes(huge + toolCall);
        var downstream = new MemoryStream();
        var calls = new List<string>();

        await PassThroughToolCallScanner.CopyAsync(new MemoryStream(body), downstream, sse: true, ChatProtocol.OpenAI, calls.Add, CancellationToken.None);

        downstream.Length.Should().Be(body.Length, "the bytes pass through unchanged");
        calls.Should().Equal("call_after");
    }

    [Fact]
    public void Standard_error_keeps_its_start_and_end()
    {
        var text = new BoundedText(100);
        text.Append("first line\n");
        for (int i = 0; i < 1000; i++)
        {
            text.Append(new string('x', 37));
        }

        text.Append("\nlast line");

        string kept = text.ToString();
        kept.Should().StartWith("first line\n").And.EndWith("\nlast line").And.Contain("characters left out");
        kept.Length.Should().BeLessThan(200);

        var small = new BoundedText(100);
        small.Append("short");
        small.Append(" and sweet");
        small.ToString().Should().Be("short and sweet");
    }

    /// <summary>
    /// A body that never ends: a prefix, then the same character forever (or the prefix over and over, with
    /// <paramref name="repeat"/>). Counts what was read.
    /// </summary>
    private sealed class EndlessStream(string prefix, bool repeat = false) : Stream
    {
        private readonly byte[] _prefix = Encoding.UTF8.GetBytes(prefix);

        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                long at = BytesRead + i;
                buffer[offset + i] = repeat ? _prefix[at % _prefix.Length] : at < _prefix.Length ? _prefix[at] : (byte)'z';
            }

            BytesRead += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Gives its first part, then waits for <paramref name="gate"/> before giving the second.</summary>
    /// <summary>Returns <c>body</c> at most <c>readSize</c> bytes per read.</summary>
    private sealed class SlowStream(byte[] body, int readSize) : MemoryStream(body)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, readSize)], cancellationToken);
    }

    /// <summary>
    /// <c>size</c> bytes of spaces, then, once <c>gate</c> completes, <see cref="Last"/>: a reply whose end arrives only
    /// after the client has been sent its start. Generated as it is read, so the test holds none of it.
    /// </summary>
    private sealed class LargeThenGatedStream(long size, Task gate) : Stream
    {
        public static readonly byte[] Last = "{}"u8.ToArray();

        private long _sent;
        private bool _done;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sent < size)
            {
                int count = (int)Math.Min(buffer.Length, size - _sent);
                buffer.Span[..count].Fill((byte)' ');
                _sent += count;
                return count;
            }

            if (_done)
            {
                return 0;
            }

            await gate.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            Last.CopyTo(buffer);
            _done = true;
            return Last.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Counts what is written to it, without keeping it, and completes <c>written</c> on the first write.</summary>
    private sealed class CountingStream(TaskCompletionSource written) : Stream
    {
        public long Count { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => Count;

        public override long Position { get => Count; set => throw new NotSupportedException(); }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Count += buffer.Length;
            written.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Count += count;
            written.TrySetResult();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
