using SemanticRouter.Host;

namespace SemanticRouter.Desktop;

/// <summary>
/// The desktop app's command line: the <c>semantic-router</c> host options (<c>--config-dir</c>, <c>--urls</c>,
/// <c>--SemanticRouter:…</c>) plus <c>--background</c> (start in the tray without opening the dashboard, as at
/// sign-in), <c>--quit</c> (ask the running app to quit), <c>--headless</c> (run only the router, with no window or
/// tray: what the background service runs), and <c>--install-service</c> / <c>--uninstall-service</c> (set the
/// background service up or remove it without the tray, for scripts and installers), <c>--update</c> (apply an
/// available update and exit) and <c>--resume-service</c> (start the background service again, after an update).
/// </summary>
internal sealed record DesktopOptions(
    HostSettings Host, bool Background, bool Quit, bool Headless = false, string? ServiceAction = null, bool Update = false, bool ResumeService = false)
{
    private static readonly string[] Flags = ["--background", "--quit", "--headless", "--install-service", "--uninstall-service", "--update", DesktopUpdater.ResumeService];

    public static DesktopOptions Parse(IReadOnlyList<string> args, Func<string, string?>? environment = null)
    {
        bool background = args.Contains("--background"), quit = args.Contains("--quit"), headless = args.Contains("--headless");
        string? serviceAction = args.Contains("--install-service") ? "install" : args.Contains("--uninstall-service") ? "uninstall" : null;
        HostSettings host = HostSettings.Parse([.. args.Where(a => !Flags.Contains(a))], environment);
        if (host.Error is null && host.Command != "run")
        {
            host = host with { Error = $"The desktop app takes options, not the '{host.Command}' command; use the semantic-router tool for that." };
        }

        return new DesktopOptions(host, background, quit, headless, serviceAction, args.Contains("--update"), args.Contains(DesktopUpdater.ResumeService));
    }
}
