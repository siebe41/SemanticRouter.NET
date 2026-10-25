using System.Globalization;

namespace SemanticRouter.Tests.Core;

/// <summary>
/// The edges of reading a reset time: midnight and noon, impossible times and dates, a date in the new year, an unknown
/// zone, and HTTP <c>Retry-After</c> dates. The common forms are in <c>UsageAndTelemetryTests</c>.
/// </summary>
public sealed class QuotaResetParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 13, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset At(string utc) => DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_message_is_no_reset(string? message)
    {
        QuotaResetParser.TryParse(message, Now, TimeZoneInfo.Utc, out DateTimeOffset reset).Should().BeFalse();
        reset.Should().Be(default);
    }

    [Theory]
    [InlineData("resets 12am", "2026-10-09T00:00:00Z")]
    [InlineData("resets 12:30 a.m.", "2026-10-09T00:30:00Z")]
    [InlineData("resets 12pm", "2026-10-09T12:00:00Z")]
    [InlineData("resets 12:30pm", "2026-10-09T12:30:00Z")]
    [InlineData("resets 1:15 PM", "2026-10-08T13:15:00Z")]
    public void Twelve_am_is_midnight_and_twelve_pm_is_noon(string message, string expected)
    {
        QuotaResetParser.TryParse(message, Now, TimeZoneInfo.Utc, out DateTimeOffset reset).Should().BeTrue();
        reset.Should().Be(At(expected));
    }

    [Theory]
    [InlineData("resets 25:00")]
    [InlineData("resets 3:75pm")]
    [InlineData("resets Feb 30, 3pm")]
    [InlineData("resets Sep 31 at 9:00")]
    public void An_impossible_time_or_date_is_no_reset(string message)
    {
        QuotaResetParser.TryParse(message, Now, TimeZoneInfo.Utc, out _).Should().BeFalse();
    }

    [Fact]
    public void A_dated_reset_that_has_passed_is_no_reset()
    {
        // Earlier today, by date: not the next such time, as a time without a date would be.
        QuotaResetParser.TryParse("resets Oct 8, 9am", Now, TimeZoneInfo.Utc, out _).Should().BeFalse();
    }

    [Fact]
    public void A_date_early_next_year_read_at_the_end_of_this_one_is_next_year()
    {
        var newYearsEve = new DateTimeOffset(2026, 12, 30, 18, 0, 0, TimeSpan.Zero);

        QuotaResetParser.TryParse("You've hit your weekly limit · resets Jan 2, 3pm", newYearsEve, TimeZoneInfo.Utc, out DateTimeOffset reset).Should().BeTrue();

        reset.Should().Be(At("2027-01-02T15:00:00Z"));
    }

    [Fact]
    public void A_utc_or_unknown_zone_in_the_message_overrides_or_falls_back_to_the_local_zone()
    {
        TimeZoneInfo plusTwo = TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2");

        QuotaResetParser.TryParse("resets 3pm (UTC)", Now, plusTwo, out DateTimeOffset utc).Should().BeTrue();
        QuotaResetParser.TryParse("resets 3pm (GMT)", Now, plusTwo, out DateTimeOffset gmt).Should().BeTrue();
        QuotaResetParser.TryParse("resets 3pm (Mars/Olympus_Mons)", Now, plusTwo, out DateTimeOffset unknown).Should().BeTrue();

        utc.Should().Be(At("2026-10-08T15:00:00Z"));
        gmt.Should().Be(utc);
        unknown.Should().Be(At("2026-10-09T13:00:00Z"), "15:00 at +2 has passed today (it is 15:00 there now), so tomorrow");
    }

    [Fact]
    public void A_relative_reset_of_zero_is_no_reset()
    {
        QuotaResetParser.TryParse("try again in 0 seconds", Now, TimeZoneInfo.Utc, out _).Should().BeFalse();
    }

    [Fact]
    public void Retry_after_reads_seconds_and_http_dates_in_the_future_only()
    {
        QuotaResetParser.TryParseRetryAfter(" 1.5 ", Now, out DateTimeOffset seconds).Should().BeTrue();
        seconds.Should().Be(Now.AddSeconds(1.5));

        QuotaResetParser.TryParseRetryAfter("Thu, 08 Oct 2026 13:05:00 GMT", Now, out DateTimeOffset date).Should().BeTrue();
        date.Should().Be(At("2026-10-08T13:05:00Z"));
        date.Offset.Should().Be(TimeSpan.Zero);

        QuotaResetParser.TryParseRetryAfter("Thu, 08 Oct 2026 12:55:00 GMT", Now, out _).Should().BeFalse("that date has passed");
        QuotaResetParser.TryParseRetryAfter("0", Now, out _).Should().BeFalse();
        QuotaResetParser.TryParseRetryAfter("-5", Now, out _).Should().BeFalse();
        QuotaResetParser.TryParseRetryAfter("soon", Now, out _).Should().BeFalse();
        QuotaResetParser.TryParseRetryAfter(null, Now, out _).Should().BeFalse();
        QuotaResetParser.TryParseRetryAfter(" ", Now, out _).Should().BeFalse();
    }
}
