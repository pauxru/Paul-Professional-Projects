namespace NotificationPlatform.Application.Fairness;

using NotificationPlatform.Domain.Common;

public interface IQuietHoursCalculator
{
    /// <summary>
    /// If <paramref name="now"/> falls inside the recipient's quiet-hours window (interpreted in
    /// the recipient's IANA time zone), returns the next allowed <see cref="DateTimeOffset"/>
    /// (start of the day after quiet hours end).  Otherwise returns <c>now</c>.
    /// Windows that cross midnight (e.g. 22:00–07:00) are supported.
    /// If <see cref="NotificationPriority.Transactional"/> is provided, the value of <paramref name="now"/>
    /// is returned unchanged.
    /// </summary>
    DateTimeOffset ComputeDeferralTarget(DateTimeOffset now, string timeZoneId, TimeSpan quietStart, TimeSpan quietEnd, NotificationPriority priority);
}

public sealed class QuietHoursCalculator : IQuietHoursCalculator
{
    public DateTimeOffset ComputeDeferralTarget(DateTimeOffset now, string timeZoneId, TimeSpan quietStart, TimeSpan quietEnd, NotificationPriority priority)
    {
        if (priority == NotificationPriority.Transactional) return now;
        if (quietStart == quietEnd) return now;

        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return now;
        }
        catch (InvalidTimeZoneException)
        {
            return now;
        }

        var local = TimeZoneInfo.ConvertTime(now, tz);
        var todayLocalDate = local.Date;
        var localTod = local.TimeOfDay;

        // Case A: window does not cross midnight (e.g. 08:00-11:00)
        if (quietStart < quietEnd)
        {
            if (localTod >= quietStart && localTod < quietEnd)
            {
                var releaseLocal = todayLocalDate + quietEnd;
                var offset = tz.GetUtcOffset(releaseLocal);
                return new DateTimeOffset(releaseLocal, offset);
            }
            return now;
        }

        // Case B: window crosses midnight (e.g. 22:00-07:00)
        if (localTod >= quietStart)
        {
            var releaseLocal = todayLocalDate.AddDays(1) + quietEnd;
            var offset = tz.GetUtcOffset(releaseLocal);
            return new DateTimeOffset(releaseLocal, offset);
        }
        if (localTod < quietEnd)
        {
            var releaseLocal = todayLocalDate + quietEnd;
            var offset = tz.GetUtcOffset(releaseLocal);
            return new DateTimeOffset(releaseLocal, offset);
        }
        return now;
    }
}
