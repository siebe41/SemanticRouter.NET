using System.Buffers;
using Microsoft.AspNetCore.Http;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Reads a request body into memory up to a limit (<see cref="PromptRouterMiddlewareOptions.MaxRequestBodyBytes"/>),
/// whether or not it declares its length: a chunked body is counted as it arrives, so it is never buffered past the
/// limit, whatever the server's own request-body limit is.
/// </summary>
internal static class BoundedRequestBody
{
    private const int ReadChunkSize = 16 * 1024;

    /// <summary>The body, or <see langword="null"/> when it is longer than <paramref name="limit"/> bytes.</summary>
    public static async Task<ReadOnlyMemory<byte>?> ReadAsync(HttpRequest request, long limit, CancellationToken cancellationToken)
    {
        if (request.ContentLength > limit)
        {
            return null;
        }

        int initialCapacity = (int)Math.Min(request.ContentLength ?? ReadChunkSize, limit);
        var buffer = new ArrayBufferWriter<byte>(Math.Max(initialCapacity, 256));
        while (true)
        {
            Memory<byte> chunk = buffer.GetMemory(ReadChunkSize);
            int read = await request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer.WrittenMemory;
            }

            buffer.Advance(read);
            if (buffer.WrittenCount > limit)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The limit for an endpoint outside the routing middleware: the running configuration's, or the default when the
    /// app runs without the router's runtime.
    /// </summary>
    public static long LimitFor(HttpContext context) =>
        context.RequestServices.GetService(typeof(RouterRuntimeProvider)) is RouterRuntimeProvider runtime
            ? runtime.Current.Configuration.Middleware.MaxRequestBodyBytes
            : new PromptRouterMiddlewareOptions().MaxRequestBodyBytes;

    /// <summary>The 413 detail for a body over <paramref name="limit"/>.</summary>
    public static string TooLarge(long limit) =>
        $"The request is larger than {limit} bytes (Middleware:MaxRequestBodyBytes).";
}
