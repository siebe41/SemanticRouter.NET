using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace SemanticRouter.Desktop;

/// <summary>What the Settings window shows: the router's status and what last went wrong, and the app's own settings.</summary>
internal sealed record SettingsState(
    string Status,
    string? Problem,
    bool CanRestart,
    bool ServiceOn,
    bool ServiceAvailable,
    bool SignInOn,
    bool SignInAvailable,
    DashboardSurface Surface,
    bool WindowAvailable,
    string WindowRequirement,
    bool Switching,
    string? ServiceProgram = null);

/// <summary>
/// The tray menu's settings and actions in a window, for whoever cannot reach the tray: a keyboard or screen-reader
/// user (a tray icon is awkward to reach), or a Linux desktop that shows no tray icons. Opened from the tray, from
/// More on the dashboard in the app window, and from the macOS app menu.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _problem = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold, IsVisible = false };
    private readonly Button _restart = new() { Content = "Restart router" };
    private readonly CheckBox _background = new() { Content = new TextBlock { Text = "Keep the router running in the background", TextWrapping = TextWrapping.Wrap } };
    private readonly CheckBox _signIn = new() { Content = new TextBlock { Text = "Start at sign-in (show SemanticRouter in the tray)", TextWrapping = TextWrapping.Wrap } };
    private readonly RadioButton _inWindow = new() { GroupName = "surface" };
    private readonly RadioButton _inBrowser = new() { GroupName = "surface", Content = "Web browser" };

    public SettingsWindow(WindowIcon icon)
    {
        Title = "SemanticRouter settings";
        Icon = icon;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };

        // The status and a problem change while the window is open: screen readers say so; a problem more urgently.
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_problem, AutomationLiveSetting.Assertive);
        AutomationProperties.SetHelpText(_background, "The router then starts when you sign in, restarts if it fails, and keeps answering when this app is closed.");
        _restart.Click += (_, _) => RestartRequested?.Invoke();
        _background.Click += (_, _) => BackgroundChanged?.Invoke(_background.IsChecked == true);
        _signIn.Click += (_, _) => SignInChanged?.Invoke(_signIn.IsChecked == true);
        _inWindow.Click += (_, _) => SurfaceChanged?.Invoke(DashboardSurface.Window);
        _inBrowser.Click += (_, _) => SurfaceChanged?.Invoke(DashboardSurface.Browser);
        var log = new Button { Content = "Show the log" };
        log.Click += (_, _) => ShowLogRequested?.Invoke();
        var folder = new Button { Content = "Show the configuration folder" };
        folder.Click += (_, _) => ShowFolderRequested?.Invoke();
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true };
        close.Click += (_, _) => Close();

        var surfaceLabel = new TextBlock { Text = "Open the dashboard in", Margin = new Thickness(0, 6, 0, 0) };
        var surfaces = new StackPanel { Spacing = 2, Margin = new Thickness(12, 0, 0, 0), Children = { _inWindow, _inBrowser } };
        AutomationProperties.SetLabeledBy(surfaces, surfaceLabel);

        Content = Accessible.FitToScreen(this, new StackPanel
        {
            Margin = new Thickness(28, 24),
            Spacing = 8,
            Children =
            {
                Accessible.Heading(new TextBlock { Text = "Settings", FontSize = 20, FontWeight = FontWeight.SemiBold }, 1),
                Accessible.Heading(new TextBlock { Text = "Router", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) }, 2),
                _status,
                _problem,
                _restart,
                _background,
                Accessible.Heading(new TextBlock { Text = "This app", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 10, 0, 0) }, 2),
                _signIn,
                surfaceLabel,
                surfaces,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0), Children = { log, folder } },
                new TextBlock
                {
                    Text = "Updates are in About SemanticRouter.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = Accessible.SecondaryOpacity,
                },
                close,
            },
        });
    }

    public event Action? RestartRequested;

    public event Action<bool>? BackgroundChanged;

    public event Action<bool>? SignInChanged;

    public event Action<DashboardSurface>? SurfaceChanged;

    public event Action? ShowLogRequested;

    public event Action? ShowFolderRequested;

    public void Show(SettingsState state)
    {
        _status.Text = state.Status;
        // Said in words, not in a colour: the theme's own text colour keeps its contrast in light, dark and high contrast.
        _problem.Text = state.Problem is { } problem ? "Problem: " + problem : null;
        _problem.IsVisible = state.Problem is not null;
        _restart.IsEnabled = state.CanRestart;
        _background.IsChecked = state.ServiceOn;
        _background.Content = new TextBlock { Text = RouterService.BackgroundLabel(state.ServiceProgram), TextWrapping = TextWrapping.Wrap };
        _background.IsEnabled = state.ServiceAvailable && !state.Switching;
        _signIn.IsChecked = state.SignInOn;
        _signIn.IsEnabled = state.SignInAvailable;
        _inWindow.Content = new TextBlock { Text = state.WindowAvailable ? "App window" : $"App window ({state.WindowRequirement})", TextWrapping = TextWrapping.Wrap };
        _inWindow.IsEnabled = state.WindowAvailable;
        _inWindow.IsChecked = state.Surface == DashboardSurface.Window;
        _inBrowser.IsChecked = state.Surface == DashboardSurface.Browser;
    }
}
