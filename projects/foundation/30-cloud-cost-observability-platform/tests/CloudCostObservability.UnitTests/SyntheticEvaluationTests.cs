using CloudCostObservability.Domain.Models;
using CloudCostObservability.Domain.Services;
using CloudCostObservability.Infrastructure.Ingestion;
using Xunit.Abstractions;

namespace CloudCostObservability.UnitTests;

public sealed class SyntheticEvaluationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SyntheticDataset_BacktestsForecastAndEvaluatesKnownInjectedAnomalies()
    {
        var data = new SyntheticFinOpsData();
        var resources = data.GenerateResources();
        var dailyTotals = new Dictionary<DateOnly, decimal>();
        await foreach (var line in data.CreateCostSource(resources).ReadAsync())
        {
            if (line.IsHourly) continue;
            dailyTotals[line.UsageDate] = dailyTotals.GetValueOrDefault(line.UsageDate) + line.AmortizedCost - line.Credits - line.Discounts;
        }

        var forecasting = new ForecastingService();
        var backtest = forecasting.Backtest(dailyTotals.OrderBy(point => point.Key).Select(point => new DailyCostPoint(point.Key, point.Value)), 14, 2);
        var targets = data.KnownInjectedAnomalies.Select(anomaly => anomaly.ResourceId).Distinct().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resourcePoints = new List<SeriesPoint>();
        await foreach (var line in data.CreateCostSource(resources.Where(resource => targets.Contains(resource.Id)).ToList()).ReadAsync())
        {
            if (!line.IsHourly)
                resourcePoints.Add(new SeriesPoint(line.UsageDate, "resource", line.ResourceId, line.AmortizedCost - line.Credits - line.Discounts));
        }

        var detector = new AnomalyDetectionService();
        var anomalies = detector.Detect(resourcePoints, [new PlannedSuppression(SyntheticFinOpsData.DefaultStart, SyntheticFinOpsData.DefaultEnd, "res-002", "scheduled batch")]);
        var knownUnexpectedResources = data.KnownInjectedAnomalies.Where(anomaly => anomaly.Name != "month-end-batch").Select(anomaly => anomaly.ResourceId);
        var detectedResources = anomalies.Where(anomaly => !anomaly.Suppressed).Select(anomaly => anomaly.Dimension).Distinct();
        var evaluation = detector.Evaluate(detectedResources, knownUnexpectedResources);

        output.WriteLine($"FORECAST_MAPE {string.Join(", ", backtest.Select(result => $"{result.Method}={result.MapePercent:0.00}% ({result.MonthsEvaluated} months)"))}");
        output.WriteLine($"ANOMALY_EVALUATION TP={evaluation.TruePositives} FP={evaluation.FalsePositives} FN={evaluation.FalseNegatives} Precision={evaluation.PrecisionPercent:0.00}% Recall={evaluation.RecallPercent:0.00}% Detected={anomalies.Count}");
        output.WriteLine($"ANOMALY_RESOURCES {string.Join(", ", detectedResources)}");
        output.WriteLine($"RES002_FIRST {anomalies.Where(anomaly => anomaly.Dimension == "res-002").Select(anomaly => anomaly.DetectedOn.ToString("yyyy-MM-dd")).FirstOrDefault() ?? "none"}");
        Assert.Equal(3, backtest.Count);
        Assert.True(evaluation.TruePositives >= 2, "At least two injected anomalous resource conditions should be detected.");
        Assert.True(evaluation.PrecisionPercent >= 50m);
    }
}
