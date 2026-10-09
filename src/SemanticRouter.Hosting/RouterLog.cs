using Microsoft.Extensions.Logging.Console;

namespace SemanticRouter.Host;

/// <summary>
/// The log file of a router without a console (the Windows background router, the desktop app):
/// <c>logs/semantic-router.log</c> in the configuration directory, rolled over to <c>.1</c> at start past 10 MB.
/// </summary>
internal static class RouterLog
{
    private const long RollOverBytes = 10 * 1024 * 1024;

    public static string FileIn(string configurationDirectory) => Path.Combine(configurationDirectory, "logs", "semantic-router.log");

    /// <summary>Sends everything written to the console (the banner, the console logger) to the log file.</summary>
    public static TextWriter RedirectConsole(string configurationDirectory)
    {
        string log = FileIn(configurationDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        if (File.Exists(log) && new FileInfo(log).Length > RollOverBytes)
        {
            File.Move(log, log + ".1", overwrite: true);
        }

        var writer = new StreamWriter(new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        TextWriter synchronized = TextWriter.Synchronized(writer);
        Console.SetOut(synchronized);
        Console.SetError(synchronized);
        return synchronized;
    }

    /// <summary>
    /// Runs the router with no console to write to (started in the background, or headless by a service manager):
    /// the banner, the logs and a crash all go to the log file.
    /// </summary>
    public static async Task<int> RunAsync(HostSettings settings)
    {
        await using TextWriter log = RedirectConsole(settings.ConfigurationDirectory);
        try
        {
            return await RouterHost.RunAsync(settings, log, Configure).ConfigureAwait(false);
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
