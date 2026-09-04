using JobScheduler.Domain.Scheduling;

namespace JobScheduler.UnitTests.Scheduling;

/// <summary>
/// Exhaustive cron parser coverage: field widths, ranges, steps, lists, names, the Vixie
/// day-of-month/day-of-week OR rule, the L / # tokens, invalid expressions, and next-N
/// occurrences compared against hand-computed instants (all in UTC unless stated).
/// </summary>
public sealed class CronParserTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static DateTimeOffset Iso(string s) => DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static string Next(string expr, string after)
    {
        var cron = CronExpression.Parse(expr);
        var next = cron.GetNextOccurrence(Iso(after), Utc);
        Assert.NotNull(next);
        return next!.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    [Theory]
    [InlineData("0 0 * * *", "2024-01-01T12:00:00Z", "2024-01-02T00:00:00Z")]        // daily midnight
    [InlineData("*/15 * * * *", "2024-01-01T00:00:00Z", "2024-01-01T00:15:00Z")]      // step every 15 min
    [InlineData("0 9 * * 1", "2024-01-03T00:00:00Z", "2024-01-08T09:00:00Z")]         // Monday 09:00 (Wed -> next Mon)
    [InlineData("0 12 1 * *", "2024-01-15T00:00:00Z", "2024-02-01T12:00:00Z")]        // 1st of month noon
    [InlineData("0 0,12 * * *", "2024-01-01T06:00:00Z", "2024-01-01T12:00:00Z")]      // list: midnight & noon
    [InlineData("0 0/6 * * *", "2024-01-01T00:00:00Z", "2024-01-01T06:00:00Z")]       // start/step
    [InlineData("0 9-17 * * *", "2024-01-01T09:30:00Z", "2024-01-01T10:00:00Z")]      // hour range
    [InlineData("0 0 1 JAN *", "2023-06-01T00:00:00Z", "2024-01-01T00:00:00Z")]       // month name
    [InlineData("0 9 * * MON-FRI", "2024-01-06T00:00:00Z", "2024-01-08T09:00:00Z")]   // day-name range (Sat -> Mon)
    public void GetNextOccurrence_matches_hand_computed(string expr, string after, string expected)
    {
        Assert.Equal(expected, Next(expr, after));
    }

    [Fact]
    public void SixFieldExpression_supports_seconds()
    {
        // sec=15 min=30 hour=8 daily
        Assert.Equal("2024-01-01T08:30:15Z", Next("15 30 8 * * *", "2024-01-01T00:00:00Z"));
    }

    [Fact]
    public void FiveFieldExpression_fires_on_second_zero()
    {
        Assert.Equal("2024-01-01T00:15:00Z", Next("*/15 * * * *", "2024-01-01T00:00:30Z"));
    }

    [Fact]
    public void LastDayOfMonth_L_resolves_including_leap_february()
    {
        Assert.Equal("2024-02-29T00:00:00Z", Next("0 0 L * *", "2024-02-10T00:00:00Z"));
    }

    [Fact]
    public void ExplicitFeb29_finds_next_leap_year()
    {
        Assert.Equal("2024-02-29T00:00:00Z", Next("0 0 29 2 *", "2023-03-01T00:00:00Z"));
    }

    [Fact]
    public void LastWeekday_5L_is_last_friday_of_month()
    {
        // Last Friday of Feb 2024 is the 23rd.
        Assert.Equal("2024-02-23T00:00:00Z", Next("0 0 * * 5L", "2024-02-01T00:00:00Z"));
    }

    [Fact]
    public void NthWeekday_hash_is_second_monday()
    {
        // 2nd Monday of Jan 2024 is the 8th.
        Assert.Equal("2024-01-08T00:00:00Z", Next("0 0 * * 1#2", "2024-01-01T00:00:00Z"));
    }

    [Fact]
    public void GetNextOccurrences_returns_sequential_days()
    {
        var cron = CronExpression.Parse("0 0 * * *");
        var occ = cron.GetNextOccurrences(Iso("2024-01-01T00:00:00Z"), 3, Utc);
        Assert.Equal(3, occ.Count);
        Assert.Equal("2024-01-02T00:00:00Z", occ[0].UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        Assert.Equal("2024-01-03T00:00:00Z", occ[1].UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        Assert.Equal("2024-01-04T00:00:00Z", occ[2].UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"));
    }

    [Fact]
    public void VixieOrRule_when_both_dom_and_dow_restricted_matches_either()
    {
        // "0 0 13 * 5" = the 13th OR any Friday. From Oct 12 2024 (Sat) the next match is the
        // 13th (a Sunday) via the day-of-month arm -> proves OR, not AND.
        Assert.Equal("2024-10-13T00:00:00Z", Next("0 0 13 * 5", "2024-10-12T00:00:00Z"));
        // From Oct 13 12:00 the next match is Friday Oct 18 via the day-of-week arm.
        Assert.Equal("2024-10-18T00:00:00Z", Next("0 0 13 * 5", "2024-10-13T12:00:00Z"));
    }

    [Fact]
    public void Inclusive_flag_returns_the_boundary_instant()
    {
        var cron = CronExpression.Parse("0 0 * * *");
        var at = Iso("2024-01-02T00:00:00Z");
        Assert.Equal(at, cron.GetNextOccurrence(at, Utc, inclusive: true));
        Assert.NotEqual(at, cron.GetNextOccurrence(at, Utc, inclusive: false));
    }

    [Fact]
    public void ImpossibleDate_returns_null_within_horizon()
    {
        // Feb 30 never occurs.
        var cron = CronExpression.Parse("0 0 30 2 *");
        Assert.Null(cron.GetNextOccurrence(Iso("2024-01-01T00:00:00Z"), Utc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("* * *")]                 // too few fields
    [InlineData("* * * * * * *")]         // too many fields
    [InlineData("60 * * * *")]            // minute out of range
    [InlineData("* 24 * * *")]            // hour out of range
    [InlineData("* * 32 * *")]            // day-of-month out of range
    [InlineData("* * * 13 *")]            // month out of range
    [InlineData("* * * * 8")]             // day-of-week out of range
    [InlineData("*/0 * * * *")]           // zero step
    [InlineData("abc * * * *")]           // non-numeric token
    [InlineData("5-1 * * * *")]           // inverted range
    public void Parse_rejects_invalid_expressions(string expr)
    {
        Assert.Throws<CronFormatException>(() => CronExpression.Parse(expr));
        Assert.False(CronExpression.TryParse(expr, out var cron));
        Assert.Null(cron);
    }

    [Theory]
    [InlineData("* * * * *")]
    [InlineData("0 0 * * *")]
    [InlineData("*/5 0-23 1,15 JAN-DEC MON-FRI")]
    [InlineData("30 2 * * *")]
    [InlineData("0 0 L * *")]
    [InlineData("0 0 * * 5L")]
    [InlineData("0 0 * * 1#3")]
    public void TryParse_accepts_valid_expressions(string expr)
    {
        Assert.True(CronExpression.TryParse(expr, out var cron));
        Assert.NotNull(cron);
    }

    [Fact]
    public void Sunday_accepts_both_0_and_7()
    {
        // Jan 7 2024 is a Sunday; both encodings must resolve to it.
        Assert.Equal("2024-01-07T00:00:00Z", Next("0 0 * * 0", "2024-01-01T00:00:00Z"));
        Assert.Equal("2024-01-07T00:00:00Z", Next("0 0 * * 7", "2024-01-01T00:00:00Z"));
    }
}
