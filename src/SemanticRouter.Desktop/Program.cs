using Avalonia;
using SemanticRouter.Desktop;
using SemanticRouter.Host;
using Velopack;

// SemanticRouter [--background] [--config-dir <dir>] [--urls <urls>] [--SemanticRouter:<key>=<value>]
// SemanticRouter --quit
// SemanticRouter --headless [options]   the router only, no window or tray (what the background service runs)
// SemanticRouter --install-service | --uninstall-service [options]   set the background service up or remove it
// SemanticRouter --update [options]   download and apply an available update (installed app only) and exit
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The installer's hooks arrive as arguments: Velopack handles them here, before anything else, and exits. A
        // downloaded update is never applied at start, only once the background service is stopped (DesktopUpdater).
        VelopackApp velopack = VelopackApp.Build().SetArgs(args).SetAutoApplyOnStartup(false);
        if (OperatingSystem.IsWindows())
        {
            // Only Windows has an uninstaller; on macOS and Linux the person deletes the app (docs/desktop.md).
            velopack = velopack.OnBeforeUninstallFastCallback(_ =>
                Installation.BeforeUninstall(DesktopOptions.Parse([]).Host, ServicePlatform.ForThisMachine, SignInLaunch.ForThisMachine()));
        }

        velopack.Run();

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

        if (options.Update)
        {
            return UpdateAsync(options).GetAwaiter().GetResult();
        }

        // Restarted after an update that stopped the background service: start it again, then carry on.
        if (options.ResumeService && ServicePlatform.ForThisMachine(options.Host) is { } service && service.IsInstalled())
        {
            service.StartAsync().GetAwaiter().GetResult();
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
    /// Applies an available update without the tray, for scripts and tests: the background service is stopped, and the
    /// new version starts it again. The exit code is 0 when the app is up to date or the update was handed over, 1 when
    /// it was not (refused by the machine policy's publisher check, for one).
    /// </summary>
    private static async Task<int> UpdateAsync(DesktopOptions options)
    {
        string log = RouterLog.FileIn(options.Host.ConfigurationDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        await using StreamWriter writer = AppendTo(log);
        DesktopUpdater updater = DesktopUpdater.Create(options.Host, Environment.GetEnvironmentVariable, writer);
        string message;
        int code;
        if (!updater.Installed)
        {
            (message, code) = ("This copy was not installed with the installer, so it does not update itself; download the new version.", 1);
        }
        else if (await updater.CheckAsync() is not { } version)
        {
            // A refused update (not signed by the publisher the policy names) is not "up to date".
            (message, code) = updater.Problem is { } refused ? (refused, 1) : ($"SemanticRouter {updater.CurrentVersion} is up to date.", 0);
        }
        else
        {
            // Asked for by name, so also when updates are otherwise only announced (Updates:Apply Notify).
            bool handed = await updater.ApplyNowAsync(restartApp: false);
            (message, code) = handed
                ? ($"Updating SemanticRouter {updater.CurrentVersion} to {version}.", 0)
                : ($"The update could not be applied{(updater.Problem is { } problem ? ": " + problem : ".")}", 1);
        }

        Console.WriteLine(message);
        await writer.WriteLineAsync($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}");
        return code;
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
                ServiceDefinition.ForDesktop(options.Host, Installation.Executable(Environment.GetEnvironmentVariable), Environment.GetEnvironmentVariable));
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
        await using (StreamWriter writer = AppendTo(log))
        {
            await writer.WriteLineAsync($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}");
        }

        return ok ? 0 : 1;
    }

    /// <summary>The router's log, shared with a router that is writing to it (the background service).</summary>
    private static StreamWriter AppendTo(string log) =>
        new(new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
}
