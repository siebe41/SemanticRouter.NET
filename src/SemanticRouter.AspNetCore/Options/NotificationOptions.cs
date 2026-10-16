using System.Text.Json.Serialization;

namespace SemanticRouter.AspNetCore;

/// <summary>
/// Notifications (the <c>Notifications</c> section): the router speaks up only when a person needs to decide something
/// (<see cref="NoticeKind"/>). Notices are listed on the dashboard either way; this decides whether the desktop app
/// shows a notification, the service writes a log line and the <see cref="Webhooks"/> get a message.
/// </summary>
public sealed class NotificationOptions
{
    /// <summary>The one switch: <see langword="false"/> silences notifications. Default <see langword="true"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>A connector whose every request failed for this long is down. Default one hour.</summary>
    public TimeSpan ConnectorDownAfter { get; set; } = TimeSpan.FromHours(1);

    /// <summary>A subscription that hit its usage limit on this many of the last 7 days keeps hitting it. Default 3.</summary>
    public int LimitDays { get; set; } = 3;

    /// <summary>
    /// Where to send notices and the weekly digest, by name: a URL and its format (JSON, Slack-compatible, ntfy). The URLs
    /// are secrets: masked on the dashboard and movable to the secret store, like API keys.
    /// </summary>
    public IDictionary<string, WebhookOptions> Webhooks { get; } = new Dictionary<string, WebhookOptions>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The dashboard's address as people reach it (e.g. <c>http://router.lan:5080/router</c>), for links in webhook messages.</summary>
    public string? DashboardUrl { get; set; }

    /// <summary>
    /// Webhook messages quote text that can come from prompts or replies: utterances learned from prompts and the latest
    /// error a connector returned. Default <see langword="false"/>: messages carry only counts, names, kinds and links.
    /// </summary>
    public bool IncludeDetails { get; set; }

    /// <summary>Validation errors; empty when valid.</summary>
    internal IEnumerable<string> GetValidationErrors()
    {
        if (ConnectorDownAfter < TimeSpan.FromMinutes(1))
        {
            yield return "Notifications:ConnectorDownAfter must be at least one minute.";
        }

        if (LimitDays is < 1 or > 7)
        {
            yield return "Notifications:LimitDays must be from 1 to 7.";
        }

        if (DashboardUrl is { Length: > 0 } dashboard && !IsHttpUrl(dashboard))
        {
            yield return "Notifications:DashboardUrl must be an http or https address.";
        }

        foreach ((string name, WebhookOptions hook) in Webhooks)
        {
            if (string.IsNullOrWhiteSpace(hook.Url) || (!SecretReference.ContainsReference(hook.Url) && hook.Url != RouterConfiguration.MaskedSecret && !IsHttpUrl(hook.Url)))
            {
                yield return $"Notifications:Webhooks:{name}:Url must be an http or https address (or a ${{secret:…}} reference).";
            }
        }
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https";
}

/// <summary>How a webhook message is written (<see cref="WebhookOptions.Format"/>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WebhookFormat>))]
public enum WebhookFormat
{
    /// <summary>A JSON object: <c>type</c> (notice or digest), and the notice or the digest's figures.</summary>
    Json,

    /// <summary>Slack's incoming-webhook shape (<c>{"text": …}</c>), which Mattermost, Discord's <c>/slack</c> endpoint and many others accept.</summary>
    Slack,

    /// <summary>An ntfy topic URL: the text as the body, the title, tags and link as headers.</summary>
    Ntfy,
}

/// <summary>One webhook (<see cref="NotificationOptions.Webhooks"/>).</summary>
public sealed class WebhookOptions
{
    /// <summary>The address to POST to. A secret (it often carries a token).</summary>
    public string? Url { get; set; }

    /// <summary>How the message is written. Default <see cref="WebhookFormat.Json"/>.</summary>
    public WebhookFormat Format { get; set; } = WebhookFormat.Json;

    /// <summary>Send notices (when one appears and when it clears). Default <see langword="true"/>.</summary>
    public bool Notices { get; set; } = true;

    /// <summary>Send the weekly digest. Default <see langword="true"/>.</summary>
    public bool Digest { get; set; } = true;
}
