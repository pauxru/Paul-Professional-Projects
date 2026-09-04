namespace SubscriptionBilling.Domain;

public enum UsageAggregationMode
{
    Sum,
    Max,
    LastValue,
    UniqueCount
}

public enum UsageRoundingMode
{
    Up,
    Down,
    NearestBankers,
    NearestAwayFromZero
}

public sealed record MeterDefinition(
    Guid Id,
    string Name,
    string Unit,
    UsageAggregationMode Aggregation,
    decimal RoundingIncrement,
    UsageRoundingMode RoundingMode)
{
    public long RoundToBillableUnits(decimal value)
    {
        if (RoundingIncrement <= 0)
        {
            throw new DomainException("Meter rounding increment must be positive.");
        }

        var scaled = value / RoundingIncrement;
        var rounded = RoundingMode switch
        {
            UsageRoundingMode.Up => decimal.Ceiling(scaled),
            UsageRoundingMode.Down => decimal.Floor(scaled),
            UsageRoundingMode.NearestBankers => decimal.Round(scaled, 0, MidpointRounding.ToEven),
            UsageRoundingMode.NearestAwayFromZero =>
                decimal.Round(scaled, 0, MidpointRounding.AwayFromZero),
            _ => throw new DomainException("Unsupported usage rounding mode.")
        };
        return checked((long)rounded);
    }
}

public sealed record UsageEvent
{
    public UsageEvent(
        string eventId,
        Guid subscriptionId,
        Guid meterId,
        DateTimeOffset occurredAt,
        decimal quantity,
        string? uniqueKey = null,
        string? adjustmentOfEventId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        if (subscriptionId == Guid.Empty || meterId == Guid.Empty)
        {
            throw new DomainException("Subscription and meter ids are required.");
        }

        if (quantity < 0 && string.IsNullOrWhiteSpace(adjustmentOfEventId))
        {
            throw new DomainException("Negative usage is allowed only as an adjustment event.");
        }

        EventId = eventId.Trim();
        SubscriptionId = subscriptionId;
        MeterId = meterId;
        OccurredAt = occurredAt;
        Quantity = quantity;
        UniqueKey = uniqueKey?.Trim();
        AdjustmentOfEventId = adjustmentOfEventId?.Trim();
    }

    public string EventId { get; }
    public Guid SubscriptionId { get; }
    public Guid MeterId { get; }
    public DateTimeOffset OccurredAt { get; }
    public decimal Quantity { get; }
    public string? UniqueKey { get; }
    public string? AdjustmentOfEventId { get; }
}

public static class UsageAggregator
{
    public static decimal Aggregate(
        MeterDefinition meter,
        IEnumerable<UsageEvent> events)
    {
        ArgumentNullException.ThrowIfNull(meter);
        var materialized = events?.ToList() ?? throw new ArgumentNullException(nameof(events));
        if (materialized.Count == 0)
        {
            return 0m;
        }

        return meter.Aggregation switch
        {
            UsageAggregationMode.Sum => materialized.Sum(item => item.Quantity),
            UsageAggregationMode.Max => materialized.Max(item => item.Quantity),
            UsageAggregationMode.LastValue => materialized
                .OrderBy(item => item.OccurredAt)
                .ThenBy(item => item.EventId, StringComparer.Ordinal)
                .Last()
                .Quantity,
            UsageAggregationMode.UniqueCount => materialized
                .Where(item => !string.IsNullOrWhiteSpace(item.UniqueKey))
                .Select(item => item.UniqueKey!)
                .Distinct(StringComparer.Ordinal)
                .LongCount(),
            _ => throw new DomainException("Unsupported usage aggregation mode.")
        };
    }
}

public enum ClosedPeriodUsageBehavior
{
    Reject,
    CreditNextOpenPeriod
}

public enum LateUsageDisposition
{
    AcceptedInOpenPeriod,
    RejectedClosedPeriod,
    CreditedToNextOpenPeriod
}

public static class LateUsagePolicy
{
    public static LateUsageDisposition Decide(
        bool containingPeriodIsClosed,
        ClosedPeriodUsageBehavior behavior)
    {
        if (!containingPeriodIsClosed)
        {
            return LateUsageDisposition.AcceptedInOpenPeriod;
        }

        return behavior switch
        {
            ClosedPeriodUsageBehavior.Reject => LateUsageDisposition.RejectedClosedPeriod,
            ClosedPeriodUsageBehavior.CreditNextOpenPeriod =>
                LateUsageDisposition.CreditedToNextOpenPeriod,
            _ => throw new DomainException("Unsupported late-usage behavior.")
        };
    }
}
