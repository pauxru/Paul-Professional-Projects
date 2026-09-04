using CloudCostObservability.Domain.Models;

namespace CloudCostObservability.Domain.Services;

public sealed record SeriesPoint(DateOnly Date, string Grain, string Dimension, decimal Value);
public sealed record AnomalyGroup(string GroupKey, DateOnly Date, IReadOnlyList<AnomalyEvent> Events, string ContributionExplanation);
public sealed record DetectionEvaluation(int TruePositives, int FalsePositives, int FalseNegatives, decimal PrecisionPercent, decimal RecallPercent);

public sealed class AnomalyDetectionService
{
    public IReadOnlyList<AnomalyEvent> Detect(
        IEnumerable<SeriesPoint> input,
        IEnumerable<PlannedSuppression>? suppressions = null,
        int rollingWindow = 28,
        decimal sensitivity = 2m)
    {
        var plans = suppressions?.ToList() ?? [];
        var candidates = new List<AnomalyEvent>();
        foreach (var series in input.GroupBy(point => (point.Grain, point.Dimension)))
        {
            var points = series.OrderBy(x => x.Date).ToList();
            if (points.Count <= rollingWindow) continue;
            var ewmaByWeekday = points.Take(rollingWindow)
                .GroupBy(point => point.Date.DayOfWeek)
                .ToDictionary(group => group.Key, group => group.Average(point => point.Value));
            var cusum = 0m;
            var driftCusum = 0m;
            for (var index = rollingWindow; index < points.Count; index++)
            {
                var point = points[index];
                var prior = points.Skip(Math.Max(0, index - rollingWindow)).Take(rollingWindow).ToList();
                var historicalMonthEnd = point.Date.Day >= 27
                    ? points.Take(index).Where(item => item.Date.Day >= 27).Select(item => item.Value).ToList()
                    : new List<decimal>();
                var regularHistory = point.Date.Day >= 27
                    ? points.Take(index).Where(item => item.Date.Day < 27).Select(item => item.Value).ToList()
                    : new List<decimal>();
                var seasonalPoints = prior.Where(x => x.Date.DayOfWeek == point.Date.DayOfWeek).ToList();
                var referencePoints = seasonalPoints.Count >= 3 ? seasonalPoints : prior;
                var reference = referencePoints.Select(x => x.Value).ToList();
                var median = Median(reference);
                var mad = Median(reference.Select(value => Math.Abs(value - median)));
                var scale = Math.Max(1m, mad * 1.4826m);
                var firstReference = referencePoints[0];
                var lastReference = referencePoints[^1];
                var elapsedReferenceDays = Math.Max(1, lastReference.Date.DayNumber - firstReference.Date.DayNumber);
                var trendPerDay = (lastReference.Value - firstReference.Value) / elapsedReferenceDays;
                var referenceDay = referencePoints.Average(item => (decimal)item.Date.DayNumber);
                var trendAdjustedExpected = median + trendPerDay * (point.Date.DayNumber - referenceDay);
                var longSeasonalHistory = points.Take(index).Where(item => item.Date.DayOfWeek == point.Date.DayOfWeek).ToList();
                var longTrend = longSeasonalHistory.Count < 2
                    ? trendPerDay
                    : (longSeasonalHistory[^1].Value - longSeasonalHistory[0].Value) /
                      Math.Max(1, longSeasonalHistory[^1].Date.DayNumber - longSeasonalHistory[0].Date.DayNumber);
                var trendChangeScore = Math.Abs(trendPerDay - longTrend) * rollingWindow / scale;
                var robustZ = Math.Abs(point.Value - trendAdjustedExpected) / scale;
                var mean = reference.Average() + trendPerDay * (point.Date.DayNumber - referenceDay);
                var variance = reference.Average(value => (value - median) * (value - median));
                var zScore = Math.Abs(point.Value - mean) / Math.Max(1m, (decimal)Math.Sqrt((double)variance));
                var seasonalEwma = ewmaByWeekday.GetValueOrDefault(point.Date.DayOfWeek, median);
                var ewmaScore = Math.Abs(point.Value - seasonalEwma) / scale;
                var residual = (point.Value - trendAdjustedExpected) / scale;
                cusum = Math.Max(0m, cusum + residual - .5m);
                var driftResidual = (point.Value - median) / scale;
                driftCusum = Math.Max(0m, driftCusum + driftResidual - 1.5m);
                var detector = robustZ >= ewmaScore && robustZ >= cusum && robustZ >= driftCusum && robustZ >= trendChangeScore
                    ? AnomalyDetector.RollingMad
                    : ewmaScore >= cusum && ewmaScore >= driftCusum && ewmaScore >= trendChangeScore ? AnomalyDetector.Ewma : AnomalyDetector.Cusum;
                var score = Math.Max(Math.Max(robustZ, zScore), Math.Max(ewmaScore, Math.Max(cusum, Math.Max(driftCusum, trendChangeScore))));
                ewmaByWeekday[point.Date.DayOfWeek] = .2m * point.Value + .8m * seasonalEwma;

                // First observe several calendar cycles, then treat recurring month-end batches as expected seasonality.
                if (point.Date.Day >= 27 && historicalMonthEnd.Count < 3)
                {
                    cusum = 0m;
                    driftCusum = 0m;
                    continue;
                }
                if (historicalMonthEnd.Count >= 3 && regularHistory.Count >= 14 &&
                    Median(historicalMonthEnd) > Median(regularHistory) * 1.15m)
                {
                    cusum = 0m;
                    driftCusum = 0m;
                    continue;
                }
                if (score < sensitivity) continue;
                var severity = score >= 12m ? AnomalySeverity.Critical : score >= 8m ? AnomalySeverity.High : score >= 5m ? AnomalySeverity.Medium : AnomalySeverity.Low;
                var groupKey = $"{point.Date:yyyy-MM-dd}|{point.Grain}";
                var anomaly = new AnomalyEvent(
                    $"{point.Grain}|{point.Dimension}|{point.Date:yyyyMMdd}",
                    point.Date,
                    point.Grain,
                    point.Dimension,
                    decimal.Round(point.Value, 2),
                    decimal.Round(trendAdjustedExpected, 2),
                    decimal.Round(score, 2),
                    severity,
                    detector,
                    $"{point.Dimension} observed {point.Value:0.##} against seasonal median {median:0.##}; robust score {score:0.##}.",
                    groupKey);
                if (plans.Any(plan => plan.AppliesTo(point.Date, point.Dimension)))
                    anomaly.Suppress();
                candidates.Add(anomaly);
            }
        }

        return candidates
            .GroupBy(x => x.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(x => x.SeverityScore).First())
            .OrderByDescending(x => x.SeverityScore)
            .ThenBy(x => x.DetectedOn)
            .ToList();
    }

