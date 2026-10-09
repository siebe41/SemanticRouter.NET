namespace SemanticRouter.AspNetCore;

/// <summary>
/// Notifications (the <c>Notifications</c> section): the router speaks up only when a person needs to decide something
/// (<see cref="NoticeKind"/>). Notices are listed on the dashboard either way; this decides whether the desktop app
/// shows a notification and the service writes a log line.
/// </summary>
public sealed class NotificationOptions
{
    /// <summary>The one switch: <see langword="false"/> silences notifications. Default <see langword="true"/>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>A connector whose every request failed for this long is down. Default one hour.</summary>
    public TimeSpan ConnectorDownAfter { get; set; } = TimeSpan.FromHours(1);

    /// <summary>A subscription that hit its usage limit on this many of the last 7 days keeps hitting it. Default 3.</summary>
    public int LimitDays { get; set; } = 3;

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
    }
}
