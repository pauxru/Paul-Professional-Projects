using CloudCostObservability.Domain.Models;
using CloudCostObservability.Domain.Services;

namespace CloudCostObservability.UnitTests;

public sealed class ForecastAndAnomalyTests
{
    [Fact]
    public void Forecast_RunRate_ProjectsObservedDailyAverageAcrossMonth()
    {
        var service = new ForecastingService();
        var result = service.Forecast(new DateOnly(2026, 4, 1), [], Enumerable.Range(1, 10).Select(day => new DailyCostPoint(new DateOnly(2026, 4, day), 100m)), ForecastMethod.RunRate);
        Assert.Equal(3000m, result.ProjectedMonthEnd);
    }

    [Fact]
    public void Forecast_SeasonalAware_DoesNotTreatWeekendPatternAsGrowth()
    {
        var service = new ForecastingService();
        var history = SeasonalSeries(new DateOnly(2026, 1, 1), 90);
        var observed = SeasonalSeries(new DateOnly(2026, 4, 1), 10);
        var expected = SeasonalSeries(new DateOnly(2026, 4, 1), 30).Sum(x => x.Amount);
        var result = service.Forecast(new DateOnly(2026, 4, 1), history, observed, ForecastMethod.SeasonalAware);
        Assert.Equal(expected, result.ProjectedMonthEnd);
    }

    [Fact]
    public void Forecast_LinearRegression_ProjectsKnownLinearTrend()
    {
        var service = new ForecastingService();
        var start = new DateOnly(2026, 1, 1);
        var history = Enumerable.Range(0, 59).Select(index => new DailyCostPoint(start.AddDays(index), 10m + index)).ToList();
        var month = new DateOnly(2026, 3, 1);
        var observed = Enumerable.Range(0, 10).Select(index => new DailyCostPoint(month.AddDays(index), 69m + index)).ToList();
        var actual = Enumerable.Range(0, 31).Select(index => 69m + index).Sum();
        var result = service.Forecast(month, history, observed, ForecastMethod.LinearRegression);
        Assert.Equal(actual, result.ProjectedMonthEnd);
    }

    [Fact]
    public void Forecast_Backtest_ComputesMeasuredMapeForEachMethod()
    {
        var series = SeasonalSeries(new DateOnly(2025, 1, 1), 365);
        var results = new ForecastingService().Backtest(series, 14, 2);
        Assert.Equal(3, results.Count);
        Assert.All(results, result => Assert.True(result.MonthsEvaluated >= 8));
        Assert.All(results, result => Assert.True(result.MapePercent >= 0m));
    }

    [Fact]
    public void Forecast_All_SelectsLowestMeasuredMapeAsDefault()
    {
        var service = new ForecastingService();
        var history = SeasonalSeries(new DateOnly(2025, 1, 1), 365);
        var observed = SeasonalSeries(new DateOnly(2026, 1, 1), 14);
        var all = service.ForecastAll(new DateOnly(2026, 1, 1), history, observed);
        var defaultForecast = Assert.Single(all, x => x.IsDefault);
        Assert.Equal(all.Min(x => x.MeasuredMape), defaultForecast.MeasuredMape);
    }

    [Fact]
    public void Forecast_ProvidesConfidenceIntervalAroundProjection()
    {
        var result = new ForecastingService().Forecast(new DateOnly(2026, 4, 1), SeasonalSeries(new DateOnly(2026, 1, 1), 90), SeasonalSeries(new DateOnly(2026, 4, 1), 10), ForecastMethod.SeasonalAware);
        Assert.True(result.LowerBound < result.ProjectedMonthEnd);
        Assert.True(result.UpperBound > result.ProjectedMonthEnd);
    }

    [Fact]
    public void AnomalyDetector_DetectsInjectedSingleDaySpike()
    {
        var start = new DateOnly(2026, 1, 1);
        var points = Enumerable.Range(0, 60)
            .Select(index => new SeriesPoint(start.AddDays(index), "resource", "vm-prod-14", index == 50 ? 500m : 100m))
            .ToList();
        var anomalies = new AnomalyDetectionService().Detect(points, rollingWindow: 21);
        var anomaly = Assert.Single(anomalies, x => x.DetectedOn == start.AddDays(50));
        Assert.Equal("vm-prod-14", anomaly.Dimension);
        Assert.True(anomaly.SeverityScore >= 4m);
    }

