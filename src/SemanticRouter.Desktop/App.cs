using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using SemanticRouter.Host;

namespace SemanticRouter.Desktop;

/// <summary>
/// The tray (menu bar) app: hosts or attaches to the router, opens the dashboard where the preferences say, and offers
/// start at sign-in. There is no main window; quitting is explicit, from the menu or <c>SemanticRouter --quit</c>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "The router is disposed by QuitAsync; Avalonia never disposes an Application.")]
internal sealed class App(DesktopOptions options, SingleInstance instance, TextWriter log) : Application
{
    private readonly string _directory = options.Host.ConfigurationDirectory;
    private readonly SignInLaunch? _signIn = SignInLaunch.ForThisMachine();
    private DesktopPreferences _preferences = DesktopPreferences.Load(options.Host.ConfigurationDirectory);
    private DesktopRouter _router = null!;
    private WindowIcon _icon = null!;
    private DashboardWindow? _window;
    private AboutWindow? _about;
    private bool _openWhenReady;
    private bool _quitting;

    private NativeMenuItem _status = null!;
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
        _ = _router.StartAsync();
        base.OnFrameworkInitializationCompleted();
    }

    private TrayIcon BuildTray()
    {
        var open = new NativeMenuItem("Open dashboard");
        open.Click += (_, _) => OpenDashboard();
        _status = new NativeMenuItem(_router.Status) { IsEnabled = false };
        _restart = new NativeMenuItem("Restart router");
        _restart.Click += async (_, _) => await _router.RestartAsync();

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
            Menu = [open, new NativeMenuItemSeparator(), _status, _restart, new NativeMenuItemSeparator(), openIn, _atSignIn, logs, configuration, new NativeMenuItemSeparator(), about, quit],
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
        _status.Header = _router.Status;
        _restart.IsEnabled = _router.State is RouterState.Hosting or RouterState.Stopped or RouterState.Failed;
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

    private void ToggleSignIn()
    {
        if (_signIn is null)
        {
            return;
        }

        try
        {
            if (_signIn.IsEnabled())
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
