using Microsoft.Extensions.Logging.Console;
using SemanticRouter.AspNetCore;

namespace SemanticRouter.Host;

/// <summary>
/// The log file of a router without a console (the Windows background router, the desktop app):
/// <c>logs/semantic-router.log</c> in the local data directory (<see cref="HostSettings.LocalDataDirectory"/>), rolled
/// over to <c>.1</c> at start past 10 MB.
/// </summary>
internal static class RouterLog
{
    private const long RollOverBytes = 10 * 1024 * 1024;

    public static string FileIn(string localDataDirectory) => Path.Combine(localDataDirectory, "logs", "semantic-router.log");

    /// <summary>The log file for <paramref name="settings"/>.</summary>
    public static string FileOf(HostSettings settings) => FileIn(settings.LocalDataDirectory);

    /// <summary>Sends everything written to the console (the banner, the console logger) to the log file.</summary>
    public static TextWriter RedirectConsole(HostSettings settings)
    {
        // Before the log is opened, so an earlier install's logs folder can move out of the roaming profile with it.
        LocalData.MoveFromConfigurationDirectory(settings);
        string log = FileOf(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        if (File.Exists(log) && new FileInfo(log).Length > RollOverBytes)
        {
            File.Move(log, log + ".1", overwrite: true);
        }

        var writer = new StreamWriter(new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        OwnerOnly.Restrict(log);
        TextWriter synchronized = TextWriter.Synchronized(writer);
        Console.SetOut(synchronized);
        Console.SetError(synchronized);
        return synchronized;
    }

    /// <summary>
    /// Runs the router with no console to write to (started in the background, or headless by a service manager):
    /// the banner, the logs and a crash all go to the log file.
    /// </summary>
    /// <param name="lifecycle">What the dashboard may do to this process (the tool's background router), or <see langword="null"/>.</param>
    public static async Task<int> RunAsync(HostSettings settings, RouterLifecycle? lifecycle = null)
    {
        await using TextWriter log = RedirectConsole(settings);
        try
        {
            return await RouterHost.RunAsync(settings, log, Configure, lifecycle).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await log.WriteLineAsync($"The router stopped: {ex}").ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>A file has no clock and shows no colours: time-stamped, plain console log lines.</summary>
    public static void Configure(WebApplicationBuilder builder) =>
        builder.Logging.AddSimpleConsole(options =>
        {
            options.ColorBehavior = LoggerColorBehavior.Disabled;
            options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
        });
}
