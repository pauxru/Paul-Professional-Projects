using Northstar.Reliability.Domain.Common;
using Northstar.Reliability.Domain.Telemetry;

namespace Northstar.Reliability.Domain.Slo;

public enum SliAggregationMode
{
    RequestBased,
    WindowBased
}

public enum SliKind
{
    Availability,
    Latency,
    Quality,
    Freshness
}

public sealed record SliFilter(string? Endpoint, string? Region, string? Tier)
{
    public static SliFilter All { get; } = new(null, null, null);

    public bool Matches(MetricSample sample) =>
        (string.IsNullOrWhiteSpace(Endpoint) || string.Equals(Endpoint, sample.Endpoint, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(Region) || string.Equals(Region, sample.Region, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(Tier) || string.Equals(Tier, sample.Tier, StringComparison.OrdinalIgnoreCase));
}

public sealed record SliDefinition(
    Guid Id,
    string Name,
    string ServiceSlug,
    SliAggregationMode AggregationMode,
    SliKind Kind,
    SliFilter Filter,
    decimal? LatencyThresholdMilliseconds,
    DateTimeOffset CreatedAt)
{
    public static SliDefinition Create(
        string name,
        string serviceSlug,
        SliAggregationMode aggregationMode,
        SliKind kind,
        SliFilter? filter,
        decimal? latencyThresholdMilliseconds,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(serviceSlug))
        {
            throw new DomainRuleViolationException("SLI name and service are required.");
        }

        if (kind == SliKind.Latency && (!latencyThresholdMilliseconds.HasValue || latencyThresholdMilliseconds <= 0))
        {
            throw new DomainRuleViolationException("Latency SLIs require a positive threshold in milliseconds.");
        }

        return new SliDefinition(
            Guid.NewGuid(),
            name.Trim(),
            serviceSlug.Trim().ToLowerInvariant(),
            aggregationMode,
            kind,
            filter ?? SliFilter.All,
            latencyThresholdMilliseconds,
            now);
    }
}

public enum SloWindowKind
{
    Rolling,
    Calendar
}

public enum CalendarWindowPeriod
{
    Monthly,
    Quarterly
}

public sealed record SloWindow(
    DateTimeOffset Start,
    DateTimeOffset End,
    DateTimeOffset CalendarPeriodEnd,
    TimeSpan CompliancePeriod,
    SloWindowKind Kind);

public sealed record SloDefinition(
    Guid Id,
    string Name,
    Guid SliId,
    string ServiceSlug,
    decimal Target,
    SloWindowKind WindowKind,
    int RollingDays,
    CalendarWindowPeriod CalendarPeriod,
    DateTimeOffset CreatedAt)
{
    public static SloDefinition Create(
        string name,
        Guid sliId,
        string serviceSlug,
        decimal target,
        SloWindowKind windowKind,
        int rollingDays,
        CalendarWindowPeriod calendarPeriod,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(serviceSlug) || sliId == Guid.Empty)
        {
            throw new DomainRuleViolationException("SLO name, service, and SLI are required.");
        }

        if (target <= 0m || target >= 1m)
        {
            throw new DomainRuleViolationException("SLO target must be expressed as a fraction strictly between zero and one.");
        }

        if (windowKind == SloWindowKind.Rolling && rollingDays <= 0)
        {
            throw new DomainRuleViolationException("Rolling SLOs require a positive rolling-day duration.");
        }

        return new SloDefinition(
            Guid.NewGuid(),
            name.Trim(),
            sliId,
            serviceSlug.Trim().ToLowerInvariant(),
            target,
            windowKind,
            rollingDays,
            calendarPeriod,
            now);
    }

