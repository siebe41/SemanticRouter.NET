using System.Globalization;
using System.Text.RegularExpressions;

namespace SemanticRouter;

/// <summary>
/// Reads when a usage limit ends from the message a provider or CLI printed, so the model cools down until then
/// instead of for a fixed time: "You've hit your session limit · resets 3:45pm", "resets 3pm (Europe/Amsterdam)",
/// "resets Oct 9, 3:00 PM", "try again in 2 hours 30 minutes", "Please try again in 4h12m". Times without a date are
/// the next such time; times without a zone are in <c>localZone</c>.
/// </summary>
public static partial class QuotaResetParser
{
    /// <summary>Finds a reset time in <paramref name="message"/>, relative to <paramref name="now"/>.</summary>
    public static bool TryParse(string? message, DateTimeOffset now, TimeZoneInfo? localZone, out DateTimeOffset resetAt)
    {
        resetAt = default;
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        localZone ??= TimeZoneInfo.Local;
        if (Relative().Match(message) is { Success: true } relative && SumDuration(relative.Groups["duration"].Value) is { } duration && duration > TimeSpan.Zero)
        {
            resetAt = now + duration;
            return true;
        }

        if (Absolute().Match(message) is not { Success: true } absolute)
        {
            return false;
        }

        if (!absolute.Groups["minute"].Success && !absolute.Groups["ampm"].Success)
        {
            return false; // a bare number ("resets 3 counters") is not a time
        }

        TimeZoneInfo zone = localZone;
        if (absolute.Groups["zone"].Success)
        {
            string id = absolute.Groups["zone"].Value.Trim();
            if (id is "UTC" or "GMT")
            {
                zone = TimeZoneInfo.Utc;
            }
            else if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out TimeZoneInfo? named))
            {
                zone = named;
            }
        }

        int hour = int.Parse(absolute.Groups["hour"].Value, CultureInfo.InvariantCulture);
        int minute = absolute.Groups["minute"].Success ? int.Parse(absolute.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
        string meridiem = absolute.Groups["ampm"].Value.ToUpperInvariant();
        if (meridiem.StartsWith('P') && hour < 12)
        {
            hour += 12;
        }
        else if (meridiem.StartsWith('A') && hour == 12)
        {
            hour = 0;
        }

        if (hour > 23 || minute > 59)
        {
            return false;
        }

        DateTime localNow = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        DateTime candidate;
        if (absolute.Groups["month"].Success)
        {
            if (!DateTime.TryParseExact(absolute.Groups["month"].Value[..3], "MMM", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime monthOnly))
            {
                return false;
            }

            int day = int.Parse(absolute.Groups["day"].Value, CultureInfo.InvariantCulture);
            if (day > DateTime.DaysInMonth(localNow.Year, monthOnly.Month))
            {
                return false;
            }

            candidate = new DateTime(localNow.Year, monthOnly.Month, day, hour, minute, 0, DateTimeKind.Unspecified);
            if (candidate < localNow.AddDays(-1))
            {
                candidate = candidate.AddYears(1); // "resets Jan 2" read on Dec 30
            }
        }
        else
        {
            candidate = new DateTime(localNow.Year, localNow.Month, localNow.Day, hour, minute, 0, DateTimeKind.Unspecified);
            if (candidate <= localNow)
            {
                candidate = candidate.AddDays(1);
            }
        }

        resetAt = new DateTimeOffset(candidate, zone.GetUtcOffset(candidate)).ToUniversalTime();
        return resetAt > now;
    }

    /// <summary>Reads an HTTP <c>Retry-After</c> value: seconds, or an HTTP date.</summary>
    public static bool TryParseRetryAfter(string? value, DateTimeOffset now, out DateTimeOffset resetAt)
    {
        resetAt = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) && seconds > 0)
        {
            resetAt = now.AddSeconds(seconds);
            return true;
        }

        if (DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset date) && date > now)
        {
            resetAt = date.ToUniversalTime();
            return true;
        }

        return false;
    }

    private static TimeSpan? SumDuration(string text)
    {
        TimeSpan total = TimeSpan.Zero;
        bool any = false;
        foreach (Match part in DurationPart().Matches(text))
        {
            double amount = double.Parse(part.Groups["n"].Value, CultureInfo.InvariantCulture);
            string unit = part.Groups["unit"].Value.ToUpperInvariant();
            total += unit[0] switch
            {
                'D' => TimeSpan.FromDays(amount),
                'H' => TimeSpan.FromHours(amount),
                'S' => TimeSpan.FromSeconds(amount),
                _ => unit.StartsWith("MS", StringComparison.Ordinal) ? TimeSpan.FromMilliseconds(amount) : TimeSpan.FromMinutes(amount),
            };
            any = true;
        }

        return any ? total : null;
    }

    [GeneratedRegex(
        @"(?:try again|retry|resets?|available again)\s+(?:after|in)\s+(?<duration>(?:\d+(?:\.\d+)?\s*(?:days?|d|hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)(?![a-z])[\s,]*(?:and\s+)?)+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Relative();

    [GeneratedRegex(@"(?<n>\d+(?:\.\d+)?)\s*(?<unit>days?|d|hours?|hrs?|h|minutes?|mins?|ms|m|seconds?|secs?|s)(?![a-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DurationPart();

    [GeneratedRegex(
        @"\bresets?\s+(?:on\s+|at\s+)?(?:(?<month>jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*\.?\s+(?<day>\d{1,2})(?:st|nd|rd|th)?,?\s+(?:at\s+)?)?(?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<ampm>[ap]\.?m\.?)?(?:\s*\((?<zone>[A-Za-z_/+\-0-9]+)\))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Absolute();
}
