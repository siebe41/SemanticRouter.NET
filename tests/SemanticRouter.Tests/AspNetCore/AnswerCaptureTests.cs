using System.Text;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Tests.AspNetCore;

/// <summary>The copy of a reply kept for the answer judge: everything goes to the client, the first bytes to the judge.</summary>
public sealed class AnswerCaptureTests
{
    [Fact]
    public async Task Every_write_reaches_the_client_and_only_the_first_bytes_are_kept()
    {
        using var client = new CountingStream();
        using var capture = new AnswerCapture(client, limit: 10);

        capture.Write(Encoding.UTF8.GetBytes("--abcd--"), 2, 4);
        capture.Write("efg"u8);
#pragma warning disable CA1835 // the array overload is one of the four ways a response is written
        await capture.WriteAsync(Encoding.UTF8.GetBytes("hij"), 0, 3, CancellationToken.None);
#pragma warning restore CA1835
        await capture.WriteAsync(Encoding.UTF8.GetBytes("klmnop").AsMemory());
        capture.Flush();
        await capture.FlushAsync(CancellationToken.None);

        Encoding.UTF8.GetString(client.ToArray()).Should().Be("abcdefghijklmnop");
        Encoding.UTF8.GetString(capture.Captured).Should().Be("abcdefghij");
        capture.Truncated.Should().BeTrue();
        client.Flushes.Should().Be(2);
    }

    [Fact]
    public void A_reply_of_exactly_the_limit_is_not_truncated()
    {
        using var client = new MemoryStream();
        using var capture = new AnswerCapture(client, limit: 6);

        capture.Write("abc"u8);
        capture.Write("def"u8);
        capture.Write(ReadOnlySpan<byte>.Empty);

        Encoding.UTF8.GetString(capture.Captured).Should().Be("abcdef");
        capture.Truncated.Should().BeFalse();

        capture.Write("g"u8);
        capture.Truncated.Should().BeTrue();
        Encoding.UTF8.GetString(capture.Captured).Should().Be("abcdef");
    }

    [Fact]
    public void It_is_write_only_and_disposing_it_leaves_the_response_stream_open()
    {
        using var client = new MemoryStream();
        var capture = new AnswerCapture(client, limit: 4);

        (capture.CanRead, capture.CanSeek, capture.CanWrite).Should().Be((false, false, true));
        FluentActions.Invoking(() => capture.Length).Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => capture.Position).Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => capture.Position = 0).Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => capture.Read(new byte[1], 0, 1)).Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => capture.Seek(0, SeekOrigin.Begin)).Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => capture.SetLength(0)).Should().Throw<NotSupportedException>();

        capture.Dispose();

        client.CanWrite.Should().BeTrue("the response stream belongs to the server");
    }

    private sealed class CountingStream : MemoryStream
    {
        public int Flushes { get; private set; }

        public override void Flush() => Flushes++;

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Flushes++;
            return Task.CompletedTask;
        }
    }
}
