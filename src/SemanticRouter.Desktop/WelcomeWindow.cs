using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace SemanticRouter.Desktop;

/// <summary>What the person chose in the welcome window; <see langword="null"/> when they closed it without choosing.</summary>
internal sealed record WelcomeChoice(bool KeepRunning, bool StartAtSignIn);

/// <summary>
/// Shown once, on the first start by hand: whether to keep the router running in the background (the service) and
/// whether to start the app at sign-in. Both are on unless unavailable; both can be changed later in the tray menu or
/// Settings. Escape or closing the window chooses neither.
/// </summary>
internal sealed class WelcomeWindow : Window
{
    private readonly TaskCompletionSource<WelcomeChoice?> _choice = new();

    public WelcomeWindow(WindowIcon icon, Bitmap logo, bool serviceAvailable, bool serviceOn, bool signInAvailable)
    {
        Title = "Welcome to SemanticRouter";
        Icon = icon;
        Width = 480;
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

        const string KeepRunningHelp = "The router then starts when you sign in, restarts if it fails, and keeps answering when this app is closed.";
        var keepRunning = new CheckBox
        {
            Content = new TextBlock
            {
                Text = serviceOn ? "Keep the router running in the background (it already does)"
                    : serviceAvailable ? "Keep the router running in the background (recommended)"
                    : "Keep the router running in the background (not available on this system)",
                TextWrapping = TextWrapping.Wrap,
            },
            IsChecked = serviceAvailable,
            IsEnabled = serviceAvailable && !serviceOn,
        };
        var signIn = new CheckBox
        {
            Content = new TextBlock { Text = "Show SemanticRouter in the tray when I sign in", TextWrapping = TextWrapping.Wrap },
            IsChecked = signInAvailable,
            IsEnabled = signInAvailable,
        };

        // The explanation under each box is read with it, not skipped when tabbing from box to box.
        AutomationProperties.SetHelpText(keepRunning, serviceAvailable ? KeepRunningHelp : "This system has no service manager the app can use.");
        AutomationProperties.SetHelpText(signIn, signInAvailable ? "The tray icon comes back after you sign in; quitting it does not stop a background router." : "Not available on this system.");
        var start = new Button { Content = "Get started", HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        start.Click += (_, _) =>
        {
            _choice.TrySetResult(new WelcomeChoice(keepRunning.IsChecked == true && !serviceOn, signIn.IsChecked == true));
            Close();
        };
        Closed += (_, _) => _choice.TrySetResult(null);

        Content = Accessible.FitToScreen(this, new StackPanel
        {
            Margin = new Thickness(28, 24),
            Spacing = 12,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 16,
                    Children =
                    {
                        Accessible.Decorative(new Image { Source = logo, Width = 48, Height = 48 }),
                        Accessible.Heading(new TextBlock { Text = "Welcome to SemanticRouter", FontSize = 20, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center }, 1),
                    },
                },
                new TextBlock
                {
                    Text = "Point Claude Code, Codex or any OpenAI or Anthropic client at the router, and it sends every prompt to the cheapest model that can answer it.",
                    TextWrapping = TextWrapping.Wrap,
                },
                keepRunning,
                new TextBlock
                {
                    Text = KeepRunningHelp,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = Accessible.SecondaryOpacity,
                    Margin = new Thickness(28, -8, 0, 0),
                },
                signIn,
                new TextBlock
                {
                    Text = "Both can be changed any time from the tray icon's menu, or in Settings (under More on the dashboard).",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = Accessible.SecondaryOpacity,
                },
                new TextBlock
                {
                    Text = "Next, the dashboard opens on its Overview page. Set up SemanticRouter there: enable Claude Code or another CLI it found, " +
                           "or add LM Studio, Ollama or an API key, and send a test prompt.",
                    TextWrapping = TextWrapping.Wrap,
                },
                start,
            },
        });
    }

    /// <summary>Completes when the window closes: the choice, or <see langword="null"/> if it was closed without one.</summary>
    public Task<WelcomeChoice?> Choice => _choice.Task;
}
