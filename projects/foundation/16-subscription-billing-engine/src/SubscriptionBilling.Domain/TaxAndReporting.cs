namespace SubscriptionBilling.Domain;

public enum TaxPricingMode
{
    Exclusive,
    Inclusive
}

public enum TaxRoundingLevel
{
    Line,
    Invoice
}

public sealed record TaxLine(Guid Id, Money Amount);

public sealed record TaxCalculation(
    Money TotalTax,
    IReadOnlyDictionary<Guid, Money> TaxByLine);

public static class TaxCalculator
{
    public static TaxCalculation Calculate(
        IReadOnlyList<TaxLine> lines,
        decimal rate,
        TaxPricingMode pricingMode,
        TaxRoundingLevel roundingLevel,
        bool exempt,
        bool reverseCharge,
        MonetaryRounding rounding = MonetaryRounding.Bankers)
    {
        if (lines.Count == 0)
        {
            throw new DomainException("At least one tax line is required.");
        }

        if (rate < 0 || rate > 1)
        {
            throw new DomainException("Tax rate must be between zero and one.");
        }

        var currency = lines[0].Amount.Currency;
        if (lines.Any(line => !string.Equals(line.Amount.Currency, currency, StringComparison.Ordinal)))
        {
            throw new DomainException("All tax lines must use the same currency.");
        }

        if (exempt || reverseCharge || rate == 0)
        {
            return new TaxCalculation(
                Money.Zero(currency),
                lines.ToDictionary(line => line.Id, _ => Money.Zero(currency)));
        }

        if (roundingLevel == TaxRoundingLevel.Line)
        {
            var byLine = lines.ToDictionary(
                line => line.Id,
                line => new Money(TaxForAmount(line.Amount.MinorUnits, rate, pricingMode, rounding), currency));
            return new TaxCalculation(
                new Money(byLine.Values.Sum(value => value.MinorUnits), currency),
                byLine);
        }

        var totalAmount = lines.Sum(line => line.Amount.MinorUnits);
        var totalTax = TaxForAmount(totalAmount, rate, pricingMode, rounding);
        return new TaxCalculation(
            new Money(totalTax, currency),
            new Dictionary<Guid, Money>());
    }

    private static long TaxForAmount(
        long amount,
        decimal rate,
        TaxPricingMode mode,
        MonetaryRounding rounding)
    {
        if (mode == TaxPricingMode.Exclusive)
        {
            return Money.RoundMinorUnits(amount * rate, rounding);
        }

        var net = Money.RoundMinorUnits(amount / (1m + rate), rounding);
        return checked(amount - net);
    }
}

public sealed record RecurringContractSnapshot(
    Guid SubscriptionId,
    Money PeriodAmount,
    BillingInterval Interval,
    bool Active,
    bool PreviouslyActive);

public sealed record MrrMovement(
    Money Opening,
    Money New,
    Money Expansion,
    Money Contraction,
    Money Churn,
    Money Reactivation,
    Money Closing,
    Money Arr);

public static class RevenueReporting
{
    public static Money MonthlyRecurringRevenue(RecurringContractSnapshot contract)
    {
        if (!contract.Active)
        {
            return Money.Zero(contract.PeriodAmount.Currency);
        }

        var factor = contract.Interval.Unit switch
        {
            BillingIntervalUnit.Day => 365m / 12m / contract.Interval.Count,
            BillingIntervalUnit.Week => 52m / 12m / contract.Interval.Count,
            BillingIntervalUnit.Month => 1m / contract.Interval.Count,
            BillingIntervalUnit.Year => 1m / (12m * contract.Interval.Count),
            _ => throw new DomainException("Unsupported billing interval.")
        };
        return contract.PeriodAmount.Multiply(factor);
    }

    public static MrrMovement CalculateMovement(
        IReadOnlyList<RecurringContractSnapshot> previous,
        IReadOnlyList<RecurringContractSnapshot> current,
        string currency)
    {
        var normalizedCurrency = Money.Zero(currency).Currency;
        var oldValues = previous.ToDictionary(
            item => item.SubscriptionId,
            item => MonthlyRecurringRevenue(item).MinorUnits);
        var currentValues = current.ToDictionary(
            item => item.SubscriptionId,
            item => MonthlyRecurringRevenue(item).MinorUnits);

        long newMrr = 0;
        long expansion = 0;
        long contraction = 0;
        long churn = 0;
        long reactivation = 0;

        foreach (var item in current)
        {
            var now = currentValues[item.SubscriptionId];
            oldValues.TryGetValue(item.SubscriptionId, out var before);
            if (before == 0 && now > 0)
            {
                if (item.PreviouslyActive)
                {
                    reactivation = checked(reactivation + now);
                }
                else
                {
                    newMrr = checked(newMrr + now);
                }
            }
            else if (now > before)
            {
                expansion = checked(expansion + now - before);
            }
            else if (now > 0 && now < before)
            {
                contraction = checked(contraction + before - now);
            }
        }

        foreach (var previousItem in previous)
        {
            var before = oldValues[previousItem.SubscriptionId];
            if (!currentValues.TryGetValue(previousItem.SubscriptionId, out var now) || now == 0)
            {
                churn = checked(churn + before);
            }
        }

        var opening = oldValues.Values.Sum();
        var closing = currentValues.Values.Sum();
        var expectedClosing = checked(opening + newMrr + expansion + reactivation - contraction - churn);
        if (expectedClosing != closing)
        {
            throw new DomainException("MRR movement categories do not reconcile to closing MRR.");
        }

        return new MrrMovement(
            new Money(opening, normalizedCurrency),
            new Money(newMrr, normalizedCurrency),
            new Money(expansion, normalizedCurrency),
            new Money(contraction, normalizedCurrency),
            new Money(churn, normalizedCurrency),
            new Money(reactivation, normalizedCurrency),
            new Money(closing, normalizedCurrency),
            new Money(checked(closing * 12), normalizedCurrency));
    }
}

public sealed record RevenueRecognitionEntry(DateOnly Date, Money Amount);

public static class RevenueRecognition
{
    public static IReadOnlyList<RevenueRecognitionEntry> SpreadEvenly(
        Money amount,
        DateTimeOffset serviceStart,
        DateTimeOffset serviceEnd)
    {
        if (serviceEnd <= serviceStart)
        {
            throw new DomainException("Revenue service period is invalid.");
        }

        var startDate = DateOnly.FromDateTime(serviceStart.UtcDateTime);
        var endDate = DateOnly.FromDateTime(serviceEnd.UtcDateTime);
        var days = endDate.DayNumber - startDate.DayNumber;
        if (days <= 0)
        {
            days = 1;
        }

        var quotient = amount.MinorUnits / days;
        var remainder = amount.MinorUnits % days;
        var entries = new List<RevenueRecognitionEntry>(days);
        for (var index = 0; index < days; index++)
        {
            var adjustment = index < Math.Abs(remainder) ? Math.Sign(remainder) : 0;
            entries.Add(new RevenueRecognitionEntry(
                startDate.AddDays(index),
                new Money(checked(quotient + adjustment), amount.Currency)));
        }

        return entries;
    }
}
