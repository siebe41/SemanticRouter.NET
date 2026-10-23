using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SemanticRouter.Tests.Support;

/// <summary>
/// An HTTP proxy that never forwards anything. It records the request line of each connection
/// (<c>CONNECT host:443 HTTP/1.1</c>, or <c>POST http://host/path HTTP/1.1</c> for plain HTTP) and where it was going.
/// Without a reply it answers nothing; with one, a plain request gets the reply's status and JSON body and a
/// <c>CONNECT</c> gets 502, so the client fails at once instead of waiting.
/// </summary>
internal sealed class RecordingProxy : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly Task _accept;
    private readonly Func<string, Uri, (HttpStatusCode Status, string Json)>? _reply;

    /// <param name="reply">Answers a plain HTTP request (its method and absolute URL); <see langword="null"/> answers nothing.</param>
    public RecordingProxy(Func<string, Uri, (HttpStatusCode Status, string Json)>? reply = null)
    {
        _reply = reply;
        _listener.Start();
        _accept = AcceptAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The URL to put in <c>HTTPS_PROXY</c> and <c>HTTP_PROXY</c>.</summary>
    public string Url => $"http://127.0.0.1:{Port}";

    /// <summary>The first line of each connection, after the time it arrived.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>Where each request was going: the URL of a plain request, or <c>https://host:port/</c> for a <c>CONNECT</c>.</summary>
    public ConcurrentQueue<Uri> Destinations { get; } = new();

    /// <summary>
    /// The connections that are not in <see cref="Destinations"/>: their first line (TLS sent straight away, say), or
    /// <c>(connected without a request line)</c> when nothing came within 5 seconds.
    /// </summary>
    public ConcurrentQueue<string> WithoutDestination { get; } = new();

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }

    /// <summary>Stops accepting and waits until every accepted connection has been recorded.</summary>
    public async Task DrainAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _accept;
        await Task.WhenAll(_connections);
    }

    /// <summary>The destination of a request line, or <see langword="null"/> when it names none.</summary>
    internal static Uri? DestinationOf(string requestLine)
    {
        string[] parts = requestLine.Split(' ');
        if (parts.Length < 2)
        {
            return null;
        }

        string target = string.Equals(parts[0], "CONNECT", StringComparison.OrdinalIgnoreCase) ? $"https://{parts[1]}/" : parts[1];
        return Uri.TryCreate(target, UriKind.Absolute, out Uri? destination) ? destination : null;
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _connections.Add(RecordAsync(client, DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture)));
        }
    }

    private async Task RecordAsync(TcpClient client, string at)
    {
        using (client)
        {
            string? line = null;
            bool addressed = false;
            try
            {
                NetworkStream stream = client.GetStream();
                // Latin-1 reads one character per byte, so Content-Length counts characters too.
                using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                line = await reader.ReadLineAsync(timeout.Token);
                if (line is not null && DestinationOf(line) is { } destination)
                {
                    Destinations.Enqueue(destination);
                    addressed = true;
                }

                if (_reply is not null && line is not null)
                {
                    await AnswerAsync(stream, reader, line, timeout.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // The connection is what counts.
            }

            line ??= "(connected without a request line)";
            if (!addressed)
            {
                WithoutDestination.Enqueue(line);
            }

            Requests.Enqueue($"{at} {line}");
        }
    }

    private async Task AnswerAsync(NetworkStream stream, StreamReader reader, string line, CancellationToken cancellationToken)
    {
        long length = 0;
        bool chunked = false;
        for (string? header = await reader.ReadLineAsync(cancellationToken); !string.IsNullOrEmpty(header); header = await reader.ReadLineAsync(cancellationToken))
        {
            int colon = header.IndexOf(':', StringComparison.Ordinal);
            string name = colon < 0 ? header : header[..colon].Trim();
            string value = colon < 0 ? string.Empty : header[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                length = long.Parse(value, CultureInfo.InvariantCulture);
            }
            else if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) && value.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                chunked = true;
            }
        }

        // Read the body, so closing the connection does not reset it before the client reads the answer.
        if (chunked)
        {
            for (string? size = await reader.ReadLineAsync(cancellationToken); size is not null; size = await reader.ReadLineAsync(cancellationToken))
            {
                int chunk = int.Parse(size.Split(';')[0].Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                await SkipAsync(reader, chunk + 2, cancellationToken);
                if (chunk == 0)
                {
                    break;
                }
            }
        }
        else
        {
            await SkipAsync(reader, length, cancellationToken);
        }

        string[] parts = line.Split(' ');
        (HttpStatusCode status, string json) = string.Equals(parts[0], "CONNECT", StringComparison.OrdinalIgnoreCase) || DestinationOf(line) is not { } destination
            ? (HttpStatusCode.BadGateway, string.Empty)
            : _reply!(parts[0], destination);
        byte[] body = Encoding.UTF8.GetBytes(json);
        string head = string.Create(CultureInfo.InvariantCulture,
            $"HTTP/1.1 {(int)status} {status}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task SkipAsync(StreamReader reader, long count, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        while (count > 0)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), cancellationToken);
            if (read == 0)
            {
                return;
            }

            count -= read;
        }
    }
}
