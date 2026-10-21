using SemanticRouter.AspNetCore;
using SemanticRouter.Host;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace SemanticRouter.Desktop;

/// <summary>
/// Updates for an installed app (Velopack), from GitHub Releases: checked at start and twice a day, downloaded in the
/// background (a delta when there is one), applied when the app quits or the person chooses to restart. The background
/// service runs this same program, so it is stopped first and started again after the update. The zip downloads are
/// not installed and do not update themselves.
/// </summary>
/// <remarks>
/// <para>
/// <c>SEMANTICROUTER_UPDATE_SOURCE</c> points the updater at another feed (a URL or a folder of packages), for testing:
/// in a debug build, or when the machine policy allows it (<see cref="UpdatePolicy.AllowCustomFeed"/>). The machine
/// policy (<see cref="MachinePolicy.Updates"/>) can turn updates off or pin them to an internal feed; the user's
/// preference and the variable cannot override it. A feed that is not HTTPS or a folder (other than plain HTTP to this
/// machine) is refused.
/// </para>
/// <para>
/// Notify (the policy's <see cref="UpdateApply.Notify"/>, or the user's preference): an update is only announced, and
/// downloaded and applied when the person chooses "Restart to update"; never when the app quits. With the policy's
/// <see cref="UpdatePolicy.Publisher"/>, a downloaded update is applied only when the app in it is signed by that
/// publisher (<see cref="UpdatePublisher"/>); one that is not is deleted and not offered again.
/// </para>
/// <para>
/// Without a publisher, an update from GitHub Releases is applied only when this project's release workflow attested its
/// package for that version (<see cref="UpdateAttestation"/>); one that is not is refused the same way. When the check
/// cannot run (GitHub's API cannot be reached), the update is applied and the app says it was not checked
/// (<see cref="Problem"/>). Updates from another feed are not checked this way.
/// </para>
/// </remarks>
internal sealed class DesktopUpdater(
    IAppUpdates? updates,
    ServicePlatform? platform,
    TextWriter log,
    IReadOnlyList<string>? hostArguments = null,
    UpdateApply apply = UpdateApply.Default,
    UpdatePublisher? publisher = null,
    string? off = null,
    string? executable = null,
    UpdateAttestation? attestation = null)
{
    /// <summary>The argument the updated app is started with to start the service again, then carry on as usual.</summary>
    public const string ResumeService = "--resume-service";

    private UpdateInfo? _available;
    private VelopackAsset? _ready;
    private VelopackAsset? _verified;
    private string? _refused;
    private string? _unchecked;

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
            return new DesktopUpdater(null, ServicePlatform.ForThisMachine(settings), log, HostArgumentsOf(settings), off: "your organization's policy cannot be read", executable: Installation.Executable(environment));
        }

        bool variable = SourceVariableAllowed(policy);
        if (!variable && policy.Mode != UpdateMode.Feed && environment(SourceVariable) is { Length: > 0 })
        {
            log.WriteLine($"{SourceVariable} is ignored: a release build takes it only when your organization's policy sets Updates:AllowCustomFeed.");
        }

        (string? feed, string? off) = Feed(policy, environment, variable);
        if (off is not null)
        {
            log.WriteLine($"Updates are off: {off}.");
            return new DesktopUpdater(null, ServicePlatform.ForThisMachine(settings), log, HostArgumentsOf(settings), off: off, executable: Installation.Executable(environment));
        }

        UpdateManager? manager = null;
        string? broken = null;
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
            (manager, broken) = (null, ex.Message);
        }

        return new DesktopUpdater(
            manager is null ? null : new VelopackUpdates(manager),
            ServicePlatform.ForThisMachine(settings),
            log,
            HostArgumentsOf(settings),
            policy.Apply,
            UpdatePublisher.ForThisMachine(policy.Publisher),
            broken,
            Installation.Executable(environment),
            feed is null ? UpdateAttestation.ForGitHub(ProjectLinks.Repository) : null);
    }

    /// <summary>The environment variable that points the updater at another feed, for testing.</summary>
    internal const string SourceVariable = "SEMANTICROUTER_UPDATE_SOURCE";

    /// <summary>
    /// Whether <see cref="SourceVariable"/> counts: in a debug build, or when the machine policy allows a custom feed
    /// (<see cref="UpdatePolicy.AllowCustomFeed"/>). A release build otherwise ignores it, so whatever can set the user's
    /// environment cannot quietly move the installed app onto another feed.
    /// </summary>
    internal static bool SourceVariableAllowed(UpdatePolicy policy) => DebugBuild || policy.AllowCustomFeed;

#if DEBUG
    internal const bool DebugBuild = true;
#else
    internal const bool DebugBuild = false;
