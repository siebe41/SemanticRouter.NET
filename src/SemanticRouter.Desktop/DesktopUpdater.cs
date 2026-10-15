using SemanticRouter.AspNetCore;
using SemanticRouter.Host;
using Velopack;
using Velopack.Sources;

namespace SemanticRouter.Desktop;

/// <summary>
/// Updates for an installed app (Velopack), from GitHub Releases: checked at start and twice a day, downloaded in the
/// background (a delta when there is one), applied when the app quits or the person chooses to restart. The background
/// service runs this same program, so it is stopped first and started again after the update. The zip downloads are
/// not installed and do not update themselves.
/// </summary>
/// <remarks>
/// <c>SEMANTICROUTER_UPDATE_SOURCE</c> points the updater at another feed (a URL or a folder of packages), for testing.
/// The machine policy (<see cref="MachinePolicy.Updates"/>) can turn updates off or pin them to an internal feed; the
/// user's preference and the variable cannot override it. A feed over plain HTTP (other than to this machine) is refused.
/// </remarks>
internal sealed class DesktopUpdater(UpdateManager? manager, ServicePlatform? platform, TextWriter log, IReadOnlyList<string>? hostArguments = null)
{
    /// <summary>The argument the updated app is started with to start the service again, then carry on as usual.</summary>
    public const string ResumeService = "--resume-service";

    private VelopackAsset? _ready;

    /// <summary>The updater for this app, or one that does nothing when the app was not installed.</summary>
    public static DesktopUpdater Create(HostSettings settings, Func<string, string?> environment, TextWriter log)
    {
        UpdatePolicy policy;
        try
        {
            MachinePolicy machine = MachinePolicyLoader.Load();
            policy = machine.Offline ? new UpdatePolicy { Mode = UpdateMode.Off } : machine.Updates;
        }
        catch (InvalidDataException ex)
        {
            // An administrator set a policy that cannot be read: do not update past it.
            log.WriteLine($"Updates are off: {ex.Message}");
            return new DesktopUpdater(null, ServicePlatform.ForThisMachine(settings), log, HostArgumentsOf(settings));
        }

        (string? feed, string? off) = Feed(policy, environment);
        if (off is not null)
        {
            log.WriteLine($"Updates are off: {off}.");
            return new DesktopUpdater(null, ServicePlatform.ForThisMachine(settings), log, HostArgumentsOf(settings));
        }

        UpdateManager? manager = null;
        try
        {
            manager = feed is not null
                ? new UpdateManager(feed)
                : new UpdateManager(new GithubSource(ProjectLinks.Repository, null, false));
            if (!manager.IsInstalled)
            {
                manager = null;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            log.WriteLine($"Updates are off: {ex.Message}");
            manager = null;
        }

        return new DesktopUpdater(manager, ServicePlatform.ForThisMachine(settings), log, HostArgumentsOf(settings));
    }

    /// <summary>
    /// Where updates come from: the policy's feed, else <c>SEMANTICROUTER_UPDATE_SOURCE</c>, else GitHub Releases
    /// (<see langword="null"/>); or why there are none.
    /// </summary>
    internal static (string? Feed, string? Off) Feed(UpdatePolicy policy, Func<string, string?> environment)
    {
        if (policy.Mode == UpdateMode.Off)
        {
            return (null, "turned off by your organization's policy");
        }

        string? feed = policy.Mode == UpdateMode.Feed ? policy.Feed : environment("SEMANTICROUTER_UPDATE_SOURCE") is { Length: > 0 } source ? source : null;
        return feed is not null && Uri.TryCreate(feed, UriKind.Absolute, out Uri? address) && address.Scheme == Uri.UriSchemeHttp && !address.IsLoopback
            ? (null, $"the update feed {address.Authority} is not HTTPS")
            : (feed, null);
    }

    /// <summary>
    /// What the new version is started with after the update: the app again (starting the service first when it was
    /// on), or, when the app quit, only the service; with the same configuration directory and host options.
    /// </summary>
    internal static string[] RestartArguments(bool restartApp, bool serviceOn, IReadOnlyList<string> hostArguments) => (restartApp, serviceOn) switch
    {
        (true, true) => [ResumeService, .. hostArguments],
        (true, false) => [.. hostArguments],
        (false, true) => ["--install-service", .. hostArguments],
        _ => [],
    };

    /// <summary>The options this app was started with that the new version needs too: the configuration directory and the host's.</summary>
    internal static IReadOnlyList<string> HostArgumentsOf(HostSettings settings) => ["--config-dir", settings.ConfigurationDirectory, .. settings.HostArguments];

    /// <summary>Whether this app was installed (and so can update itself).</summary>
    public bool Installed => manager is not null;

    /// <summary>The installed version, when installed.</summary>
    public string? CurrentVersion => manager?.CurrentVersion?.ToString();

    /// <summary>The version downloaded and waiting to be applied, if any.</summary>
    public string? Ready => (_ready ?? manager?.UpdatePendingRestart)?.Version.ToString();

    /// <summary>Checks for an update and downloads it. Returns the version now waiting, if any.</summary>
    public async Task<string?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (manager is null)
        {
            return null;
        }

        try
        {
            UpdateInfo? update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is not null)
            {
                await manager.DownloadUpdatesAsync(update, null, cancellationToken).ConfigureAwait(false);
                _ready = update.TargetFullRelease;
                log.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} Update {_ready.Version} downloaded; it is applied when the app quits or restarts.");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Offline, rate-limited by GitHub, or a feed without this channel: try again later.
            log.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} Checking for updates failed: {ex.Message}");
        }

        return Ready;
    }

    /// <summary>
    /// Hands the waiting update to the updater, which applies it once this process exits. The background service is
    /// stopped first; after the update it starts again: the app restarts with <see cref="ResumeService"/> when
    /// <paramref name="restartApp"/>, otherwise the new version reinstalls and starts the service on its own.
    /// Returns <see langword="false"/> when there was nothing to apply.
    /// </summary>
    public async Task<bool> ApplyOnExitAsync(bool restartApp)
    {
        VelopackAsset? update = _ready ?? manager?.UpdatePendingRestart;
        if (manager is null || update is null)
        {
            return false;
        }

        bool serviceOn = platform?.IsInstalled() == true;
        if (serviceOn)
        {
            ServiceOutcome stopped = await platform!.StopAsync().ConfigureAwait(false);
            log.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} Stopping the background service for the update: {(stopped.Succeeded ? "stopped" : stopped.Detail)}");
        }

        string[] args = RestartArguments(restartApp, serviceOn, hostArguments ?? []);
        manager.WaitExitThenApplyUpdates(update, silent: true, restart: args.Length > 0 || restartApp, args);
        log.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} Applying update {update.Version} as the app exits.");
        return true;
    }
}
