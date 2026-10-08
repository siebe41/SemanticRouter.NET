using Avalonia;
using SemanticRouter.Desktop;
using SemanticRouter.Host;

// SemanticRouter [--background] [--config-dir <dir>] [--urls <urls>] [--SemanticRouter:<key>=<value>]
// SemanticRouter --quit
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
}