    [Fact]
    public void AnomalyDetector_DoesNotFlagPredictableWeeklySeasonality()
    {
        var start = new DateOnly(2026, 1, 1);
        var points = Enumerable.Range(0, 90)
            .Select(index =>
            {
                var date = start.AddDays(index);
                return new SeriesPoint(date, "team", "commerce", date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 45m : 120m);
            });
        var anomalies = new AnomalyDetectionService().Detect(points, rollingWindow: 28);
        Assert.Empty(anomalies);
    }

    [Fact]
    public void AnomalyDetector_SuppressesPlannedWindowWithoutDroppingEvidence()
    {
        var start = new DateOnly(2026, 1, 1);
        var spikeDate = start.AddDays(45);
        var points = Enumerable.Range(0, 60).Select(index => new SeriesPoint(start.AddDays(index), "service", "migration", index == 45 ? 500m : 100m));
        var anomalies = new AnomalyDetectionService().Detect(points, [new PlannedSuppression(spikeDate, spikeDate, "migration", "planned migration")], 21);
        var anomaly = Assert.Single(anomalies, x => x.DetectedOn == spikeDate);
        Assert.True(anomaly.Suppressed);
    }

    [Fact]
    public void AnomalyDetector_GroupsRelatedEventsAndAttributesLeadingContribution()
    {
        var detector = new AnomalyDetectionService();
        var date = new DateOnly(2026, 4, 1);
        var large = new AnomalyEvent("a", date, "service", "Compute", 180m, 100m, 9m, AnomalySeverity.High, AnomalyDetector.RollingMad, "x", "2026-04-01|total");
        var small = new AnomalyEvent("b", date, "service", "Storage", 120m, 100m, 5m, AnomalySeverity.Medium, AnomalyDetector.RollingMad, "x", "2026-04-01|total");
        var group = Assert.Single(detector.GroupRelated([large, small]));
        Assert.Contains("80%", group.ContributionExplanation);
        Assert.Equal(2, group.Events.Count);
    }

    [Fact]
    public void AnomalyEvaluation_ComputesPrecisionAndRecallAgainstKnownInjectionIds()
    {
        var evaluation = new AnomalyDetectionService().Evaluate(["a", "b", "noise"], ["a", "b", "missing"]);
        Assert.Equal(2, evaluation.TruePositives);
        Assert.Equal(1, evaluation.FalsePositives);
        Assert.Equal(1, evaluation.FalseNegatives);
        Assert.Equal(66.67m, evaluation.PrecisionPercent);
        Assert.Equal(66.67m, evaluation.RecallPercent);
    }

    [Fact]
    public void AnomalyDetector_DeduplicatesSameGrainDimensionAndDate()
    {
        var start = new DateOnly(2026, 1, 1);
        var points = Enumerable.Range(0, 50).Select(index => new SeriesPoint(start.AddDays(index), "resource", "r1", index == 40 ? 500m : 100m));
        var anomalies = new AnomalyDetectionService().Detect(points, rollingWindow: 21);
        Assert.Equal(anomalies.Count, anomalies.Select(x => x.Id).Distinct().Count());
    }

    [Fact]
    public void AnomalyDetector_DeduplicateRelated_CondensesConsecutiveIncidentCandidates()
    {
        var detector = new AnomalyDetectionService();
        var date = new DateOnly(2026, 1, 1);
        var candidates = new[]
        {
            new AnomalyEvent("one", date, "resource", "vm-1", 200m, 100m, 5m, AnomalySeverity.Medium, AnomalyDetector.Cusum, "x", "g1"),
            new AnomalyEvent("two", date.AddDays(1), "resource", "vm-1", 220m, 100m, 9m, AnomalySeverity.High, AnomalyDetector.Cusum, "x", "g2"),
            new AnomalyEvent("three", date.AddDays(20), "resource", "vm-1", 180m, 100m, 4m, AnomalySeverity.Low, AnomalyDetector.RollingMad, "x", "g3")
        };
        var deduplicated = detector.DeduplicateRelated(candidates);
        Assert.Equal(2, deduplicated.Count);
        Assert.Contains(deduplicated, anomaly => anomaly.Id == "two");
    }

    private static IReadOnlyList<DailyCostPoint> SeasonalSeries(DateOnly start, int days) =>
        Enumerable.Range(0, days).Select(index =>
        {
            var date = start.AddDays(index);
            return new DailyCostPoint(date, date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 50m : 100m);
        }).ToList();
}
