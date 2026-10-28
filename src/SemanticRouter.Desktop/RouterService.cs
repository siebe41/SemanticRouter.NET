using SemanticRouter.Host;

namespace SemanticRouter.Desktop;

/// <summary>
/// The background service, as the app manages it. On: the operating system's service manager runs
/// <c>SemanticRouter --headless</c> (this same program) at sign-in, and on macOS and Linux restarts it if it fails (the
/// Windows sign-in entry does not), and the app only shows that router; quitting the app leaves it running. Off: the app
/// runs the router itself. Switching hands the port from one to the other.
/// </summary>
internal sealed class RouterService(ServicePlatform? platform, HostSettings settings, string executable, Func<string, string?> environment)
{
    /// <summary>What keeping the router running in the background means here, for the welcome and settings windows.</summary>
    internal static string KeepRunningHelp => OperatingSystem.IsWindows()
        ? "The router then starts when you sign in and keeps answering when this app is closed."
        : "The router then starts when you sign in, restarts if it fails, and keeps answering when this app is closed.";

    /// <summary>How long to wait for the service's router to listen after starting it.</summary>
    internal TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether this operating system has a service manager to use.</summary>
    public bool Available => platform is not null;

    /// <summary>Whether the service is installed (by the app, or by <c>semantic-router service install</c>).</summary>
    public bool IsOn => platform?.IsInstalled() == true;

    /// <summary>
    /// The program the service runs when it is not this app (the <c>semantic-router</c> tool, installed with
    /// <c>service install</c>); <see langword="null"/> when it is this app, or no service is installed.
    /// </summary>
    public string? OtherProgram => platform is not null && platform.IsInstalled() && !platform.Runs(executable) ? platform.InstalledProgram() : null;

    public string? Location => platform?.Location;

    /// <summary>The tray's and the Settings window's label for the service, naming the program it runs when that is not this app.</summary>
    public static string BackgroundLabel(string? otherProgram) =>
        otherProgram is null
            ? "Keep the router running in the background"
            : $"Keep the router running in the background (the service runs {Path.GetFileName(otherProgram)})";

    /// <summary>Installs and starts the service, then shows its router. Returns what went wrong, or <see langword="null"/>.</summary>
    public async Task<string?> TurnOnAsync(DesktopRouter router)
    {
        if (platform is null)
        {
            return "this operating system has no service manager to run it.";
        }

        await router.ReleaseAsync();
        DateTimeOffset requested = DateTimeOffset.UtcNow;
        ServiceOutcome outcome = await platform.InstallAsync(ServiceDefinition.ForDesktop(settings, executable, environment));
        if (!outcome.Succeeded)
        {
            await router.StartAsync();
            return outcome.Detail;
        }

        RouterInstance? started = await RouterInstance.WaitForStartAsync(settings.ConfigurationDirectory, requested, StartTimeout);
        await router.StartAsync();
        return started is null ? $"the service did not start within {StartTimeout.TotalSeconds:0} seconds; see {platform.LogHint}" : null;
    }

    /// <summary>Stops and removes the service, then runs the router in the app again.</summary>
    public async Task<string?> TurnOffAsync(DesktopRouter router)
    {
        if (platform is null)
        {
            return null;
        }

        ServiceOutcome outcome = await platform.UninstallAsync();

        // Give the service's router a moment to let go of the port before the app takes it.
        for (int i = 0; i < 40 && RouterInstance.ReadRunning(settings.ConfigurationDirectory) is not null; i++)
        {
            await Task.Delay(250);
        }

        await router.StartAsync();
        return outcome.Succeeded ? null : outcome.Detail;
    }

    /// <summary>Restarts the service's router and shows the new one.</summary>
    public async Task<string?> RestartAsync(DesktopRouter router)
    {
        if (platform is null)
        {
            return null;
        }

        DateTimeOffset requested = DateTimeOffset.UtcNow;
        ServiceOutcome outcome = await platform.RestartAsync();
        if (!outcome.Succeeded)
        {
            return outcome.Detail;
        }

        RouterInstance? started = await RouterInstance.WaitForStartAsync(settings.ConfigurationDirectory, requested, StartTimeout);
        router.Refresh();
        return started is null ? $"the service did not start again within {StartTimeout.TotalSeconds:0} seconds; see {platform.LogHint}" : null;
    }
}
