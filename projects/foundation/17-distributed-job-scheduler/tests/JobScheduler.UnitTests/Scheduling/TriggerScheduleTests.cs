using JobScheduler.Domain;
using JobScheduler.Domain.Scheduling;

namespace JobScheduler.UnitTests.Scheduling;

/// <summary>
/// Trigger evaluation and misfire handling. A cron that was missed for several hours is evaluated
/// after the fact; the catch-up window separates on-time occurrences from misfires, and the
/// misfire policy decides how much of the backlog is recovered.
/// </summary>
public sealed class TriggerScheduleTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static DateTimeOffset Iso(string s) => DateTimeOffset.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static TriggerSchedule Hourly(MisfirePolicy policy, int maxCatchUp = 100) => new(
        TriggerType.Cron, Utc,
        cron: CronExpression.Parse("0 * * * *"),
        misfire: policy,
        catchUpWindow: TimeSpan.FromMinutes(5),
        maxCatchUp: maxCatchUp);

    // last fire 00:00, now 05:02 -> occurrences 01:00..05:00; cutoff 04:57 -> on-time {05:00}.
    private static readonly DateTimeOffset LastFire = Iso("2024-01-01T00:00:00Z");
    private static readonly DateTimeOffset Now = Iso("2024-01-01T05:02:00Z");

    [Fact]
    public void FireNow_collapses_backlog_to_a_single_catchup_run()
    {
        var due = Hourly(MisfirePolicy.FireNow).ComputeDue(LastFire, Now);
        Assert.Equal(["2024-01-01T05:00:00Z"], due.OnTime.Select(x => x.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ")));
        Assert.Single(due.Recovered);
        Assert.Equal("2024-01-01T04:00:00Z", due.Recovered[0].UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"));
    }

    [Fact]
    public void SkipToNext_discards_all_misfires()
    {
        var due = Hourly(MisfirePolicy.SkipToNext).ComputeDue(LastFire, Now);
        Assert.Single(due.OnTime);
        Assert.Empty(due.Recovered);
    }

    [Fact]
    public void RunAllMissed_recovers_every_missed_occurrence()
    {
        var due = Hourly(MisfirePolicy.RunAllMissed).ComputeDue(LastFire, Now);
        // misfired = 01:00,02:00,03:00,04:00
        Assert.Equal(4, due.Recovered.Count);
        Assert.Single(due.OnTime);
        Assert.Equal(5, due.Count);
    }

    [Fact]
    public void RunAllMissed_respects_the_catchup_cap_keeping_most_recent()
    {
        var due = Hourly(MisfirePolicy.RunAllMissed, maxCatchUp: 2).ComputeDue(LastFire, Now);
        Assert.Equal(2, due.Recovered.Count);
        // Keeps the two most recent misfires: 03:00 and 04:00.
        Assert.Equal("2024-01-01T03:00:00Z", due.Recovered[0].UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        Assert.Equal("2024-01-01T04:00:00Z", due.Recovered[1].UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"));
    }

    [Fact]
    public void NoMisfires_when_everything_is_within_the_catchup_window()
    {
        var lastFire = Iso("2024-01-01T04:58:00Z");
        var due = Hourly(MisfirePolicy.RunAllMissed).ComputeDue(lastFire, Now); // only 05:00 due
        Assert.Single(due.OnTime);
        Assert.Empty(due.Recovered);
    }

    [Fact]
    public void Interval_enumerates_each_missed_tick()
    {
        var schedule = new TriggerSchedule(TriggerType.Interval, Utc, interval: TimeSpan.FromMinutes(30),
            misfire: MisfirePolicy.RunAllMissed, catchUpWindow: TimeSpan.FromMinutes(1));
        var due = schedule.ComputeDue(Iso("2024-01-01T00:00:00Z"), Iso("2024-01-01T02:00:00Z"));
        // 00:30, 01:00, 01:30, 02:00
        Assert.Equal(4, due.Count);
    }

    [Fact]
    public void OneOff_fires_once_inside_the_window_then_never_again()
    {
        var runAt = Iso("2024-01-01T01:00:00Z");
        var schedule = new TriggerSchedule(TriggerType.OneOff, Utc, runAt: runAt);
        var due = schedule.ComputeDue(Iso("2024-01-01T00:00:00Z"), Iso("2024-01-01T02:00:00Z"));
        Assert.Equal(1, due.Count);

        // After it has fired (lastFire moved past runAt), it is no longer due.
        var after = schedule.ComputeDue(runAt, Iso("2024-01-01T03:00:00Z"));
        Assert.Equal(0, after.Count);
    }

    [Fact]
    public void Manual_trigger_is_never_due()
    {
        var schedule = new TriggerSchedule(TriggerType.Manual, Utc);
        Assert.Equal(0, schedule.ComputeDue(LastFire, Now).Count);
        Assert.Null(schedule.NextFireAfter(Now));
    }

    [Fact]
    public void NextFireAfter_computes_the_upcoming_instant_per_type()
    {
        var cron = new TriggerSchedule(TriggerType.Cron, Utc, cron: CronExpression.Parse("0 * * * *"));
        Assert.Equal(Iso("2024-01-01T06:00:00Z"), cron.NextFireAfter(Iso("2024-01-01T05:02:00Z")));

        var interval = new TriggerSchedule(TriggerType.Interval, Utc, interval: TimeSpan.FromHours(1));
        Assert.Equal(Iso("2024-01-01T06:02:00Z"), interval.NextFireAfter(Iso("2024-01-01T05:02:00Z")));

        var oneOff = new TriggerSchedule(TriggerType.OneOff, Utc, runAt: Iso("2024-01-01T06:00:00Z"));
        Assert.Equal(Iso("2024-01-01T06:00:00Z"), oneOff.NextFireAfter(Now));
        Assert.Null(oneOff.NextFireAfter(Iso("2024-01-01T07:00:00Z")));
    }

    [Theory]
    [InlineData(TriggerType.Cron)]
    [InlineData(TriggerType.Interval)]
    [InlineData(TriggerType.OneOff)]
    public void Constructor_validates_required_parameters(TriggerType type)
    {
        Assert.Throws<ArgumentException>(() => new TriggerSchedule(type, Utc));
    }
}
