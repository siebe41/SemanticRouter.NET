namespace SemanticRouter.Desktop;

/// <summary>A screen's usable area (without the taskbar or menu bar) in physical pixels, and its scale factor.</summary>
internal readonly record struct ScreenArea(int X, int Y, int Width, int Height, double Scaling)
{
    public bool Contains(int x, int y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
}

/// <summary>
/// Where the dashboard window goes: its position in physical pixels, its size in device-independent units, and whether
/// it is maximized. Remembered in <c>desktop.json</c> when the window is hidden.
/// </summary>
internal sealed record WindowPlacement(int X, int Y, double Width, double Height, bool Maximized = false)
{
    /// <summary>The size the window wants on a screen with room to spare; the dashboard's wide layout starts at 1100.</summary>
    public const double PreferredWidth = 1280, PreferredHeight = 860;

    /// <summary>Below this the dashboard switches to its phone layout, which still works.</summary>
    public const double MinimumWidth = 420, MinimumHeight = 480;

    /// <summary>The share of a small screen's usable area the window takes when it cannot have its preferred size.</summary>
    private const double ScreenShare = 0.9;

    /// <summary>
    /// The saved placement if it still lands on a screen (a monitor may have been unplugged or its scaling changed),
    /// shrunk and moved so all of the window, title bar included, is visible; otherwise a size that fits the primary
    /// screen, centred on it.
    /// </summary>
    public static WindowPlacement Choose(WindowPlacement? saved, IReadOnlyList<ScreenArea> screens, ScreenArea primary)
    {
        if (saved is not null && screens.FirstOrDefault(s => s.Contains(saved.X + 40, saved.Y + 10)) is { Width: > 0 } screen)
        {
            return Fit(saved, screen);
        }

        double width = Math.Min(PreferredWidth, primary.Width / primary.Scaling * ScreenShare);
        double height = Math.Min(PreferredHeight, primary.Height / primary.Scaling * ScreenShare);
        return Fit(new WindowPlacement(
            primary.X + (int)((primary.Width - (width * primary.Scaling)) / 2),
            primary.Y + (int)((primary.Height - (height * primary.Scaling)) / 2),
            width,
            height,
            saved?.Maximized == true), primary);
    }

    /// <summary>Keeps <paramref name="placement"/> inside <paramref name="screen"/>: no larger than it, nowhere outside it.</summary>
    internal static WindowPlacement Fit(WindowPlacement placement, ScreenArea screen)
    {
        double maxWidth = screen.Width / screen.Scaling, maxHeight = screen.Height / screen.Scaling;
        double width = Math.Clamp(placement.Width, Math.Min(MinimumWidth, maxWidth), maxWidth);
        double height = Math.Clamp(placement.Height, Math.Min(MinimumHeight, maxHeight), maxHeight);
        // Rounding the scaled size up can make it a pixel wider than the screen: then the window starts at its edge.
        int x = Math.Clamp(placement.X, screen.X, Math.Max(screen.X, screen.X + screen.Width - (int)Math.Ceiling(width * screen.Scaling)));
        int y = Math.Clamp(placement.Y, screen.Y, Math.Max(screen.Y, screen.Y + screen.Height - (int)Math.Ceiling(height * screen.Scaling)));
        return new WindowPlacement(x, y, width, height, placement.Maximized);
    }
}
