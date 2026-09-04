using System.Globalization;

namespace SubscriptionBilling.Domain;

public sealed class DomainException(string message) : InvalidOperationException(message);

public enum MonetaryRounding
{
    Bankers,
    AwayFromZero
}

public readonly record struct Money
{
    private static readonly HashSet<string> SupportedCurrencies =
        new(StringComparer.Ordinal) { "KES", "USD", "EUR" };

    public Money(long minorUnits, string currency)
    {
        Currency = NormalizeCurrency(currency);
        MinorUnits = minorUnits;
    }

    public long MinorUnits { get; }
    public string Currency { get; }

    public static Money Zero(string currency) => new(0, currency);

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(checked(MinorUnits + other.MinorUnits), Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(checked(MinorUnits - other.MinorUnits), Currency);
    }

    public Money Multiply(long multiplier) =>
        new(checked(MinorUnits * multiplier), Currency);

    public Money Multiply(decimal multiplier, MonetaryRounding rounding = MonetaryRounding.Bankers) =>
        new(RoundMinorUnits(MinorUnits * multiplier, rounding), Currency);

    public Money Negate() => new(checked(-MinorUnits), Currency);

    public Money Abs() => MinorUnits == long.MinValue
        ? throw new OverflowException("Cannot take the absolute value of the minimum 64-bit amount.")
        : new Money(Math.Abs(MinorUnits), Currency);

    public static long RoundMinorUnits(
        decimal value,
        MonetaryRounding rounding = MonetaryRounding.Bankers)
    {
        var midpoint = rounding == MonetaryRounding.Bankers
            ? MidpointRounding.ToEven
            : MidpointRounding.AwayFromZero;
        return checked((long)decimal.Round(value, 0, midpoint));
    }

    public static Money operator +(Money left, Money right) => left.Add(right);
    public static Money operator -(Money left, Money right) => left.Subtract(right);
    public static Money operator -(Money value) => value.Negate();

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Currency} {MinorUnits / 100m:0.00}");

    private static string NormalizeCurrency(string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        var normalized = currency.Trim().ToUpperInvariant();
        if (!SupportedCurrencies.Contains(normalized))
        {
            throw new DomainException("Currency must be one of KES, USD, or EUR.");
        }

        return normalized;
    }

    private void EnsureSameCurrency(Money other)
    {
        if (!string.Equals(Currency, other.Currency, StringComparison.Ordinal))
        {
            throw new DomainException($"Currency mismatch: {Currency} and {other.Currency}.");
        }
    }
}

public enum BillingIntervalUnit
{
    Day,
    Week,
    Month,
    Year
}

public readonly record struct BillingInterval
{
    public BillingInterval(BillingIntervalUnit unit, int count)
    {
        if (count is < 1 or > 36)
        {
            throw new DomainException("Billing interval count must be between 1 and 36.");
        }

        Unit = unit;
        Count = count;
    }

    public BillingIntervalUnit Unit { get; }
    public int Count { get; }
}

public readonly record struct BillingCycleAnchor(
    DateTimeOffset Origin,
    int Day,
    bool IsMonthEnd)
{
    public static BillingCycleAnchor From(DateTimeOffset origin)
    {
        var isMonthEnd = origin.Day == DateTime.DaysInMonth(origin.Year, origin.Month);
        return new BillingCycleAnchor(origin, origin.Day, isMonthEnd);
    }

    public DateTimeOffset Next(DateTimeOffset currentPeriodStart, BillingInterval interval) =>
        interval.Unit switch
        {
            BillingIntervalUnit.Day => currentPeriodStart.AddDays(interval.Count),
            BillingIntervalUnit.Week => currentPeriodStart.AddDays(checked(interval.Count * 7)),
            BillingIntervalUnit.Month => AddMonthsAnchored(currentPeriodStart, interval.Count),
            BillingIntervalUnit.Year => AddYearsAnchored(currentPeriodStart, interval.Count),
            _ => throw new DomainException("Unsupported billing interval.")
        };

    private DateTimeOffset AddMonthsAnchored(DateTimeOffset current, int months)
    {
        var targetMonth = new DateTime(current.Year, current.Month, 1).AddMonths(months);
        var day = IsMonthEnd
            ? DateTime.DaysInMonth(targetMonth.Year, targetMonth.Month)
            : Math.Min(Day, DateTime.DaysInMonth(targetMonth.Year, targetMonth.Month));
        return CreateAtCurrentTime(targetMonth.Year, targetMonth.Month, day, current);
    }

    private DateTimeOffset AddYearsAnchored(DateTimeOffset current, int years)
    {
        var year = checked(current.Year + years);
        var month = Origin.Month;
        var day = IsMonthEnd
            ? DateTime.DaysInMonth(year, month)
            : Math.Min(Day, DateTime.DaysInMonth(year, month));
        return CreateAtCurrentTime(year, month, day, current);
    }

    private static DateTimeOffset CreateAtCurrentTime(
        int year,
        int month,
        int day,
        DateTimeOffset current)
    {
        var result = new DateTimeOffset(
            year,
            month,
            day,
            current.Hour,
            current.Minute,
            current.Second,
            current.Offset);
        return result.AddTicks(current.Ticks % TimeSpan.TicksPerSecond);
    }
}
