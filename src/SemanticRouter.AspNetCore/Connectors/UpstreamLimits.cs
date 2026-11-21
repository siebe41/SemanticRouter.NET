using System.Text;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// How much of a backend's reply the router holds in memory at once, so a broken or hostile backend cannot make it
/// buffer without end. <see cref="StallGuard"/> bounds how long a reply may go quiet; these bound how much it may send.
/// </summary>
internal static class UpstreamLimits
{
    /// <summary>
    /// The most of an error body that is read: 64 KiB. Only its first few thousand characters are ever used (a
    /// message, a reset time); a pass-through client gets at most this much of the backend's error.
    /// </summary>
    public const int ErrorBodyBytes = 64 * 1024;

    /// <summary>
    /// The longest server-sent-events line, and event, that is read whole: 16 Mi characters (bytes, where bytes are
    /// counted). Deltas are small; the largest legitimate lines are the events that carry a whole reply or a generated
    /// image (the Responses API's <c>response.completed</c> and image events, a base64 image being a few MB), so this
    /// leaves room for several such images in one line while bounding what a stream without line breaks can hold.
    /// </summary>
    public const int MaxLineLength = 16 * 1024 * 1024;

    /// <summary>
    /// The largest non-streamed reply that is read whole: to be translated to another protocol, or, passed through, to
    /// be checked for an error before it is sent. 64 MiB, eight times the default request-body limit. A longer
    /// pass-through reply streams to the client as it arrives, unchecked.
    /// </summary>
    public const long MaxReplyBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Reads at most <see cref="ErrorBodyBytes"/> of <paramref name="content"/> as text (in its declared charset, else
    /// UTF-8), instead of <see cref="HttpContent.ReadAsStringAsync()"/>, which buffers the whole body.
    /// </summary>
    public static async Task<string> ReadErrorBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            byte[] buffer = new byte[ErrorBodyBytes];
            int total = 0;
            int read;
            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
            }

            using var reader = new StreamReader(new MemoryStream(buffer, 0, total, writable: false), EncodingOf(content), detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Wraps <paramref name="stream"/> so that reading more than <paramref name="limit"/> bytes from it fails.</summary>
    public static Stream Limit(Stream stream, long limit) => new LimitedReadStream(stream, limit);

    private static Encoding EncodingOf(HttpContent content)
    {
        if (content.Headers.ContentType?.CharSet is { Length: > 0 } charset)
        {
            try
            {
                return Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
                // An unknown charset: read it as UTF-8, as HttpContent does for no charset.
            }
        }

        return Encoding.UTF8;
    }

    /// <summary>A read-only stream that throws <see cref="UpstreamLimitException"/> past a number of bytes.</summary>
    private sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

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

        private int Count(int read)
        {
            _read += read;
            return _read > limit
                ? throw new UpstreamLimitException($"the reply is larger than {limit / (1024 * 1024)} MiB")
                : read;
        }
    }
}

/// <summary>
/// A backend sent more than the router reads whole (<see cref="UpstreamLimits"/>). An <see cref="IOException"/>, so a
/// connector treats it like a dropped connection: the next model answers when nothing reached the client yet.
/// </summary>
internal sealed class UpstreamLimitException(string message) : IOException(message);
