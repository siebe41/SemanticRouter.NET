using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace SemanticRouter.Desktop;

/// <summary>
/// The dashboard in the platform's own web view. Links that leave the router open in the default browser. Closing the
/// window hides it: the router keeps running in the tray. It opens sized to fit the screen, or where it was last.
/// </summary>
internal sealed class DashboardWindow : Window
{
    private readonly NativeWebView _view;
    private readonly WindowPlacement? _saved;
    private readonly Action<WindowPlacement> _savePlacement;
    private Uri _home;
    private bool _closing;
    private bool _placed;
    private WindowPlacement? _normal;

    public DashboardWindow(Uri home, string dataDirectory, WindowIcon icon, WindowPlacement? saved, Action<WindowPlacement> savePlacement)
    {
        _home = home;
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
                Shell.Open(target.AbsoluteUri);
            }

            e.Handled = true;
        };
        _view.NavigationStarted += (_, e) =>
        {
            if (e.Request is { Scheme: "http" or "https" } target && !IsRouter(target))
            {
                e.Cancel = true;
                Shell.Open(target.AbsoluteUri);
            }
        };
        Content = _view;
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

    private bool IsRouter(Uri target) => Uri.Compare(target, _home, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
}
