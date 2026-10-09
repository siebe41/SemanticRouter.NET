using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using SemanticRouter.Host;

namespace SemanticRouter.Desktop;

/// <summary>
/// The tray (menu bar) app: runs the router itself or shows the background service's, opens the dashboard where the
/// preferences say, and manages the background service and start at sign-in. There is no main window; quitting is
/// explicit, from the menu or <c>SemanticRouter --quit</c>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The router is disposed by QuitAsync; Avalonia never disposes an Application.")]
internal sealed class App(DesktopOptions options, SingleInstance instance, TextWriter log) : Application
{
    private readonly string _directory = options.Host.ConfigurationDirectory;
    private readonly SignInLaunch? _signIn = SignInLaunch.ForThisMachine();
    private DesktopPreferences _preferences = DesktopPreferences.Load(options.Host.ConfigurationDirectory);
    private DesktopRouter _router = null!;
    private RouterService _service = null!;
    private bool _switching;
    private WindowIcon _icon = null!;
    private DashboardWindow? _window;
    private AboutWindow? _about;
    private bool _openWhenReady;
    private bool _quitting;

    private NativeMenuItem _status = null!;
    private NativeMenuItem _problem = null!;
    private NativeMenuItem _inBackground = null!;
    private NativeMenuItem _restart = null!;
    private NativeMenuItem _inWindow = null!;
    private NativeMenuItem _inBrowser = null!;
    private NativeMenuItem _atSignIn = null!;

    public override void Initialize()
    {
        Name = "SemanticRouter";
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += (_, _) => _ = QuitAsync();
        }

        using (Stream png = AssetLoader.Open(new Uri("avares://SemanticRouter/Assets/icon.png")))
        {
            _icon = new WindowIcon(png);
        }

        _router = new DesktopRouter(options.Host, log);
        _router.Changed += () => Dispatcher.UIThread.Post(OnRouterChanged);
        _service = new RouterService(ServicePlatform.ForThisMachine(options.Host), options.Host, Environment.ProcessPath!, Environment.GetEnvironmentVariable);
        TrayIcon.SetIcons(this, [BuildTray()]);
        instance.Listen(command => Dispatcher.UIThread.Post(() =>
        {
            if (command == SingleInstance.Quit)
            {
                _ = QuitAsync();
            }
            else
            {
                OpenDashboard();
            }
        }));

        // A background service may start, stop or restart while the app runs; follow it.
        var poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        poll.Tick += (_, _) => _router.Refresh();
        poll.Start();

