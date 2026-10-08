using Avalonia.Controls;
using Avalonia.Platform;

namespace SemanticRouter.Desktop;

/// <summary>
/// The dashboard in the platform's own web view. Links that leave the router open in the default browser. Closing the
/// window hides it: the router keeps running in the tray.
/// </summary>
internal sealed class DashboardWindow : Window
{
    private readonly NativeWebView _view;
    private Uri _home;
    private bool _closing;

    public DashboardWindow(Uri home, string dataDirectory, WindowIcon icon)
    {
        _home = home;
        Title = "SemanticRouter";
        Icon = icon;
        Width = 1280;
        Height = 860;
        MinWidth = 420;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

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

        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <summary>Closes the window for good (the app is quitting).</summary>
    public void CloseForGood()
    {
        _closing = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private bool IsRouter(Uri target) => Uri.Compare(target, _home, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0;
}