    public SloWindow ResolveWindow(DateTimeOffset now)
    {
        now = now.ToUniversalTime();
        if (WindowKind == SloWindowKind.Rolling)
        {
            var period = TimeSpan.FromDays(RollingDays);
            return new SloWindow(now.Subtract(period), now, now.Add(period), period, WindowKind);
        }

        var firstMonth = CalendarPeriod == CalendarWindowPeriod.Monthly
            ? now.Month
            : ((now.Month - 1) / 3 * 3) + 1;
        var start = new DateTimeOffset(now.Year, firstMonth, 1, 0, 0, 0, TimeSpan.Zero);
        var calendarEnd = CalendarPeriod == CalendarWindowPeriod.Monthly
            ? start.AddMonths(1)
            : start.AddMonths(3);
        return new SloWindow(start, now, calendarEnd, calendarEnd - start, WindowKind);
    }
}

public sealed record SliResult(long GoodEvents, long ValidEvents, long EvaluatedWindows)
{
    public long BadEvents => Math.Max(0, ValidEvents - GoodEvents);
    public decimal Value => ValidEvents == 0 ? 1m : (decimal)GoodEvents / ValidEvents;
    public decimal ErrorRate => ValidEvents == 0 ? 0m : (decimal)BadEvents / ValidEvents;
}

public sealed record ErrorBudgetStatus(
    decimal Total,
    decimal Consumed,
    decimal Remaining,
    decimal ConsumedPercent,
    decimal RemainingPercent);

public sealed record SloStatus(
    Guid SloId,
    SloWindow Window,
    SliResult Sli,
    ErrorBudgetStatus ErrorBudget,
    decimal CurrentBurnRate,
    DateTimeOffset? ProjectedExhaustionAt);

public static class SliEvaluator
{
    public static SliResult Evaluate(
        SliDefinition definition,
        IEnumerable<MetricSample> samples,
        DateTimeOffset start,
        DateTimeOffset end)
    {
        if (end <= start)
        {
            throw new DomainRuleViolationException("SLI evaluation window end must be after its start.");
        }

        var scoped = samples
            .Where(sample => string.Equals(sample.ServiceSlug, definition.ServiceSlug, StringComparison.OrdinalIgnoreCase))
            .Where(sample => sample.Timestamp >= start && sample.Timestamp < end)
            .Where(definition.Filter.Matches)
            .ToArray();

        return definition.AggregationMode == SliAggregationMode.RequestBased
            ? EvaluateRequests(definition, scoped)
            : EvaluateWindows(definition, scoped);
    }

    private static SliResult EvaluateRequests(SliDefinition definition, IEnumerable<MetricSample> samples)
    {
        long good = 0;
        long valid = 0;
        foreach (var sample in samples)
        {
            var events = EventsFor(definition, sample, useProbeForAvailability: false);
            good += events.Good;
            valid += events.Valid;
        }

        return new SliResult(good, valid, 0);
    }

    private static SliResult EvaluateWindows(SliDefinition definition, IEnumerable<MetricSample> samples)
    {
        var groups = samples.GroupBy(sample => TruncateToMinute(sample.Timestamp));
        long goodWindows = 0;
        long validWindows = 0;
        foreach (var group in groups)
        {
            var values = group.Select(sample => EventsFor(definition, sample, useProbeForAvailability: true)).ToArray();
            var valid = values.Sum(value => value.Valid);
            if (valid == 0)
            {
                continue;
            }

            validWindows++;
            if (values.Sum(value => value.Good) == valid)
            {
                goodWindows++;
            }
        }

        return new SliResult(goodWindows, validWindows, validWindows);
    }

    private static (long Good, long Valid) EventsFor(
        SliDefinition definition,
        MetricSample sample,
        bool useProbeForAvailability)
    {
        return definition.Kind switch
        {
            SliKind.Availability when useProbeForAvailability && sample.ProbeTotalMinutes > 0 =>
                (sample.ProbeGoodMinutes, sample.ProbeTotalMinutes),
            SliKind.Availability => (sample.Requests - sample.Errors, sample.Requests),
            SliKind.Latency => (sample.RequestsBelow(definition.LatencyThresholdMilliseconds!.Value), sample.Requests),
            SliKind.Quality => (sample.QualityGoodEvents, sample.QualityValidEvents),
            SliKind.Freshness => (sample.FreshnessGoodEvents, sample.FreshnessValidEvents),
            _ => throw new DomainRuleViolationException("Unsupported SLI kind.")
        };
    }

