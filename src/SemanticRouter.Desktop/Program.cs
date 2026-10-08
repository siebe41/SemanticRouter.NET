using Avalonia;
using SemanticRouter.Desktop;
using SemanticRouter.Host;

// SemanticRouter [--background] [--config-dir <dir>] [--urls <urls>] [--SemanticRouter:<key>=<value>]
// SemanticRouter --quit
// SemanticRouter --headless [options]   the router only, no window or tray (what the background service runs)
// SemanticRouter --install-service | --uninstall-service [options]   set the background service up or remove it
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        DesktopOptions options = DesktopOptions.Parse(args);
        if (options.Host.Error is { } error)
        {
            Console.Error.WriteLine(error);
            return 2;
        }

        string directory = options.Host.ConfigurationDirectory;
        Directory.CreateDirectory(directory);

        // The background service: a service manager started it and stops it, the app's tray is a separate process.
        // A GUI program, so on Windows it runs without a console window. Logs go to logs/semantic-router.log.
        if (options.Headless)
        {
            return RouterLog.RunAsync(options.Host).GetAwaiter().GetResult();
        }

        if (options.ServiceAction is { } action)
        {
            return ChangeServiceAsync(options, action).GetAwaiter().GetResult();
        }

        // Already running for this directory: hand over (open the dashboard, or quit) and leave. A sign-in start
        // while it runs does nothing.
        var instance = new SingleInstance(directory);
        string command = options.Quit ? SingleInstance.Quit : options.Background ? SingleInstance.Ping : SingleInstance.Show;
        if (instance.TrySendAsync(command, TimeSpan.FromSeconds(1)).GetAwaiter().GetResult() || options.Quit)
        {
            instance.Dispose();
            return 0;
        }

        using TextWriter log = RouterLog.RedirectConsole(directory);
        ShellEnvironment.AdoptLoginShellPath();
        return AppBuilder.Configure(() => new App(options, instance, log))
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }

    /// <summary>
    /// Installs the background service (this program, <c>--headless</c>) or removes it, for scripts and installers.
    /// The outcome goes to the console and the log; the exit code is 0 when it worked.
    /// </summary>
    private static async Task<int> ChangeServiceAsync(DesktopOptions options, string action)
    {
        ShellEnvironment.AdoptLoginShellPath();
        ServicePlatform? platform = ServicePlatform.ForThisMachine(options.Host);
        string message;
        bool ok;
        if (platform is null)
        {
            (ok, message) = (false, "This operating system has no service manager to run the router.");
        }
        else if (action == "install")
        {
            ServiceOutcome outcome = await platform.InstallAsync(
                ServiceDefinition.ForDesktop(options.Host, Environment.ProcessPath!, Environment.GetEnvironmentVariable));
            (ok, message) = (outcome.Succeeded, outcome.Succeeded ? $"Installed and started the background service: {platform.Location}" : $"Could not install the background service: {outcome.Detail}");
        }
        else if (!platform.IsInstalled())
        {
            (ok, message) = (true, "The background service is not installed.");
        }
        else
        {
            ServiceOutcome outcome = await platform.UninstallAsync();
            (ok, message) = (outcome.Succeeded, outcome.Succeeded ? $"Removed the background service: {platform.Location}" : $"Could not remove the background service: {outcome.Detail}");
        }

        Console.WriteLine(message);
        string log = RouterLog.FileIn(options.Host.ConfigurationDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        await File.AppendAllTextAsync(log, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        return ok ? 0 : 1;
    }
}
