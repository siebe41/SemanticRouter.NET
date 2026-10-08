using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace SemanticRouter.Desktop;

/// <summary>
/// One desktop app per configuration directory. The first one listens on a pipe only this user can open; a second
/// launch hands it a command (<c>show</c>, <c>quit</c>) and exits. On Linux and macOS the pipe is a Unix socket.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    public const string Show = "show";
    public const string Quit = "quit";

    /// <summary>Asks nothing; only finds out whether an app is running.</summary>
    public const string Ping = "ping";

    private const string Acknowledged = "ok";

    private readonly string _pipeName;
    private readonly CancellationTokenSource _stopping = new();

    public SingleInstance(string configurationDirectory) => _pipeName = PipeName(configurationDirectory);

    /// <summary>A short name (Unix socket paths are limited to about 100 characters) unique to the directory.</summary>
    internal static string PipeName(string configurationDirectory)
    {
        string normalized = OperatingSystem.IsWindows() ? configurationDirectory.ToUpperInvariant() : configurationDirectory;
        return "semantic-router-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant();
    }

    /// <summary>
    /// Hands <paramref name="command"/> to a running app and waits for it to acknowledge; <see langword="false"/> when
    /// none is running. Without the acknowledgement a Unix socket can accept a connection nobody reads.
    /// </summary>
    public async Task<bool> TrySendAsync(string command, TimeSpan timeout)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
            using var deadline = new CancellationTokenSource(timeout);
            await client.ConnectAsync(deadline.Token).ConfigureAwait(false);
            await client.WriteAsync(Encoding.UTF8.GetBytes(command + "\n"), deadline.Token).ConfigureAwait(false);
            await client.FlushAsync(deadline.Token).ConfigureAwait(false);
            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            return await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false) == Acknowledged;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Accepts commands from later launches until disposed, calling <paramref name="received"/> for each.</summary>
    public void Listen(Action<string> received) => _ = Task.Run(() => ListenAsync(received));

    private NamedPipeServerStream CreateServer() => new(
        _pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
        PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);

    private async Task ListenAsync(Action<string> received)
    {
        NamedPipeServerStream next = CreateServer();
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                NamedPipeServerStream server = next;
                await server.WaitForConnectionAsync(_stopping.Token).ConfigureAwait(false);

                // Listen again before this connection is closed, so there is never a moment without a listener.
                next = CreateServer();
                await using (server)
                {
                    await HandleAsync(server, received).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Quitting.
        }
        finally
        {
            await next.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream server, Action<string> received)
    {
        try
        {
            using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
            string? command = await reader.ReadLineAsync(_stopping.Token).ConfigureAwait(false);
            await server.WriteAsync(Encoding.UTF8.GetBytes(Acknowledged + "\n"), _stopping.Token).ConfigureAwait(false);
            await server.FlushAsync(_stopping.Token).ConfigureAwait(false);
            if (command is Show or Quit)
            {
                received(command);
            }
        }
        catch (IOException)
        {
            // A client that went away mid-message.
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _stopping.Dispose();
    }
}
