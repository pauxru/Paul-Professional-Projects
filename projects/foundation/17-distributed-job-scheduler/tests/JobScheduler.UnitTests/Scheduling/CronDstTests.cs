using JobScheduler.Domain.Scheduling;

namespace JobScheduler.UnitTests.Scheduling;

/// <summary>
/// Timezone/DST correctness for the cron engine, exercised with a real IANA/Windows zone.
/// Spring-forward: the skipped wall-clock time must not fire. Fall-back: the repeated wall-clock
/// time must fire exactly once, on the earlier (larger-offset) instant.
/// </summary>
public sealed class CronDstTests
{
    // Resolver tolerates both "America/New_York" (IANA) and "Eastern Standard Time" (Windows).
    private static readonly TimeZoneInfo Eastern = TimeZoneResolver.Resolve("America/New_York");

    private static DateTimeOffset Iso(string s) => DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);

    [Fact]
    public void SpringForward_skips_the_nonexistent_local_time()
    {
        // US Eastern springs forward on 2024-03-10: 02:00 -> 03:00, so 02:30 does not exist.
        var cron = CronExpression.Parse("30 2 * * *"); // 02:30 local daily
        var occ = cron.GetNextOccurrences(Iso("2024-03-08T12:00:00-05:00"), 3, Eastern);

        var localDays = occ
            .Select(o => TimeZoneInfo.ConvertTime(o, Eastern).Day)
            .ToList();

        // March 10 is skipped entirely: sequence is the 9th, 11th, 12th.
        Assert.Equal([9, 11, 12], localDays);

        // No occurrence has a local wall time on the 10th.
        Assert.DoesNotContain(occ, o => TimeZoneInfo.ConvertTime(o, Eastern).Day == 10);
    }

    [Fact]
    public void SpringForward_resumes_with_daylight_offset()
    {
        var cron = CronExpression.Parse("30 2 * * *");
        // First occurrence at/after March 10 must be the 11th, now at EDT (-04:00).
        var next = cron.GetNextOccurrence(Iso("2024-03-10T00:00:00-05:00"), Eastern);
        Assert.NotNull(next);
        Assert.Equal(11, TimeZoneInfo.ConvertTime(next!.Value, Eastern).Day);
        Assert.Equal(TimeSpan.FromHours(-4), next.Value.Offset);
    }

    [Fact]
    public void FallBack_fires_repeated_local_time_once_on_the_earlier_instant()
    {
        // US Eastern falls back on 2024-11-03: 02:00 -> 01:00, so 01:30 occurs twice.
        var cron = CronExpression.Parse("30 1 * * *"); // 01:30 local daily
        var occ = cron.GetNextOccurrences(Iso("2024-11-02T12:00:00-04:00"), 3, Eastern);

        var onNov3 = occ.Where(o => TimeZoneInfo.ConvertTime(o, Eastern).Day == 3).ToList();
        Assert.Single(onNov3); // fired exactly once despite the ambiguity

        // The earlier instant corresponds to the larger offset (EDT, -04:00).
        Assert.Equal(TimeSpan.FromHours(-4), onNov3[0].Offset);
    }

    [Fact]
    public void FallBack_following_day_uses_standard_offset()
    {
        var cron = CronExpression.Parse("30 1 * * *");
        var occ = cron.GetNextOccurrences(Iso("2024-11-02T12:00:00-04:00"), 3, Eastern);
        var nov4 = occ.First(o => TimeZoneInfo.ConvertTime(o, Eastern).Day == 4);
        Assert.Equal(TimeSpan.FromHours(-5), nov4.Offset); // EST after the transition
    }
}
