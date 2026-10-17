using System.Net;
using System.Net.Http.Headers;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Cancels a reply that stops arriving (<see cref="ConnectorOptions.StallTimeout"/>). The timer runs only while a read
/// waits for the backend, and starts again with every read, so a long reply that keeps coming is never cut, and time
/// the client takes to accept what was sent does not count.
/// </summary>
internal sealed class StallGuard : IDisposable
{
    private readonly CancellationTokenSource _source;
    private readonly CancellationToken _request;

    public StallGuard(TimeSpan limit, CancellationToken request)
    {
        Limit = limit;
        _request = request;
        _source = CancellationTokenSource.CreateLinkedTokenSource(request);
    }

    /// <summary>How long a read may wait.</summary>
    public TimeSpan Limit { get; }

    /// <summary>Cancelled when a read waited too long, or when the request is.</summary>
    public CancellationToken Token => _source.Token;

    /// <summary>A read waited too long (the request itself was not cancelled).</summary>
    public bool Stalled => _source.IsCancellationRequested && !_request.IsCancellationRequested;

    /// <summary>What to say when <see cref="Stalled"/>.</summary>
    public string Describe() => "stalled: no output for " + Duration(Limit);

    /// <summary><c>100 s</c>, or <c>300 ms</c> under a second.</summary>
    public static string Duration(TimeSpan limit) => limit < TimeSpan.FromSeconds(1)
        ? limit.TotalMilliseconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " ms"
        : limit.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " s";

    /// <summary>Wraps a reply's content so every read of its body is guarded.</summary>
    public HttpContent Guard(HttpContent content) => new GuardedContent(content, this);

    public void Dispose() => _source.Dispose();

    private async ValueTask<int> ReadAsync(Stream inner, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _source.Token);
        _source.CancelAfter(Limit);
        try
        {
            return await inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            if (!_source.IsCancellationRequested)
            {
                _source.CancelAfter(Timeout.InfiniteTimeSpan);
            }
        }
    }

    private sealed class GuardedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly StallGuard _guard;

        public GuardedContent(HttpContent inner, StallGuard guard)
        {
            _inner = inner;
            _guard = guard;
            foreach (KeyValuePair<string, HeaderStringValues> header in inner.Headers.NonValidated)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            new GuardedStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), _guard);

        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            Stream body = await CreateContentReadStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                await body.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
            }
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class GuardedStream(Stream inner, StallGuard guard) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            guard.ReadAsync(inner, buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        // Readers here are asynchronous; a synchronous read cannot be cancelled, so it is not guarded.
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
