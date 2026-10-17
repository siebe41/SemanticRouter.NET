using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SemanticRouter.Desktop;

/// <summary>
/// What the Updates section of the About window shows: where updates stand, what the person can do now (check, or
/// restart to update), and the two switches the tray menu also has.
/// </summary>
internal sealed record UpdateStatus(
    string Headline,
    string? Detail,
    bool CanCheck,
    bool CanRestart,
    bool Installed,
    bool CheckAutomatically,
    bool ApplyOnQuit,
    bool ApplyOnQuitLocked)
{
    /// <summary>
    /// The status of <paramref name="updater"/>, while the app is <paramref name="checking"/> or
    /// <paramref name="downloading"/> (for Restart to update) or neither; <paramref name="checkAutomatically"/> is the
    /// user's preference.
    /// </summary>
    public static UpdateStatus Of(DesktopUpdater updater, bool checking, bool downloading, bool checkAutomatically, DateTimeOffset now)
    {
        (string headline, string? detail, bool canCheck, bool canRestart) = Describe(updater, checking, downloading, checkAutomatically, now);
        return new UpdateStatus(headline, detail, canCheck, canRestart, updater.Installed, checkAutomatically, !updater.NotifyOnly, updater.NotifyLocked);
    }

    private static (string Headline, string? Detail, bool CanCheck, bool CanRestart) Describe(
        DesktopUpdater updater, bool checking, bool downloading, bool checkAutomatically, DateTimeOffset now)
    {
        if (!updater.Installed)
        {
            return updater.Off is { } off
                ? ($"Updates are off: {off}.", null, false, false)
                : ("This copy was not installed with the installer, so it does not update itself.", "New versions are on the releases page.", false, false);
        }

        if (downloading && updater.Offer is { } downloadingVersion)
        {
            return ($"Downloading version {downloadingVersion}…", "The app restarts with it when the download is done.", false, false);
        }

        if (updater.Ready is { } ready && updater.Offer is not null)
        {
            string when = updater.NotifyOnly
                ? "Restart to update installs it and starts the app again."
                : "It is installed when you quit the app, or now with Restart to update.";
            return ($"Version {ready} is ready to install.", updater.Problem ?? when, false, true);
        }

        if (updater.Offer is { } offer)
        {
            return ($"Version {offer} is available.", updater.Problem ?? "Restart to update downloads and installs it, then starts the app again.", false, true);
        }

        if (checking)
        {
            return ("Checking for updates…", null, false, false);
        }

        if (updater.Available is { } found)
        {
            // Found, but the download in the background failed: Check now tries again.
            return ($"Version {found} is available but could not be downloaded yet.", "It is tried again at the next check.", true, false);
        }

        string current = updater.CurrentVersion is { } version ? $"Version {version}" : "This version";
        string schedule = checkAutomatically
            ? "Updates are checked a minute after the app starts, then twice a day."
            : "Automatic checks are off.";
        if (updater.CheckFailed is { } failed)
        {
            return ($"Could not check for updates: {failed}", schedule, true, false);
        }

        if (updater.Problem is { } problem)
        {
            return (problem, schedule, true, false);
        }

        return updater.LastChecked is { } last
            ? ($"{current} is up to date.", $"Last checked {When(last, now)}. {schedule}", true, false)
            : ($"{current} is installed.", schedule, true, false);
    }

    private static string When(DateTimeOffset at, DateTimeOffset now)
    {
        string time = at.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);
        return at.ToLocalTime().Date == now.ToLocalTime().Date
            ? $"at {time}"
            : $"on {at.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)} at {time}";
    }
}

/// <summary>The Updates section of the About window: the <see cref="UpdateStatus"/>, Check now, Restart to update and the two switches.</summary>
internal sealed class UpdatesSection : StackPanel
{
    private readonly TextBlock _headline = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = Accessible.SecondaryOpacity };
    private readonly Button _check = new() { Content = "Check now" };
    private readonly Button _restart = new() { Content = "Restart to update", Classes = { "accent" } };
    private readonly CheckBox _automatic = new() { Content = "Check for updates automatically" };
    private readonly CheckBox _onQuit = new();

    public UpdatesSection()
    {
        Spacing = 6;

        // A check's outcome replaces the headline: screen readers say it when it changes, without moving the focus.
        AutomationProperties.SetLiveSetting(_headline, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_detail, AutomationLiveSetting.Polite);
        _check.Click += (_, _) => CheckRequested?.Invoke();
        _restart.Click += (_, _) => RestartRequested?.Invoke();
        _automatic.Click += (_, _) => CheckAutomaticallyChanged?.Invoke(_automatic.IsChecked == true);
        _onQuit.Click += (_, _) => ApplyOnQuitChanged?.Invoke(_onQuit.IsChecked == true);

        Children.Add(Accessible.Heading(new TextBlock { Text = "Updates", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) }, 2));
        Children.Add(_headline);
        Children.Add(_detail);
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2), Children = { _restart, _check } });
        Children.Add(_automatic);
        Children.Add(_onQuit);
    }

    /// <summary>The person chose Check now.</summary>
    public event Action? CheckRequested;

    /// <summary>The person chose Restart to update.</summary>
    public event Action? RestartRequested;

    /// <summary>The person ticked or unticked Check for updates automatically.</summary>
    public event Action<bool>? CheckAutomaticallyChanged;

    /// <summary>The person ticked or unticked Install updates when quitting.</summary>
    public event Action<bool>? ApplyOnQuitChanged;

    public void Show(UpdateStatus status)
    {
        bool focused = _check.IsFocused || _restart.IsFocused;
        _headline.Text = status.Headline;
        _detail.Text = status.Detail;
        _detail.IsVisible = status.Detail is not null;
        _check.IsVisible = status.Installed && !status.CanRestart;
        _check.IsEnabled = status.CanCheck;
        _restart.IsVisible = status.CanRestart;
        _automatic.IsVisible = _onQuit.IsVisible = status.Installed;
        _automatic.IsChecked = status.CheckAutomatically;
        _onQuit.IsChecked = status.ApplyOnQuit;
        _onQuit.IsEnabled = !status.ApplyOnQuitLocked;
        _onQuit.Content = status.ApplyOnQuitLocked ? "Install updates when quitting (off: your organization's policy)" : "Install updates when quitting";

        // Check now is disabled while it checks, and gives way to Restart to update when an update is found: the focus
        // moves to what can be used next, rather than to nowhere.
        if (focused && !(_check.IsFocused && _check.IsVisible && _check.IsEnabled) && !(_restart.IsFocused && _restart.IsVisible))
        {
            Control? next = _restart.IsVisible ? _restart : _check.IsVisible && _check.IsEnabled ? _check : _automatic.IsVisible ? _automatic : null;
            next?.Focus();
        }
    }
}
