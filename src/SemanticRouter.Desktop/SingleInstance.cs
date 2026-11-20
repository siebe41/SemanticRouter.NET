using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace SemanticRouter.Desktop;

/// <summary>
/// One desktop app per configuration directory. The app holds an exclusive lock on <see cref="LockFileName"/> while it
/// runs and listens on a pipe only this user can open; a second launch finds the lock taken, hands the app a command
/// (<c>show</c>, <c>quit</c>) and exits. On Linux and macOS the pipe is a Unix socket.
/// </summary>
/// <remarks>
/// The lock is what decides: two launches at the same moment cannot both take it, where both could find nobody
/// listening on the pipe yet. The operating system releases it when the process ends, however it ends.
/// </remarks>
internal sealed class SingleInstance : IDisposable
{
    /// <summary>The lock file, in the local data directory (the configuration directory, except on Windows by default).</summary>
    public const string LockFileName = "desktop.lock";

    public const string Show = "show";
    public const string Quit = "quit";

    /// <summary>Asks nothing; only finds out whether an app is running.</summary>
    public const string Ping = "ping";

    private const string Acknowledged = "ok";

    // How long the listener waits before listening again after the pipe failed, so a lasting failure does not spin.
    private static readonly TimeSpan RelistenDelay = TimeSpan.FromMilliseconds(250);

    private readonly string _pipeName;
    private readonly string _lockFile;
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationToken _stopped;
    private FileStream? _lock;
    private int _disposed;

    /// <summary>For the configuration directory <paramref name="configurationDirectory"/>, with the lock file in it.</summary>
    public SingleInstance(string configurationDirectory)
        : this(configurationDirectory, configurationDirectory)
    {
    }

    /// <summary>For <paramref name="configurationDirectory"/>, with the lock file in <paramref name="lockDirectory"/>.</summary>
    public SingleInstance(string configurationDirectory, string lockDirectory)
    {
        _pipeName = PipeName(configurationDirectory);
        _lockFile = Path.Combine(lockDirectory, LockFileName);
        _stopped = _stopping.Token;
    }

    /// <summary>What <see cref="ClaimAsync"/> found.</summary>
    public enum Claim
    {
        /// <summary>No app was running: this one is the app now, and holds the lock until disposed.</summary>
        Primary,

        /// <summary>An app is running and took the command.</summary>
        HandedOver,

        /// <summary>An app holds the lock but did not answer in time (it is starting very slowly, or quitting).</summary>
        NoAnswer,
    }

    /// <summary>
    /// Becomes the app for the directory, or hands <paramref name="command"/> to the one that is. An app that holds the
    /// lock but does not listen yet (it is starting) or any more (it is quitting) is waited for, up to
    /// <paramref name="patience"/>: until it answers, or until it is gone and this launch can take the lock.
    /// </summary>
    public async Task<Claim> ClaimAsync(string command, TimeSpan patience)
    {
        long started = Environment.TickCount64;
        while (true)
        {
            if (TryLock())
            {
                return Claim.Primary;
            }

            // A ping first, repeated until the app listens: a command it took but acknowledged too late would arrive
            // twice if it were sent again.
            if (await TrySendAsync(Ping, TimeSpan.FromSeconds(1)).ConfigureAwait(false))
            {
                return command == Ping || await TrySendAsync(command, TimeSpan.FromSeconds(5)).ConfigureAwait(false) ? Claim.HandedOver : Claim.NoAnswer;
            }

            if (TimeSpan.FromMilliseconds(Environment.TickCount64 - started) >= patience)
            {
                return Claim.NoAnswer;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Takes the lock that makes this process the app for the directory; <see langword="false"/> when another process
    /// holds it. Taken once, it is held until <see cref="Dispose"/> or the process ends.
    /// </summary>
    public bool TryLock()
    {
        if (_lock is not null)
        {
            return true;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_lockFile)!);

            // FileShare.None: a sharing violation on Windows, an exclusive flock on Linux and macOS.
            _lock = new FileStream(_lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // The directory cannot be written, so nothing can be locked there: carry on as before the lock existed (the
            // pipe check already found no app). The router itself will report the directory.
            return true;
        }
    }

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
        NamedPipeServerStream? next = null;
        try
        {
            while (!_stopped.IsCancellationRequested)
            {
                NamedPipeServerStream? server = next;
                next = null;
                try
                {
                    server ??= CreateServer();
                    await server.WaitForConnectionAsync(_stopped).ConfigureAwait(false);

                    // Listen again before this connection is closed, so there is never a moment without a listener.
                    next = CreateServer();
                    await HandleAsync(server, received).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A pipe that broke or could not be made: listen again, or later launches would each start an app
                    // that finds the lock taken and nobody answering.
                    await Task.Delay(RelistenDelay, _stopped).ConfigureAwait(false);
                }
                finally
                {
                    if (server is not null)
                    {
                        await server.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Quitting.
        }
        finally
        {
            if (next is not null)
            {
                await next.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task HandleAsync(NamedPipeServerStream server, Action<string> received)
    {
        try
        {
            using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
            string? command = await reader.ReadLineAsync(_stopped).ConfigureAwait(false);
            await server.WriteAsync(Encoding.UTF8.GetBytes(Acknowledged + "\n"), _stopped).ConfigureAwait(false);
            await server.FlushAsync(_stopped).ConfigureAwait(false);
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

    /// <summary>Stops listening and releases the lock. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopping.Cancel();
        _stopping.Dispose();
        _lock?.Dispose();
        _lock = null;
    }
}
