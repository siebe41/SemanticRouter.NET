using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SemanticRouter.Host;

namespace SemanticRouter.Desktop;

/// <summary>Name, version and where the project lives: the repository, the docs, issues, releases and the licence.</summary>
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

        Content = new StackPanel
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
                        new Image { Source = logo, Width = 64, Height = 64 },
                        new StackPanel
                        {
                            VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                new TextBlock { Text = "SemanticRouter", FontSize = 22, FontWeight = FontWeight.SemiBold },
                                new SelectableTextBlock { Text = $"Version {RouterHost.Version}", Opacity = 0.75 },
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
                Link("GitHub repository", ProjectLinks.Repository),
                Link("Documentation", ProjectLinks.Documentation),
                Link("Report an issue or suggest an idea", ProjectLinks.Issues),
                Link("Releases and downloads", ProjectLinks.Releases),
                new TextBlock
                {
                    Text = "Open source under the MIT licence. The connectors drive third-party services under those services' own terms.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Opacity = 0.75,
                    Margin = new Thickness(0, 6, 0, 0),
                },
                Link("MIT licence", ProjectLinks.License),
                close,
            },
        };
    }

    private static HyperlinkButton Link(string text, string url)
    {
        var link = new HyperlinkButton { Content = text, Padding = new Thickness(0, 2) };
        ToolTip.SetTip(link, url);
        link.Click += (_, _) => Shell.Open(url);
        return link;
    }
}
