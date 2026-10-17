using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace SemanticRouter.Desktop;

/// <summary>
/// The dashboard in the platform's own web view. Web links that leave the router open in the default browser, and
/// links of any other scheme are dropped; links to the router stay in the window, however the web view asks (a
/// navigation or a new window) and whichever name of this machine they use. Closing the window hides it: the router keeps running in the tray. It opens sized to fit the
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
                else if (ShouldOpenExternally(target))
                {
                    OpenInBrowser(target, "a new window");
                }
                else
                {
                    Refuse(target, "a new window");
                }
            }

            e.Handled = true;
        };
        _view.NavigationStarted += (_, e) =>
        {
            if (e.Request is not { } target)
            {
                return;
            }

            switch (Decide(target, _home))
            {
                case NavigationOutcome.OpenInBrowser:
                    e.Cancel = true;
                    OpenInBrowser(target, "a navigation");
                    break;
                case NavigationOutcome.Refuse:
                    e.Cancel = true;
                    Refuse(target, "a navigation");
                    break;
            }
        };
        _view.WebMessageReceived += (_, e) =>
        {
            // Only the router's own page speaks for the app. The web view's source is the page it last loaded, and a
            // navigation away from the router is cancelled above, so this refuses only what got past that.
            if (!IsSameRouter(_view.Source, _home))
            {
                _log.WriteLine($"Dashboard window: ignored a message from {Describe(_view.Source)}, which is not the router ({_home.GetLeftPart(UriPartial.Authority)}).");
                return;
            }

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
    internal static bool IsSameRouter(Uri? target, Uri home) =>
        target is { IsAbsoluteUri: true }
        && string.Equals(target.Scheme, home.Scheme, StringComparison.OrdinalIgnoreCase)
        && target.Port == home.Port
        && (string.Equals(target.IdnHost, home.IdnHost, StringComparison.OrdinalIgnoreCase) || (target.IsLoopback && home.IsLoopback));

    /// <summary>
    /// What the window does with a navigation to <paramref name="target"/>: the router's own pages, <c>about:blank</c>
    /// and a download the page made itself stay in the window, web links go to the default browser, and every other
    /// scheme is refused.
    /// </summary>
    internal static NavigationOutcome Decide(Uri target, Uri home)
    {
        if (IsSameRouter(target, home) || IsBlank(target) || IsOwnBlob(target, home))
        {
            return NavigationOutcome.Stay;
        }

        return ShouldOpenExternally(target) ? NavigationOutcome.OpenInBrowser : NavigationOutcome.Refuse;
    }

    /// <summary>
    /// Whether <paramref name="target"/> may go to the default browser: an absolute <c>https</c> or <c>http</c> URL
    /// with a host. Anything else (<c>file:</c>, a UNC path, <c>javascript:</c>, <c>data:</c>, <c>ms-msdt:</c>,
    /// <c>search-ms:</c> or another protocol handler) the operating system would hand to a program, so it is not opened.
    /// </summary>
    internal static bool ShouldOpenExternally(Uri target) =>
        target is { IsAbsoluteUri: true, IsUnc: false }
        && (target.Scheme == Uri.UriSchemeHttps || target.Scheme == Uri.UriSchemeHttp)
        && target.Host.Length > 0;

    private static bool IsBlank(Uri target) =>
        target.IsAbsoluteUri && string.Equals(target.AbsoluteUri, "about:blank", StringComparison.OrdinalIgnoreCase);

    // The dashboard downloads its configuration as a blob: URL of its own origin (blob:http://localhost:5080/<id>).
    private static bool IsOwnBlob(Uri target, Uri home) =>
        target.IsAbsoluteUri
        && target.Scheme == "blob"
        && Uri.TryCreate(target.AbsolutePath, UriKind.Absolute, out Uri? origin)
        && IsSameRouter(origin, home);

    /// <summary>What the log says about <paramref name="target"/>: a web URL without its query, or only the scheme of anything else.</summary>
    private static string Describe(Uri? target) => target switch
    {
        null or { IsAbsoluteUri: false } => "a page with no address",
        { Scheme: "http" or "https" } => target.GetLeftPart(UriPartial.Path),
        _ => $"a {target.Scheme}: address",
    };

    /// <summary>Hands a web link to the default browser, and notes why in the app's log (a router link should never get here).</summary>
    private void OpenInBrowser(Uri target, string how)
    {
        _log.WriteLine($"Dashboard window: opened {target.GetLeftPart(UriPartial.Path)} in the browser ({how}; the router is {_home.GetLeftPart(UriPartial.Authority)}).");
        _log.Flush();
        Shell.Open(target.AbsoluteUri);
    }

    /// <summary>Notes in the app's log that the window dropped <paramref name="target"/>, which is neither the router nor a web link.</summary>
    private void Refuse(Uri target, string how)
    {
        _log.WriteLine($"Dashboard window: refused {Describe(target)} ({how}): only web links leave the window.");
        _log.Flush();
    }
}

/// <summary>What the dashboard window does with a navigation (<see cref="DashboardWindow.Decide"/>).</summary>
internal enum NavigationOutcome
{
    /// <summary>Let it happen in the window: a page of the router, <c>about:blank</c> or the page's own download.</summary>
    Stay,

    /// <summary>Cancel it and open the link in the default browser: a web page elsewhere.</summary>
    OpenInBrowser,

    /// <summary>Cancel it: any other scheme, which the operating system would hand to a program.</summary>
    Refuse,
}