#endif

    /// <summary>
    /// Where updates come from: the policy's feed, else <see cref="SourceVariable"/> when <paramref name="variable"/>
    /// says it counts, else GitHub Releases (<see langword="null"/>); or why there are none. A feed must be HTTPS, a
    /// folder (a local or network path) or plain HTTP to this machine.
    /// </summary>
    internal static (string? Feed, string? Off) Feed(UpdatePolicy policy, Func<string, string?> environment, bool variable)
    {
        if (policy.Mode == UpdateMode.Off)
        {
            return (null, "turned off by your organization's policy");
        }

        string? feed = policy.Mode == UpdateMode.Feed ? policy.Feed
            : variable && environment(SourceVariable) is { Length: > 0 } source ? source
            : null;
        return feed is not null && Refuses(feed) is { } refused ? (null, refused) : (feed, null);

        // A string that is not an absolute URI is a relative folder path, which Velopack reads as a folder.
        static string? Refuses(string feed) =>
            !Uri.TryCreate(feed, UriKind.Absolute, out Uri? address) || address.IsFile || address.Scheme == Uri.UriSchemeHttps ? null
            : address.Scheme == Uri.UriSchemeHttp && address.IsLoopback ? null
            : address.Scheme == Uri.UriSchemeHttp ? $"the update feed {address.Authority} is not HTTPS"
            : $"the update feed is a {address.Scheme}: address, not HTTPS or a folder";
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
    public bool Installed => updates is not null;

    /// <summary>
    /// Why this app does not update itself although it may be installed: the policy turned updates off, its feed is
    /// refused, or the updater could not start. <see langword="null"/> otherwise (installed, or a plain archive).
    /// </summary>
    public string? Off => off;

    /// <summary>When the last check for an update got an answer, if one did in this run.</summary>
    public DateTimeOffset? LastChecked { get; private set; }

    /// <summary>Why the last check for an update got no answer; <see langword="null"/> when it did.</summary>
    public string? CheckFailed { get; private set; }

    /// <summary>The installed version, when installed.</summary>
    public string? CurrentVersion => updates?.CurrentVersion;

    /// <summary>The user's preference: apply a downloaded update when the app quits. Off: notify only.</summary>
    public bool ApplyOnQuit { get; set; } = true;

    /// <summary>Whether the machine policy says notify only, so the user's preference does not count.</summary>
    public bool NotifyLocked => apply == UpdateApply.Notify;

    /// <summary>Whether an update is only announced, and applied only when the person chooses to.</summary>
    public bool NotifyOnly => NotifyLocked || !ApplyOnQuit;

    /// <summary>The version downloaded and waiting to be applied, if any.</summary>
    public string? Ready => (_ready ?? updates?.Pending)?.Version.ToString();

    /// <summary>The newest version found, downloaded or not.</summary>
    public string? Available => Ready ?? _available?.TargetFullRelease.Version.ToString();

    /// <summary>The version "Restart to update" offers: the one found when notifying only, else the one downloaded.</summary>
    public string? Offer => NotifyOnly ? Available : Ready;

    /// <summary>Why the last update could not be downloaded or was refused, for the tray; <see langword="null"/> when nothing went wrong.</summary>
    public string? Problem { get; private set; }

    /// <summary>
    /// Checks for an update, and downloads it unless <see cref="NotifyOnly"/>. Returns the newest version found
    /// (<see cref="Available"/>), if any. A version that was refused (not the publisher's) is not offered again.
    /// </summary>
    public async Task<string?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (updates is null)
        {
            return null;
        }

        try
        {
            UpdateInfo? update = await updates.CheckAsync().ConfigureAwait(false);
            (LastChecked, CheckFailed) = (DateTimeOffset.Now, null);
            string? version = update?.TargetFullRelease.Version.ToString();
            if (update is not null && version != _refused)
            {
                bool news = _available?.TargetFullRelease.Version.ToString() != version;
                _available = update;
                if (!NotifyOnly)
                {
                    await DownloadAsync(chosen: false, cancellationToken).ConfigureAwait(false);
                }
                else if (news)
                {
                    log.WriteLine($"{Now} Update {version} is available; it is applied when you choose Restart to update.");
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Offline, rate-limited by GitHub, or a feed without this channel: try again later.
            log.WriteLine($"{Now} Checking for updates failed: {ex.Message}");
            CheckFailed = ex.Message;
        }

        return Available;
    }

    /// <summary>
    /// Downloads the update found (unless it already was) and checks its publisher when the policy asks for one.
    /// Returns the version now waiting (<see cref="Ready"/>), or <see langword="null"/> with <see cref="Problem"/> saying why not.
    /// A download in the background (not <paramref name="chosen"/>) that fails is only logged, and tried again at the next check.
    /// </summary>
    public async Task<string?> DownloadAsync(bool chosen = true, CancellationToken cancellationToken = default)
    {
        if (updates is null)
        {
            return null;
        }

        VelopackAsset? asset = _ready;
        try
        {
            if (asset is null && _available is { } update)
            {
                // Checked against its attestation, an update is downloaded whole: Velopack rebuilds one from deltas into
                // a zip of its own, which is not the attested package byte for byte.
                await updates.DownloadAsync(Attested ? new UpdateInfo(update.TargetFullRelease, update.IsDowngrade) : update, cancellationToken).ConfigureAwait(false);
                asset = update.TargetFullRelease;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string failed = $"Update {_available?.TargetFullRelease.Version} could not be downloaded: {ex.Message}";
            log.WriteLine($"{Now} {failed}");
            Problem = chosen ? failed : Problem;
            return null;
        }

        asset ??= updates.Pending;
        if (asset is null || !await Task.Run(() => VerifyAsync(asset), cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (_ready is null)
        {
            _ready = asset;
            Problem = _unchecked;
            log.WriteLine($"{Now} Update {asset.Version} downloaded; it is applied when {(NotifyOnly ? "you choose Restart to update" : "the app quits or restarts")}.");
        }

        return Ready;
    }

    /// <summary>
    /// The app is quitting: hands a downloaded update to the updater (<see cref="ApplyAsync"/>), unless updates are only
    /// announced (<see cref="NotifyOnly"/>), when nothing is applied. Returns whether an update was handed over.
    /// </summary>
    public async Task<bool> ApplyOnQuitAsync()
    {
        if (updates is null)
        {
            return false;
        }

        if (NotifyOnly)
        {
            if (Available is { } version)
            {
                log.WriteLine($"{Now} Update {version} is not applied as the app quits: updates are applied only when you choose to.");
            }

            return false;
        }

        return await ApplyAsync(restartApp: false).ConfigureAwait(false);
    }

    /// <summary>
    /// The person chose to update ("Restart to update", <c>--update</c>): downloads the update when it is not yet, then
    /// hands it to the updater (<see cref="ApplyAsync"/>). Returns whether an update was handed over.
    /// </summary>
    public async Task<bool> ApplyNowAsync(bool restartApp) =>
        await DownloadAsync().ConfigureAwait(false) is not null && await ApplyAsync(restartApp).ConfigureAwait(false);

    /// <summary>
    /// Hands the waiting update to the updater, which applies it once this process exits. The background service is
    /// stopped first; after the update it starts again: the app restarts with <see cref="ResumeService"/> when
    /// <paramref name="restartApp"/>, otherwise the new version reinstalls and starts the service on its own. A service
    /// that runs another program (the <c>semantic-router</c> tool, installed with <c>service install</c>) is not this
    /// app's to stop or reinstall: the update does not replace that program, so it is left running as it is.
    /// Returns <see langword="false"/> when there was nothing to apply, or it was refused.
    /// </summary>
    private async Task<bool> ApplyAsync(bool restartApp)
    {
        VelopackAsset? update = _ready ?? updates?.Pending;
        if (updates is null || update is null || !await Task.Run(() => VerifyAsync(update)).ConfigureAwait(false))
        {
            return false;
        }

        bool serviceOn = platform?.IsInstalled() == true;
        if (serviceOn && executable is not null && !platform!.Runs(executable))
        {
            log.WriteLine($"{Now} The background service runs {platform.InstalledProgram()}, not this app: the update leaves it as it is.");
            serviceOn = false;
        }

        if (serviceOn)
        {
            ServiceOutcome stopped = await platform!.StopAsync().ConfigureAwait(false);
            log.WriteLine($"{Now} Stopping the background service for the update: {(stopped.Succeeded ? "stopped" : stopped.Detail)}");
        }

        string[] args = RestartArguments(restartApp, serviceOn, hostArguments ?? []);
        updates.ApplyAfterExit(update, restart: args.Length > 0 || restartApp, args);
        log.WriteLine($"{Now} Applying update {update.Version} as the app exits.");
        return true;
    }

    /// <summary>
    /// Whether <paramref name="update"/> may be applied. With a publisher in the policy: when its package is signed by
    /// the publisher. Otherwise, for an update from GitHub: when its package is attested by the release workflow, or the
    /// attestation could not be checked (then <see cref="Problem"/> says so). A refused package is deleted, so it is not
    /// applied later either.
    /// </summary>
    /// <summary>Whether updates are checked against their attestation: from GitHub, with no publisher in the policy.</summary>
    private bool Attested => publisher is null && attestation is not null;

    private async Task<bool> VerifyAsync(VelopackAsset update)
    {
        if ((publisher is null && attestation is null) || update == _verified)
        {
            return true;
        }

        string? package = updates?.PackageFile(update);
        bool found = package is not null && File.Exists(package);
        if (publisher is not null)
        {
            string? refused = found ? publisher.Refuses(package!) : "its package is not where the updater keeps it";
            if (refused is null)
            {
                _verified = update;
                log.WriteLine($"{Now} Update {update.Version} is signed by {publisher.Expected}, as your organization's policy requires.");
                return true;
            }

            return Refuse(update, refused, $"Your organization's policy accepts only updates signed by {publisher.Expected}.");
        }

        UpdateAttestation.Verdict verdict = found
            ? await attestation!.CheckAsync(package!, Path.GetFileName(update.FileName), update.Version.ToString()).ConfigureAwait(false)
            : new UpdateAttestation.Verdict(UpdateAttestation.Outcome.Unchecked, "its package is not where the updater keeps it");
        switch (verdict.Outcome)
        {
            case UpdateAttestation.Outcome.Verified:
                (_verified, _unchecked) = (update, null);
                log.WriteLine($"{Now} Update {update.Version} was built by the project's release workflow: {verdict.Detail}.");
                return true;
            case UpdateAttestation.Outcome.Unchecked:
                // The check could not run: the update is applied, as before there was one, and the app says so.
                (_verified, _unchecked) = (update, $"Update {update.Version} could not be checked against its attestation: {verdict.Detail}");
                Problem = _unchecked;
                log.WriteLine($"{Now} {_unchecked}. It is applied anyway.");
                return true;
            default:
                return Refuse(update, verdict.Detail, "Without a publisher in your organization's policy, only updates the project's release workflow attested are applied.");
        }
    }

    /// <summary>Refuses <paramref name="update"/>: says why, deletes its package and does not offer that version again.</summary>
    private bool Refuse(VelopackAsset update, string refused, string rule)
    {
        Problem = $"Update {update.Version} was refused: {refused}";
        log.WriteLine($"{Now} {Problem}. {rule}");
        updates?.Discard(update);
        _refused = update.Version.ToString();
        (_available, _ready) = (null, null);
        return false;
    }

    private static string Now => DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The installed app's updates as Velopack provides them: a seam, so the updater can be tested without an installed app.</summary>
internal interface IAppUpdates
{
    /// <summary>The installed version.</summary>
    string? CurrentVersion { get; }

    /// <summary>A newer full package already downloaded (by this or an earlier run), if any.</summary>
    VelopackAsset? Pending { get; }

    /// <summary>The newest release in the feed, when it is newer than this one.</summary>
    Task<UpdateInfo?> CheckAsync();

    /// <summary>Downloads <paramref name="update"/> (its full package, or deltas rebuilt into one) to the packages folder.</summary>
    Task DownloadAsync(UpdateInfo update, CancellationToken cancellationToken);

    /// <summary>Where <paramref name="asset"/>'s full package is once downloaded, or <see langword="null"/> when that is not known.</summary>
    string? PackageFile(VelopackAsset asset);

    /// <summary>Deletes <paramref name="asset"/>'s downloaded package.</summary>
    void Discard(VelopackAsset asset);

    /// <summary>Starts the updater, which applies <paramref name="asset"/> once this process exits.</summary>
    void ApplyAfterExit(VelopackAsset asset, bool restart, string[] arguments);
}

/// <summary>Velopack's <see cref="UpdateManager"/> for an installed app.</summary>
internal sealed class VelopackUpdates(UpdateManager manager) : IAppUpdates
{
    public string? CurrentVersion => manager.CurrentVersion?.ToString();

    public VelopackAsset? Pending => manager.UpdatePendingRestart;

    public Task<UpdateInfo?> CheckAsync() => manager.CheckForUpdatesAsync();

    public Task DownloadAsync(UpdateInfo update, CancellationToken cancellationToken) => manager.DownloadUpdatesAsync(update, null, cancellationToken);

    // Where UpdateManager downloads to: the locator VelopackApp.Run set up for this installed app.
    public string? PackageFile(VelopackAsset asset) =>
        VelopackLocator.IsCurrentSet && VelopackLocator.Current.PackagesDir is { Length: > 0 } packages
            ? Path.Combine(packages, Path.GetFileName(asset.FileName))
            : null;

    public void Discard(VelopackAsset asset)
    {
        try
        {
            if (PackageFile(asset) is { } file)
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still there: the policy check refuses it again before it could be applied.
        }
    }

    public void ApplyAfterExit(VelopackAsset asset, bool restart, string[] arguments) =>
        manager.WaitExitThenApplyUpdates(asset, silent: true, restart, arguments);
}
