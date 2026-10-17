using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform;

namespace SemanticRouter.Desktop;

/// <summary>
/// What the app's own windows share so screen readers, the keyboard and large text work in them: headings, decorative
/// images, links that read as links, secondary text that keeps full contrast under the platform's high-contrast
/// setting, and content that scrolls rather than running off a short screen.
/// </summary>
internal static class Accessible
{
    /// <summary>Whether the platform asks for high contrast (Windows contrast themes, macOS Increase contrast).</summary>
    public static bool HighContrast => Application.Current?.PlatformSettings?.GetColorValues().ContrastPreference == ColorContrastPreference.High;

    /// <summary>The opacity of secondary text: dimmed, except under high contrast, where it is as strong as the rest.</summary>
    public static double SecondaryOpacity => HighContrast ? 1 : 0.75;

    /// <summary>Marks <paramref name="control"/> as a heading of <paramref name="level"/>, for screen readers' heading navigation.</summary>
    public static T Heading<T>(T control, int level)
        where T : Control
    {
        AutomationProperties.SetHeadingLevel(control, level);
        return control;
    }

    /// <summary>Hides an image that only decorates (the logo) from screen readers, which would read it as an unnamed image.</summary>
    public static Image Decorative(Image image)
    {
        AutomationProperties.SetAccessibilityView(image, AccessibilityView.Raw);
        return image;
    }

    /// <summary>A link that opens <paramref name="url"/>: read as a link (not a button), with where it goes.</summary>
    public static HyperlinkButton Link(string text, string url)
    {
        var link = new HyperlinkButton { Content = text, Padding = new Thickness(0, 2) };
        ToolTip.SetTip(link, url);
        AutomationProperties.SetControlTypeOverride(link, AutomationControlType.Hyperlink);
        AutomationProperties.SetHelpText(link, url);
        link.Click += (_, _) => Shell.Open(url);
        return link;
    }

    /// <summary>
    /// Puts a fixed-width window's <paramref name="content"/> in a scroll viewer, and keeps the window within the screen
    /// it opens on: with large text a window sized to its content would otherwise run past the bottom of the screen and
    /// take its buttons with it.
    /// </summary>
    public static ScrollViewer FitToScreen(Window window, Control content)
    {
        window.Opened += (_, _) =>
        {
            if (window.Screens.ScreenFromWindow(window) is { } screen)
            {
                window.MaxHeight = screen.WorkingArea.Height / screen.Scaling;
            }
        };
        return new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
    }
}
