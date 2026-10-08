using SemanticRouter.Host;

namespace SemanticRouter.Desktop;

/// <summary>
/// The desktop app's command line: the <c>semantic-router</c> host options (<c>--config-dir</c>, <c>--urls</c>,
/// <c>--SemanticRouter:…</c>) plus <c>--background</c> (start in the tray without opening the dashboard, as at
/// sign-in) and <c>--quit</c> (ask the running app to quit).
/// </summary>
internal sealed record DesktopOptions(HostSettings Host, bool Background, bool Quit)
{
    public static DesktopOptions Parse(IReadOnlyList<string> args, Func<string, string?>? environment = null)
    {
        bool background = args.Contains("--background"), quit = args.Contains("--quit");
        HostSettings host = HostSettings.Parse([.. args.Where(a => a is not ("--background" or "--quit"))], environment);
        if (host.Error is null && host.Command != "run")
        {
            host = host with { Error = $"The desktop app takes options, not the '{host.Command}' command; use the semantic-router tool for that." };
        }

        return new DesktopOptions(host, background, quit);
    }
}
