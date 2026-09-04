using ExampleBank.Ledger.Domain.Common;

namespace ExampleBank.Ledger.Domain.Interest;

public enum DayCountConvention
{
    /// <summary>Actual days elapsed divided by a fixed 365-day year.</summary>
    Actual365 = 1,

    /// <summary>US (NASD) 30/360: each month treated as 30 days, year as 360 days.</summary>
    Thirty360 = 2,
}

/// <summary>Computes the year fraction between two dates under a specific day-count convention.</summary>
public interface IDayCountConvention
{
    DayCountConvention Kind { get; }

    decimal YearFraction(DateOnly start, DateOnly end);
}

public sealed class Actual365Convention : IDayCountConvention
{
    public DayCountConvention Kind => DayCountConvention.Actual365;

    public decimal YearFraction(DateOnly start, DateOnly end)
    {
        int days = end.DayNumber - start.DayNumber;
        return (decimal)days / 365m;
    }
}

public sealed class Thirty360Convention : IDayCountConvention
{
    public DayCountConvention Kind => DayCountConvention.Thirty360;

    public decimal YearFraction(DateOnly start, DateOnly end)
    {
        int d1 = start.Day;
        int d2 = end.Day;
        int m1 = start.Month;
        int m2 = end.Month;
        int y1 = start.Year;
        int y2 = end.Year;

        // US/NASD 30/360 day adjustments.
        if (d1 == 31)
        {
            d1 = 30;
        }

        if (d2 == 31 && d1 == 30)
        {
            d2 = 30;
        }

        int days = (360 * (y2 - y1)) + (30 * (m2 - m1)) + (d2 - d1);
        return (decimal)days / 360m;
    }
}

public static class DayCountConventions
{
    private static readonly Actual365Convention Act365 = new();
    private static readonly Thirty360Convention T30360 = new();

    public static IDayCountConvention For(DayCountConvention convention) => convention switch
    {
        DayCountConvention.Actual365 => Act365,
        DayCountConvention.Thirty360 => T30360,
        _ => throw new DomainException("interest.unknown_convention", $"Unknown day-count convention {convention}."),
    };
}
