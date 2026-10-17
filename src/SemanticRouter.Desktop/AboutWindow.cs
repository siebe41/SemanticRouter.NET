using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SemanticRouter.Host;

namespace SemanticRouter.Desktop;

/// <summary>
/// Name, version, where updates stand (<see cref="UpdatesSection"/>) and where the project lives: the repository, the
/// docs, issues, releases and the licence.
/// </summary>
internal sealed class AboutWindow : Window
{
    public AboutWindow(WindowIcon icon, Bitmap logo)
    {
        Title = "About SemanticRouter";
        Icon = icon;
        Width = 440;
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

        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true };
        close.Click += (_, _) => Close();

        Content = Accessible.FitToScreen(this, new StackPanel
        {
            Margin = new Thickness(28, 24),
            Spacing = 10,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 16,
                    Children =
                    {
                        Accessible.Decorative(new Image { Source = logo, Width = 64, Height = 64 }),
                        new StackPanel
                        {
                            VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                Accessible.Heading(new TextBlock { Text = "SemanticRouter", FontSize = 22, FontWeight = FontWeight.SemiBold }, 1),
                                new SelectableTextBlock { Text = $"Version {RouterHost.Version}", Opacity = Accessible.SecondaryOpacity },
                            },
                        },
                    },
                },
                new TextBlock
                {
                    Text = "Sends every prompt to the cheapest model that can answer it: local models, your subscription CLIs or paid APIs.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 6, 0, 4),
                },
                Updates,
                new Separator { Margin = new Thickness(0, 6) },
                Accessible.Link("GitHub repository", ProjectLinks.Repository),
                Accessible.Link("Documentation", ProjectLinks.Documentation),
                Accessible.Link("Report an issue or suggest an idea", ProjectLinks.Issues),
                Accessible.Link("Releases and downloads", ProjectLinks.Releases),
                new TextBlock
                {
                    Text = "Open source under the MIT licence. The connectors drive third-party services under those services' own terms.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = Accessible.SecondaryOpacity,
                    Margin = new Thickness(0, 6, 0, 0),
                },
                Accessible.Link("MIT licence", ProjectLinks.License),
                close,
            },
        });
    }

    /// <summary>Where updates stand, and the choices about them.</summary>
    public UpdatesSection Updates { get; } = new();
}
