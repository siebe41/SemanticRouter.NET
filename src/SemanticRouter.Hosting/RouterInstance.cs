using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;

namespace SemanticRouter.Host;

/// <summary>
/// The router running on a configuration directory: written to <c>semantic-router.instance.json</c> once it listens and
/// removed when it stops, so <c>semantic-router service status</c> can say where it answers and Windows can stop it.
/// </summary>
internal sealed record RouterInstance(int ProcessId, IReadOnlyList<string> Urls, DateTimeOffset StartedAt, string Version)
{
    public const string FileName = "semantic-router.instance.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string PathIn(string configurationDirectory) => Path.Combine(configurationDirectory, FileName);

    public void Write(string configurationDirectory) =>
        File.WriteAllText(PathIn(configurationDirectory), JsonSerializer.Serialize(this, Json));

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

    /// <summary>The name of the event a Windows router waits on; setting it shuts that router down cleanly.</summary>
    public static string StopEventName(int processId) => $@"Local\semantic-router-stop-{processId}";

    /// <summary>Shuts the router down when <see cref="StopEventName"/> is set (Windows has no SIGTERM to send).</summary>
    [SupportedOSPlatform("windows")]
    public static IDisposable ListenForStop(IHostApplicationLifetime lifetime)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, StopEventName(Environment.ProcessId));
        RegisteredWaitHandle registration = ThreadPool.RegisterWaitForSingleObject(
            signal, (_, _) => lifetime.StopApplication(), null, Timeout.Infinite, executeOnlyOnce: true);
        return new StopListener(signal, registration);
    }

    /// <summary>Asks a Windows router to shut down; <see langword="false"/> when it does not listen for the request.</summary>
    [SupportedOSPlatform("windows")]
    public bool RequestStop()
    {
        if (!EventWaitHandle.TryOpenExisting(StopEventName(ProcessId), out EventWaitHandle? signal))
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
}
