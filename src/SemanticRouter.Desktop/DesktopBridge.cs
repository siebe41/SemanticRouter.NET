using System.Text.Json;
using System.Text.Json.Serialization;

namespace SemanticRouter.Desktop;

/// <summary>What the dashboard in the app window asks of the app.</summary>
internal enum DesktopPageAction
{
    /// <summary>The page has loaded and wants the app's state.</summary>
    Ready,

    /// <summary>Open the About window.</summary>
    About,

    /// <summary>Restart to update, as in the tray menu.</summary>
    RestartToUpdate,
}

/// <summary>
/// What the app tells the dashboard in its window: where updates stand, for the update banner. Only a dashboard in the
/// app window gets it; in a browser the dashboard shows neither the banner nor the About entry.
/// </summary>
internal sealed record DesktopPageState(
    [property: JsonPropertyName("offer")] string? Offer,
    [property: JsonPropertyName("downloading")] bool Downloading,
    [property: JsonPropertyName("headline")] string Headline,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("canRestart")] bool CanRestart)
{
    /// <summary>The state for an <see cref="UpdateStatus"/>: an offer only when there is one to restart for, or one downloading.</summary>
    public static DesktopPageState Of(UpdateStatus status, string? offer, bool downloading) =>
        new(status.CanRestart || downloading ? offer : null, downloading && offer is not null, status.Headline, status.Detail, status.CanRestart);
}

/// <summary>
/// The messages between the app and the dashboard in its window. The page posts <c>{"action": "about"}</c> and the like
/// through the web view's <c>invokeCSharpAction</c>; the app hands the page its state by running
/// <see cref="Script"/>, which the dashboard's <c>desktop.js</c> picks up.
/// </summary>
internal static class DesktopBridge
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The action in a message from the page, or <see langword="null"/> for anything else.</summary>
    public static DesktopPageAction? Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 1024)
        {
            return null;
        }

        try
        {
            using JsonDocument message = JsonDocument.Parse(body);
            return message.RootElement.ValueKind == JsonValueKind.Object
                && message.RootElement.TryGetProperty("action", out JsonElement action)
                && action.ValueKind == JsonValueKind.String
                ? action.GetString() switch
                {
                    "ready" => DesktopPageAction.Ready,
                    "about" => DesktopPageAction.About,
                    "restart-to-update" => DesktopPageAction.RestartToUpdate,
                    _ => null,
                }
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The script that hands <paramref name="state"/> to the page: kept on <c>window</c> for a page still loading, and
    /// announced as the <c>semantic-router-desktop</c> event for one that has.
    /// </summary>
    public static string Script(DesktopPageState state)
    {
        // System.Text.Json escapes <, >, &, quotes and non-ASCII, so the JSON is a safe JavaScript literal.
        string json = JsonSerializer.Serialize(state, Json);
        return $"window.semanticRouterDesktop = {json}; window.dispatchEvent(new CustomEvent('semantic-router-desktop', {{ detail: window.semanticRouterDesktop }}));";
    }
}
