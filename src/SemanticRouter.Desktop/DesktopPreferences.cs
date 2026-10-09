using System.Text.Json;
using System.Text.Json.Serialization;

namespace SemanticRouter.Desktop;

/// <summary>Where the dashboard opens: in the app's own window (the platform web view) or in the default browser.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DashboardSurface>))]
internal enum DashboardSurface
{
    Window,
    Browser,
}

/// <summary>The desktop app's own settings, in <c>desktop.json</c> next to the router's configuration.</summary>
internal sealed record DesktopPreferences
{
    public const string FileName = "desktop.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public DashboardSurface OpenDashboardIn { get; init; } = DashboardSurface.Window;

    /// <summary>Whether the welcome window (background service, start at sign-in) has been shown.</summary>
    public bool Welcomed { get; init; }

    /// <summary>Whether an installed app looks for updates (at start and twice a day). Default on.</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>Where the dashboard window was when it was last hidden.</summary>
    public WindowPlacement? Window { get; init; }

    public static DesktopPreferences Load(string configurationDirectory)
    {
        try
        {
            return JsonSerializer.Deserialize<DesktopPreferences>(File.ReadAllText(Path.Combine(configurationDirectory, FileName)), Json) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save(string configurationDirectory)
    {
        Directory.CreateDirectory(configurationDirectory);
        File.WriteAllText(Path.Combine(configurationDirectory, FileName), JsonSerializer.Serialize(this, Json));
    }
}
