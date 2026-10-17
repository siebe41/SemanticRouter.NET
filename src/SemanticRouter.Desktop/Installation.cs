using SemanticRouter.Host;

namespace SemanticRouter.Desktop;

/// <summary>
/// The app as an installed program (Velopack: a per-user <c>Setup.exe</c> on Windows, a <c>.pkg</c> on macOS, an
/// AppImage on Linux): what the uninstaller cleans up, and the path the background service and the sign-in entry
/// should start.
/// </summary>
internal static class Installation
{
    /// <summary>
    /// The program to start later (service, sign-in entry): inside an AppImage the running file is in a temporary
    /// mount, so the AppImage itself; otherwise this executable.
    /// </summary>
    public static string Executable(Func<string, string?> environment) =>
        environment("APPIMAGE") is { Length: > 0 } image && File.Exists(image) ? image : Environment.ProcessPath!;

    /// <summary>
    /// Before the files go: the background service and the sign-in entry, which would otherwise start a program that
    /// is no longer there. A service that runs another program (<paramref name="executable"/> is this app's), the
    /// <c>semantic-router</c> tool installed with <c>service install</c>, keeps working without the app, so it stays.
    /// The configuration directory stays.
    /// </summary>
    internal static void BeforeUninstall(HostSettings settings, Func<HostSettings, ServicePlatform?> platforms, SignInLaunch? signIn, string executable)
    {
        try
        {
            ServicePlatform? platform = platforms(settings);
            if (platform?.IsInstalled() == true && platform.Runs(executable))
            {
                platform.UninstallAsync().GetAwaiter().GetResult();
            }
            else if (platform?.IsInstalled() == true)
            {
                Console.Error.WriteLine($"Left the background service in place: it runs {platform.InstalledProgram()}, not this app.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Could not remove the background service: {ex.Message}");
        }

        try
        {
            if (signIn?.IsEnabled() == true)
            {
                signIn.Disable();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Could not remove the sign-in entry: {ex.Message}");
        }
    }
}
