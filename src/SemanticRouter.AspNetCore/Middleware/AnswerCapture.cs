namespace SemanticRouter.AspNetCore;

/// <summary>
/// Passes the response through and keeps a copy of its first <see cref="Limit"/> bytes, so a sampled answer can be
/// graded after the client has it (<see cref="AnswerJudge"/>).
/// </summary>
internal sealed class AnswerCapture(Stream inner, int limit) : Stream
{
    private readonly MemoryStream _copy = new();

    /// <summary>The most bytes kept.</summary>
    public int Limit { get; } = limit;

    /// <summary>The response went on past <see cref="Limit"/>.</summary>
    public bool Truncated { get; private set; }

    /// <summary>The bytes kept.</summary>
    public ReadOnlySpan<byte> Captured => _copy.GetBuffer().AsSpan(0, (int)_copy.Length);

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        Keep(buffer.AsSpan(offset, count));
        inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Keep(buffer);
        inner.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Keep(buffer.AsSpan(offset, count));
        return inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Keep(buffer.Span);
        return inner.WriteAsync(buffer, cancellationToken);
    }

    private void Keep(ReadOnlySpan<byte> bytes)
    {
        int room = Limit - (int)_copy.Length;
        if (bytes.Length > room)
        {
            Truncated = true;
        }

        if (room > 0)
        {
            _copy.Write(bytes[..Math.Min(room, bytes.Length)]);
        }
    }

    protected override void Dispose(bool disposing)
    {
        // The response stream belongs to the server; only the copy is ours.
        if (disposing)
        {
            _copy.Dispose();
        }

        base.Dispose(disposing);
    }
}