    public IReadOnlyList<AnomalyGroup> GroupRelated(IEnumerable<AnomalyEvent> anomalies)
    {
        return anomalies
            .GroupBy(x => x.GroupKey)
            .Select(group =>
            {
                var events = group.OrderByDescending(x => x.SeverityScore).ToList();
                var deltas = events.Select(x => Math.Max(0m, x.Observed - x.Expected)).ToList();
                var totalDelta = deltas.Sum();
                var lead = events[0];
                var percentage = totalDelta == 0 ? 0m : deltas[0] / totalDelta * 100m;
                return new AnomalyGroup(
                    group.Key,
                    lead.DetectedOn,
                    events,
                    totalDelta == 0m
                        ? $"No positive contributing delta was available for {lead.Dimension}."
                        : $"{percentage:0.#}% of the grouped spike is {lead.Dimension} in {lead.Grain}.");
            })
            .OrderByDescending(x => x.Events.Max(e => e.SeverityScore))
            .ToList();
    }

    public IReadOnlyList<AnomalyEvent> DeduplicateRelated(IEnumerable<AnomalyEvent> anomalies, int gapDays = 7)
    {
        if (gapDays < 0) throw new ArgumentOutOfRangeException(nameof(gapDays));
        var representatives = new List<AnomalyEvent>();
        foreach (var dimension in anomalies.GroupBy(anomaly => (anomaly.Grain, anomaly.Dimension)))
        {
            var cluster = new List<AnomalyEvent>();
            DateOnly? previousDate = null;
            foreach (var anomaly in dimension.OrderBy(anomaly => anomaly.DetectedOn))
            {
                if (previousDate is not null && anomaly.DetectedOn.DayNumber - previousDate.Value.DayNumber > gapDays)
                {
                    representatives.Add(cluster.OrderByDescending(item => item.SeverityScore).ThenBy(item => item.DetectedOn).First());
                    cluster.Clear();
                }
                cluster.Add(anomaly);
                previousDate = anomaly.DetectedOn;
            }
            if (cluster.Count > 0)
                representatives.Add(cluster.OrderByDescending(item => item.SeverityScore).ThenBy(item => item.DetectedOn).First());
        }
        return representatives.OrderByDescending(anomaly => anomaly.SeverityScore).ThenBy(anomaly => anomaly.DetectedOn).ToList();
    }

    public DetectionEvaluation Evaluate(IEnumerable<string> detectedIds, IEnumerable<string> knownInjectedIds)
    {
        var detected = detectedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var known = knownInjectedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var truePositives = detected.Intersect(known, StringComparer.OrdinalIgnoreCase).Count();
        var falsePositives = detected.Count - truePositives;
        var falseNegatives = known.Count - truePositives;
        var precision = detected.Count == 0 ? 100m : decimal.Round(truePositives * 100m / detected.Count, 2);
        var recall = known.Count == 0 ? 100m : decimal.Round(truePositives * 100m / known.Count, 2);
        return new DetectionEvaluation(truePositives, falsePositives, falseNegatives, precision, recall);
    }

    private static decimal Median(IEnumerable<decimal> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        if (sorted.Count == 0) return 0m;
        return sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2m;
    }
}
