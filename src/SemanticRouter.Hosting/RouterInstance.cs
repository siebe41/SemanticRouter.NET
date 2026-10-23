using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Host;

/// <summary>
/// The router running on a configuration directory: written to <c>semantic-router.instance.json</c> once it listens and
/// removed when it stops, so <c>semantic-router service status</c> can say where it answers and Windows can stop it.
/// </summary>
/// <param name="StopEvent">
/// The event that stops this router on Windows (<see cref="NewStopEventName"/>). Records from earlier releases have none:
/// those routers listen only on <see cref="StopEventName"/>.
/// </param>
internal sealed record RouterInstance(int ProcessId, IReadOnlyList<string> Urls, DateTimeOffset StartedAt, string Version, string? StopEvent = null)
{
    public const string FileName = "semantic-router.instance.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string PathIn(string configurationDirectory) => Path.Combine(configurationDirectory, FileName);

    public void Write(string configurationDirectory)
    {
        File.WriteAllText(PathIn(configurationDirectory), JsonSerializer.Serialize(this, Json));
        OwnerOnly.Restrict(PathIn(configurationDirectory));
    }

    /// <summary>The recorded instance, or <see langword="null"/> when there is none or it is unreadable.</summary>
    public static RouterInstance? Read(string configurationDirectory)
    {
        try
        {
            return JsonSerializer.Deserialize<RouterInstance>(File.ReadAllText(PathIn(configurationDirectory)), Json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The recorded instance if its process is still alive; a router that crashed leaves a stale file.</summary>
    public static RouterInstance? ReadRunning(string configurationDirectory) =>
        Read(configurationDirectory) is { } instance && instance.IsAlive() ? instance : null;

    /// <summary>
    /// Waits until a router started after <paramref name="requested"/> has recorded itself, or <see langword="null"/>
    /// after <paramref name="timeout"/>. A record from before (a router that is shutting down) does not count.
    /// </summary>
    public static async Task<RouterInstance?> WaitForStartAsync(string configurationDirectory, DateTimeOffset requested, TimeSpan timeout)
    {
        // The record stores whole milliseconds, and the clocks are the same machine's.
        DateTimeOffset since = requested.AddSeconds(-1);
        var waited = Stopwatch.StartNew();
        while (true)
        {
            if (ReadRunning(configurationDirectory) is { } instance && instance.StartedAt >= since)
            {
                return instance;
            }

            if (waited.Elapsed >= timeout)
            {
                return null;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }
    }

    /// <summary>Removes the file if it still describes this process (another instance may have replaced it).</summary>
    public static void Remove(string configurationDirectory, int processId)
    {
        if (Read(configurationDirectory)?.ProcessId == processId)
        {
            File.Delete(PathIn(configurationDirectory));
        }
    }

    public bool IsAlive()
    {
        try
        {
            using Process process = Process.GetProcessById(ProcessId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// The event every Windows router of a process waits on, and the one earlier releases' <c>service stop</c> sets.
    /// Setting it wakes one waiting router, so with several in one process (tests) it may stop the wrong one.
    /// </summary>
    public static string StopEventName(int processId) => $@"Local\semantic-router-stop-{processId}";

    /// <summary>An event name for one router alone, for <see cref="StopEvent"/>.</summary>
    public static string NewStopEventName() => $"{StopEventName(Environment.ProcessId)}-{Guid.NewGuid():N}";

    /// <summary>
    /// Shuts the router down when its <see cref="StopEvent"/> is set (Windows has no SIGTERM to send), or the process's
    /// <see cref="StopEventName"/>, which earlier releases set.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public IDisposable ListenForStop(IHostApplicationLifetime lifetime)
    {
        string[] names = StopEvent is null ? [StopEventName(ProcessId)] : [StopEvent, StopEventName(ProcessId)];
        var listeners = new StopListener[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset, names[i]);
            RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(
                signal, (_, _) => lifetime.StopApplication(), null, Timeout.Infinite, executeOnlyOnce: true);
            listeners[i] = new StopListener(signal, registration);
        }

        return new StopListeners(listeners);
    }

    /// <summary>Asks a Windows router to shut down; <see langword="false"/> when it does not listen for the request.</summary>
    [SupportedOSPlatform("windows")]
    public bool RequestStop()
    {
        if (!EventWaitHandle.TryOpenExisting(StopEvent ?? StopEventName(ProcessId), out EventWaitHandle? signal))
        {
            return false;
        }

        using (signal)
        {
            return signal.Set();
        }
    }

    private sealed class StopListener(EventWaitHandle signal, RegisteredWaitHandle registration) : IDisposable
    {
        public void Dispose()
        {
            registration.Unregister(null);
            signal.Dispose();
        }
    }

    private sealed class StopListeners(StopListener[] listeners) : IDisposable
    {
        public void Dispose()
        {
            foreach (StopListener listener in listeners)
            {
                listener.Dispose();
            }
        }
    }
}
