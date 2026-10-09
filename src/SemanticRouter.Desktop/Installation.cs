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
    /// is no longer there. The configuration directory stays.
    /// </summary>
    internal static void BeforeUninstall(HostSettings settings, Func<HostSettings, ServicePlatform?> platforms, SignInLaunch? signIn)
    {
        try
        {
            ServicePlatform? platform = platforms(settings);
            if (platform?.IsInstalled() == true)
            {
                platform.UninstallAsync().GetAwaiter().GetResult();
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
