using Healthcare.Domain.Appointments;
using Xunit;

namespace Healthcare.UnitTests.Domain;

public class RecurringSeriesTests
{
    [Fact]
    public void Weekly_Occurrences_Only_On_Target_Day()
    {
        var series = RecurringSeries.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DayOfWeek.Tuesday, new TimeOnly(10, 0), new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));
        var days = series.Occurrences();
        Assert.All(days, d => Assert.Equal(DayOfWeek.Tuesday, d.DayOfWeek));
        // 2026-09-01 is a Tuesday. There should be 5 Tuesdays in Sep 2026 (1,8,15,22,29).
        Assert.Equal(5, days.Count);
    }

    [Fact]
    public void Exception_Dates_Excluded()
    {
        var series = RecurringSeries.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            DayOfWeek.Tuesday, new TimeOnly(10, 0), new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));
        series.AddExceptionDate(new DateOnly(2026, 9, 15));
        var days = series.Occurrences();
        Assert.DoesNotContain(new DateOnly(2026, 9, 15), days);
        Assert.Equal(4, days.Count);
    }
}
