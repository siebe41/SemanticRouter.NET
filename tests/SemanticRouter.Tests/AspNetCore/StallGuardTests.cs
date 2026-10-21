using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>
/// The stall timeout on a backend's reply: a read that waits too long is cancelled and called a stall; time between
/// reads, and a cancelled request, are not.
/// </summary>
public sealed class StallGuardTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task A_read_that_waits_past_the_limit_is_cancelled_and_called_a_stall()
    {
        using var guard = new StallGuard(Short, CancellationToken.None);
        using HttpContent content = guard.Guard(new StreamContent(new Backend()));
        await using Stream body = await content.ReadAsStreamAsync();

        Func<Task> read = () => body.ReadAsync(new byte[16]).AsTask();

        await read.Should().ThrowAsync<OperationCanceledException>();
        guard.Stalled.Should().BeTrue();
        guard.Token.IsCancellationRequested.Should().BeTrue();
        guard.Describe().Should().Be("stalled: no output for 100 ms");
    }

    [Fact]
    public async Task The_array_overload_is_guarded_too()
    {
        using var guard = new StallGuard(Short, CancellationToken.None);
        using HttpContent content = guard.Guard(new StreamContent(new Backend()));
        await using Stream body = await content.ReadAsStreamAsync();

        Func<Task> read = () => body.ReadAsync(new byte[16], 0, 16, CancellationToken.None);

        await read.Should().ThrowAsync<OperationCanceledException>();
        guard.Stalled.Should().BeTrue();
    }

    [Fact]
    public async Task Time_the_client_takes_between_reads_does_not_count()
    {
        var backend = new Backend();
        backend.Send("one ");
        backend.Send("two");
        backend.End();
        using var guard = new StallGuard(Short, CancellationToken.None);
        using HttpContent content = guard.Guard(new StreamContent(backend));
        await using Stream body = await content.ReadAsStreamAsync();
        byte[] buffer = new byte[16];

        int first = await body.ReadAsync(buffer);
        await Task.Delay(Short * 4);
        int second = await body.ReadAsync(buffer.AsMemory(first));
        int end = await body.ReadAsync(buffer.AsMemory(first + second));

        Encoding.UTF8.GetString(buffer, 0, first + second).Should().Be("one two");
        end.Should().Be(0);
        guard.Stalled.Should().BeFalse("the timer runs only while a read waits");
        guard.Token.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task A_cancelled_request_is_not_a_stall()
    {
        using var request = new CancellationTokenSource();
        using var guard = new StallGuard(TimeSpan.FromMinutes(5), request.Token);
        using HttpContent content = guard.Guard(new StreamContent(new Backend()));
        await using Stream body = await content.ReadAsStreamAsync();

        Task<int> read = body.ReadAsync(new byte[16]).AsTask();
        await request.CancelAsync();

        await FluentActions.Awaiting(() => read).Should().ThrowAsync<OperationCanceledException>();
        guard.Token.IsCancellationRequested.Should().BeTrue();
        guard.Stalled.Should().BeFalse();
    }

    [Fact]
    public async Task The_guarded_content_keeps_the_headers_and_copies_the_whole_reply()
    {
        var backend = new Backend();
        backend.Send("data: 1\n\n");
        backend.Send("data: 2\n\n");
        backend.End();
        var inner = new StreamContent(backend);
        inner.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        using var guard = new StallGuard(TimeSpan.FromSeconds(30), CancellationToken.None);
        using HttpContent content = guard.Guard(inner);

        string text = await content.ReadAsStringAsync();

        content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        text.Should().Be("data: 1\n\ndata: 2\n\n");
    }

    [Fact]
    public async Task A_synchronous_read_passes_through_and_the_stream_is_read_only()
    {
        var backend = new Backend();
        backend.Send("abc");
        using var guard = new StallGuard(Short, CancellationToken.None);
        using HttpContent content = guard.Guard(new StreamContent(backend));
        Stream body = await content.ReadAsStreamAsync();
        byte[] buffer = new byte[8];

        int read = body.Read(buffer, 0, buffer.Length);

        Encoding.UTF8.GetString(buffer, 0, read).Should().Be("abc");
        (body.CanRead, body.CanSeek, body.CanWrite).Should().Be((true, false, false));
        FluentActions.Invoking(() => body.Length).Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => body.Position).Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => body.Position = 1).Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => body.Seek(0, SeekOrigin.Begin)).Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => body.SetLength(1)).Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => body.Write(buffer, 0, 1)).Should().Throw<NotSupportedException>();
        body.Flush();

        body.Dispose();
        backend.Disposed.Should().BeTrue("disposing the guarded stream disposes the backend's");
    }

    [Theory]
    [InlineData(300, "300 ms")]
    [InlineData(1000, "1 s")]
    [InlineData(100_000, "100 s")]
    public void Durations_read_in_seconds_or_milliseconds_under_one(int milliseconds, string expected)
    {
        StallGuard.Duration(TimeSpan.FromMilliseconds(milliseconds)).Should().Be(expected);
    }

    /// <summary>A backend reply that arrives when the test sends it, and waits (cancellably) until then.</summary>
    private sealed class Backend : Stream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();

        public bool Disposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public void Send(string text) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

        public void End() => _chunks.Writer.Complete();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken))
            {
                return 0;
            }

            byte[] chunk = await _chunks.Reader.ReadAsync(cancellationToken);
            chunk.CopyTo(buffer);
            return chunk.Length;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (!_chunks.Reader.TryRead(out byte[]? chunk))
            {
                return 0;
            }

            chunk.CopyTo(buffer, offset);
            return chunk.Length;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
