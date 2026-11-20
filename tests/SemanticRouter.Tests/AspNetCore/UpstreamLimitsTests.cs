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
    public async Task A_pass_through_json_reply_reaches_the_client_before_it_has_all_arrived()
    {
        var firstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new TwoPartStream(Encoding.UTF8.GetBytes("""{"id":"x","choices":"""), Encoding.UTF8.GetBytes("[]}"), firstWrite.Task);
        var downstream = new NotifyingStream(firstWrite);

        await PassThroughToolCallScanner.CopyAsync(upstream, downstream, sse: false, ChatProtocol.OpenAI, _ => { }, CancellationToken.None);

        Encoding.UTF8.GetString(downstream.ToArray()).Should().Be("""{"id":"x","choices":[]}""");
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
    private sealed class TwoPartStream(byte[] first, byte[] second, Task gate) : Stream
    {
        private int _part;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            switch (_part++)
            {
                case 0:
                    first.CopyTo(buffer);
                    return first.Length;
                case 1:
                    await gate.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                    second.CopyTo(buffer);
                    return second.Length;
                default:
                    return 0;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A memory stream that says when something was first written to it.</summary>
    private sealed class NotifyingStream(TaskCompletionSource written) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ValueTask write = base.WriteAsync(buffer, cancellationToken);
            written.TrySetResult();
            return write;
        }
    }
}