        _openWhenReady = !options.Background;
        if (!options.Background && !_preferences.Welcomed)
        {
            _ = WelcomeAsync();
        }
        else
        {
            _ = _router.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private TrayIcon BuildTray()
    {
        var open = new NativeMenuItem("Open dashboard");
        open.Click += (_, _) => OpenDashboard();
        _status = new NativeMenuItem(_router.Status) { IsEnabled = false };
        _problem = new NativeMenuItem { IsEnabled = false, IsVisible = false };
        _restart = new NativeMenuItem("Restart router");
        _restart.Click += async (_, _) => await RestartAsync();
        _inBackground = new NativeMenuItem("Keep the router running in the background") { ToggleType = MenuItemToggleType.CheckBox };
        _inBackground.Click += async (_, _) => await SetBackgroundAsync(!_service.IsOn);

        _inWindow = new NativeMenuItem(Shell.WebViewAvailable ? "App window" : $"App window ({Shell.WebViewRequirement})")
        {
            ToggleType = MenuItemToggleType.Radio,
            IsEnabled = Shell.WebViewAvailable,
        };
        _inWindow.Click += (_, _) => SetSurface(DashboardSurface.Window);
        _inBrowser = new NativeMenuItem("Web browser") { ToggleType = MenuItemToggleType.Radio };
        _inBrowser.Click += (_, _) => SetSurface(DashboardSurface.Browser);
        var openIn = new NativeMenuItem("Open the dashboard in") { Menu = [_inWindow, _inBrowser] };

        _atSignIn = new NativeMenuItem("Start at sign-in") { ToggleType = MenuItemToggleType.CheckBox, IsEnabled = _signIn is not null };
        _atSignIn.Click += (_, _) => ToggleSignIn();
        var logs = new NativeMenuItem("Show the log");
        logs.Click += (_, _) => Shell.Open(Path.GetDirectoryName(RouterLog.FileIn(_directory))!);
        var configuration = new NativeMenuItem("Show the configuration folder");
        configuration.Click += (_, _) => Shell.Open(_directory);
        var about = new NativeMenuItem("About SemanticRouter");
        about.Click += (_, _) => ShowAbout();
        var quit = new NativeMenuItem("Quit SemanticRouter");
        quit.Click += (_, _) => _ = QuitAsync();

        var tray = new TrayIcon
        {
            Icon = _icon,
            ToolTipText = "SemanticRouter",
            Menu = [open, new NativeMenuItemSeparator(), _status, _problem, _restart, new NativeMenuItemSeparator(), openIn, _inBackground, _atSignIn, logs, configuration, new NativeMenuItemSeparator(), about, quit],
        };
        tray.Clicked += (_, _) => OpenDashboard();
        UpdateMenu();
        return tray;
    }

    private void OnRouterChanged()
    {
        UpdateMenu();
        if (_router.State is RouterState.Hosting or RouterState.Attached && _openWhenReady)
        {
            _openWhenReady = false;
            OpenDashboard();
        }
        else if (_router.State == RouterState.Failed && _openWhenReady)
        {
            // Started by hand and it failed: say why rather than leave only a tray icon behind.
            _openWhenReady = false;
            Shell.Open(RouterLog.FileIn(_directory));
        }
    }

    private void UpdateMenu()
    {
        _status.Header = _switching ? "Switching the router over…" : _router.Status;
        bool serviceOn = _service.IsOn;
        _restart.IsEnabled = !_switching
            && (_router.State is RouterState.Hosting or RouterState.Stopped or RouterState.Failed || (_router.State == RouterState.Attached && serviceOn));
        _inBackground.IsChecked = serviceOn;
        _inBackground.IsEnabled = _service.Available && !_switching;
        DashboardSurface surface = EffectiveSurface;
        _inWindow.IsChecked = surface == DashboardSurface.Window;
        _inBrowser.IsChecked = surface == DashboardSurface.Browser;
        _atSignIn.IsChecked = _signIn?.IsEnabled() == true;
    }

    /// <summary>The preference, unless the app window cannot work on this machine.</summary>
    private DashboardSurface EffectiveSurface => Shell.WebViewAvailable ? _preferences.OpenDashboardIn : DashboardSurface.Browser;

    private void OpenDashboard()
    {
        if (_router.Url is not { } url)
        {
            _openWhenReady = _router.State == RouterState.Starting;
            if (!_openWhenReady)
            {
                Shell.Open(RouterLog.FileIn(_directory));
            }

            return;
        }

        var dashboard = new Uri(url + "/router");
        if (EffectiveSurface == DashboardSurface.Browser)
        {
            Shell.Open(dashboard.AbsoluteUri);
            return;
        }

        _window ??= new DashboardWindow(dashboard, Path.Combine(_directory, "webview"), _icon, _preferences.Window, placement =>
        {
            _preferences = _preferences with { Window = placement };
            _preferences.Save(_directory);
        });
        _window.ShowAt(dashboard);
    }

    private void ShowAbout()
    {
        if (_about is null)
        {
            using Stream png = AssetLoader.Open(new Uri("avares://SemanticRouter/Assets/icon.png"));
            _about = new AboutWindow(_icon, new Avalonia.Media.Imaging.Bitmap(png));
            _about.Closed += (_, _) => _about = null;
        }

        _about.Show();
        _about.Activate();
    }

    private void SetSurface(DashboardSurface surface)
    {
        _preferences = _preferences with { OpenDashboardIn = surface };
        _preferences.Save(_directory);
        if (surface == DashboardSurface.Browser)
        {
            _window?.Hide();
        }

        UpdateMenu();
    }

    private void ToggleSignIn() => SetSignIn(_signIn?.IsEnabled() != true);

    /// <summary>First start by hand: offer the background service and start at sign-in, then start or show the router.</summary>
    private async Task WelcomeAsync()
    {
        using Stream png = AssetLoader.Open(new Uri("avares://SemanticRouter/Assets/icon.png"));
        var welcome = new WelcomeWindow(_icon, new Avalonia.Media.Imaging.Bitmap(png), _service.Available, _service.IsOn, _signIn is not null);
        welcome.Show();
        WelcomeChoice? choice = await welcome.Choice;

        _preferences = _preferences with { Welcomed = true };
        _preferences.Save(_directory);
        if (choice?.StartAtSignIn == true && _signIn?.IsEnabled() == false)
        {
            SetSignIn(true);
        }

        if (choice?.KeepRunning == true)
        {
            await SetBackgroundAsync(true);
        }
        else
        {
            await _router.StartAsync();
        }
    }

    /// <summary>Turns the background service on (the service manager runs the router) or off (the app runs it).</summary>
    private async Task SetBackgroundAsync(bool on)
    {
        if (_switching)
        {
            return;
        }

        _switching = true;
        ShowProblem(null);
        UpdateMenu();
        string? problem;
        try
        {
            problem = on ? await _service.TurnOnAsync(_router) : await _service.TurnOffAsync(_router);
        }
        finally
        {
            _switching = false;
        }

        if (problem is not null)
        {
            await log.WriteLineAsync($"Could not turn the background service {(on ? "on" : "off")} ({_service.Location}): {problem}");
            ShowProblem($"Background service: {problem}");
        }

        UpdateMenu();
    }

    private async Task RestartAsync()
    {
        if (_router.State == RouterState.Attached && _service.IsOn)
        {
            _switching = true;
            ShowProblem(null);
            UpdateMenu();
            string? problem = await _service.RestartAsync(_router);
            _switching = false;
            ShowProblem(problem is null ? null : $"Background service: {problem}");
            UpdateMenu();
        }
        else
        {
            await _router.RestartAsync();
        }
    }

    /// <summary>A line under the status in the menu, for what last went wrong; <see langword="null"/> hides it.</summary>
    private void ShowProblem(string? problem)
    {
        _problem.Header = problem;
        _problem.IsVisible = problem is not null;
    }

    private void SetSignIn(bool on)
    {
        if (_signIn is null)
        {
            return;
        }

        try
        {
            if (!on)
            {
                _signIn.Disable();
            }
            else
            {
                _signIn.Enable(Environment.ProcessPath!, SignInLaunch.Arguments(_directory));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log.WriteLine($"Could not change start at sign-in ({_signIn.Location}): {ex.Message}");
        }

        UpdateMenu();
    }

    private async Task QuitAsync()
    {
        if (_quitting)
        {
            return;
        }

        _quitting = true;
        _about?.Close();
        _window?.CloseForGood();
        await _router.DisposeAsync();
        instance.Dispose();
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }
}
