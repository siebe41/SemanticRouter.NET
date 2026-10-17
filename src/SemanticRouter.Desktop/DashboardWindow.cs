using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace SemanticRouter.Desktop;

/// <summary>
/// The dashboard in the platform's own web view. Links that leave the router open in the default browser; links to the
/// router stay in the window, however the web view asks (a navigation or a new window) and whichever name of this
/// machine they use. Closing the window hides it: the router keeps running in the tray. It opens sized to fit the
/// screen, or where it was last. The page and the app talk through <see cref="DesktopBridge"/>: the page asks for the
/// About window or Restart to update (<see cref="PageAction"/>), and the app hands it where updates stand (<see cref="Push"/>).
/// </summary>
internal sealed class DashboardWindow : Window
{
    private readonly NativeWebView _view;
    private readonly WindowPlacement? _saved;
    private readonly Action<WindowPlacement> _savePlacement;
    private readonly TextWriter _log;
    private Uri _home;
    private bool _closing;
    private bool _placed;
    private WindowPlacement? _normal;
    private DesktopPageState? _state;

    public DashboardWindow(Uri home, string dataDirectory, WindowIcon icon, WindowPlacement? saved, Action<WindowPlacement> savePlacement, TextWriter log)
    {
        _home = home;
        _log = log;
        _saved = saved;
        _savePlacement = savePlacement;
        Title = "SemanticRouter";
        Icon = icon;
        CanResize = true;
        WindowDecorations = Avalonia.Controls.WindowDecorations.Full;
        WindowStartupLocation = WindowStartupLocation.Manual;

        // Remember the normal (not maximized) bounds, which are what a maximized window returns to.
        PositionChanged += (_, _) => TrackNormalBounds();
        Resized += (_, _) => TrackNormalBounds();

        _view = new NativeWebView { Source = home };
        _view.EnvironmentRequested += (_, e) =>
        {
            // Keep the web view's profile with the router's files, not next to the program (which may be read-only).
            switch (e)
            {
                case WindowsWebView2EnvironmentRequestedEventArgs webView2:
                    webView2.UserDataFolder = Path.Combine(dataDirectory, "WebView2");
                    break;
                case LinuxWpeWebViewEnvironmentRequestedEventArgs wpe:
                    wpe.DataDirectory = Path.Combine(dataDirectory, "data");
                    wpe.CacheDirectory = Path.Combine(dataDirectory, "cache");
                    break;
                case GtkWebViewEnvironmentRequestedEventArgs gtk:
                    gtk.BaseDataDirectory = Path.Combine(dataDirectory, "data");
                    gtk.BaseCacheDirectory = Path.Combine(dataDirectory, "cache");
                    break;
            }
        };
        _view.NewWindowRequested += (_, e) =>
        {
            if (e.Request is { } target)
            {
                if (IsSameRouter(target, _home))
                {
                    _view.Navigate(target);
                }
                else
                {
                    OpenInBrowser(target, "a new window");
                }
            }

            e.Handled = true;
        };
        _view.NavigationStarted += (_, e) =>
        {
            if (e.Request is { Scheme: "http" or "https" } target && !IsSameRouter(target, _home))
            {
                e.Cancel = true;
                OpenInBrowser(target, "a navigation");
            }
        };
        _view.WebMessageReceived += (_, e) =>
        {
            if (DesktopBridge.Parse(e.Body) is { } action)
            {
                PageAction?.Invoke(action);
            }
        };

        // A page loaded afresh (a reload, the router moved) has none of the app's state yet.
        _view.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess)
            {
                Send();
            }
        };
        Content = _view;
    }

    /// <summary>The dashboard asked something of the app (<see cref="DesktopPageAction.Ready"/>: it wants the state again).</summary>
    public event Action<DesktopPageAction>? PageAction;

    /// <summary>Hands <paramref name="state"/> to the page, when it differs from what the page last got.</summary>
    public void Push(DesktopPageState state)
    {
        if (state != _state)
        {
            _state = state;
            Send();
        }
    }

    /// <summary>Hands the page the last state again (it loaded afresh, or asked for it).</summary>
    public void Resend() => Send();

    private async void Send()
    {
        if (_state is null)
        {
            return;
        }

        try
        {
            await _view.InvokeScript(DesktopBridge.Script(_state));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No page yet, or one going away: it asks again ("ready") when it has loaded. Never take the app down for it.
            _log.WriteLine($"Dashboard window: could not hand the page the app's state: {ex.Message}");
        }
    }

    /// <summary>Brings the window up on <paramref name="home"/>, reloading only when the router moved.</summary>
    public void ShowAt(Uri home)
    {
        if (home != _home)
        {
            _home = home;
            _view.Navigate(home);
        }

        if (!_placed)
        {
            Place();
        }

        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = _saved?.Maximized == true ? WindowState.Maximized : WindowState.Normal;
        }

        Activate();

        // Into the page, so Tab and a screen reader's browse mode start there rather than on the window's frame.
        _view.Focus();
    }

    /// <summary>Sizes and positions the window for the screens there are now, before it is first shown.</summary>
    private void Place()
    {
        _placed = true;
        ScreenArea[] screens = [.. Screens.All.Select(Area)];
        ScreenArea primary = Screens.Primary is { } p ? Area(p) : screens.FirstOrDefault(new ScreenArea(0, 0, 1280, 800, 1));
        WindowPlacement placement = WindowPlacement.Choose(_saved, screens, primary);
        ScreenArea screen = screens.FirstOrDefault(s => s.Contains(placement.X, placement.Y), primary);

        MinWidth = Math.Min(WindowPlacement.MinimumWidth, screen.Width / screen.Scaling);
        MinHeight = Math.Min(WindowPlacement.MinimumHeight, screen.Height / screen.Scaling);
        Width = placement.Width;
        Height = placement.Height;
        Position = new PixelPoint(placement.X, placement.Y);
        _normal = placement with { Maximized = false };
        if (placement.Maximized)
        {
            WindowState = WindowState.Maximized;
        }

        static ScreenArea Area(Screen s) => new(s.WorkingArea.X, s.WorkingArea.Y, s.WorkingArea.Width, s.WorkingArea.Height, s.Scaling);
    }

    private void TrackNormalBounds()
    {
        if (_placed && WindowState == WindowState.Normal && IsVisible)
        {
            _normal = new WindowPlacement(Position.X, Position.Y, Bounds.Width, Bounds.Height);
        }
    }

    private void SavePlacement()
    {
        if (_normal is { } normal)
        {
            _savePlacement(normal with { Maximized = WindowState == WindowState.Maximized });
        }
    }

    /// <summary>Closes the window for good (the app is quitting).</summary>
    public void CloseForGood()
    {
        if (IsVisible)
        {
            SavePlacement();
        }

        _closing = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closing)
        {
            e.Cancel = true;
            SavePlacement();
            Hide();
        }

        base.OnClosing(e);
    }

    /// <summary>
    /// Whether <paramref name="target"/> is on the router at <paramref name="home"/>: the same scheme and port, and the
    /// same host, where localhost, 127.0.0.1 and [::1] all count as this machine.
    /// </summary>
    internal static bool IsSameRouter(Uri target, Uri home) =>
        string.Equals(target.Scheme, home.Scheme, StringComparison.OrdinalIgnoreCase)
        && target.Port == home.Port
        && (string.Equals(target.IdnHost, home.IdnHost, StringComparison.OrdinalIgnoreCase) || (target.IsLoopback && home.IsLoopback));

    /// <summary>Hands a link to the default browser, and notes why in the app's log (a router link should never get here).</summary>
    private void OpenInBrowser(Uri target, string how)
    {
        _log.WriteLine($"Dashboard window: opened {target.GetLeftPart(UriPartial.Path)} in the browser ({how}; the router is {_home.GetLeftPart(UriPartial.Authority)}).");
        _log.Flush();
        Shell.Open(target.AbsoluteUri);
    }
}