    private static DateTimeOffset TruncateToMinute(DateTimeOffset timestamp)
    {
        var utc = timestamp.UtcDateTime;
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
    }
}

public static class ErrorBudgetCalculator
{
    public static ErrorBudgetStatus Calculate(decimal target, SliResult sli)
    {
        if (target <= 0m || target >= 1m)
        {
            throw new DomainRuleViolationException("SLO target must be a fraction strictly between zero and one.");
        }

        var total = sli.ValidEvents * (1m - target);
        if (total == 0m)
        {
            return new ErrorBudgetStatus(0m, 0m, 0m, 0m, 100m);
        }

        var consumed = sli.BadEvents;
        var remaining = Math.Max(0m, total - consumed);
        var consumedPercent = consumed / total * 100m;
        return new ErrorBudgetStatus(total, consumed, remaining, consumedPercent, remaining / total * 100m);
    }

    public static SloStatus Evaluate(
        SloDefinition slo,
        SliDefinition sli,
        IEnumerable<MetricSample> samples,
        DateTimeOffset now,
        TimeSpan? burnTrendWindow = null)
    {
        var window = slo.ResolveWindow(now);
        var materializedSamples = samples.ToArray();
        var sliResult = SliEvaluator.Evaluate(sli, materializedSamples, window.Start, window.End);
        var budget = Calculate(slo.Target, sliResult);
        var trend = burnTrendWindow ?? TimeSpan.FromHours(1);
        var trendStart = now.Subtract(trend);
        var trendResult = SliEvaluator.Evaluate(sli, materializedSamples, trendStart, now);
        var burn = BurnRateCalculator.Calculate(slo.Target, trendResult);
        var exhaustion = ProjectedExhaustionCalculator.Calculate(
            now,
            budget.RemainingPercent,
            burn,
            window.CompliancePeriod,
            slo.WindowKind == SloWindowKind.Calendar ? window.CalendarPeriodEnd : null);
        return new SloStatus(slo.Id, window, sliResult, budget, burn, exhaustion);
    }
}

public static class ProjectedExhaustionCalculator
{
    public static DateTimeOffset? Calculate(
        DateTimeOffset now,
        decimal remainingBudgetPercent,
        decimal burnRate,
        TimeSpan compliancePeriod,
        DateTimeOffset? calendarPeriodEnd = null)
    {
        if (remainingBudgetPercent <= 0m)
        {
            return now;
        }

        if (burnRate <= 0m || compliancePeriod <= TimeSpan.Zero)
        {
            return null;
        }

        var hours = (double)(remainingBudgetPercent / 100m / burnRate * (decimal)compliancePeriod.TotalHours);
        var projected = now.AddHours(hours);
        return calendarPeriodEnd.HasValue && projected > calendarPeriodEnd.Value
            ? calendarPeriodEnd
            : projected;
    }
}

public sealed record BurnRateReading(TimeSpan Window, decimal ErrorRate, decimal BurnRate, long GoodEvents, long ValidEvents);

public static class BurnRateCalculator
{
    public static decimal Calculate(decimal target, SliResult result)
    {
        var allowableErrorRate = 1m - target;
        if (allowableErrorRate <= 0m)
        {
            throw new DomainRuleViolationException("SLO target must leave a non-zero error budget.");
        }

        return result.ErrorRate / allowableErrorRate;
    }

    public static BurnRateReading Calculate(
        SloDefinition slo,
        SliDefinition sli,
        IEnumerable<MetricSample> samples,
        DateTimeOffset end,
        TimeSpan window)
    {
        var result = SliEvaluator.Evaluate(sli, samples, end.Subtract(window), end);
        return new BurnRateReading(window, result.ErrorRate, Calculate(slo.Target, result), result.GoodEvents, result.ValidEvents);
    }
}
