using Iiot.Application;
using Iiot.Device;
using Iiot.Domain;

namespace Iiot.UnitTests;

public sealed class AnomalyEvaluationTests
{
    [Fact]
    public void DeterministicSimulatorFaults_HaveMeasuredDetectionRates()
    {
        var start = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        var faults = new[]
        {
            (SimulatedFault.BearingWear, SensorMetric.VibrationMmPerSecondRms),
            (SimulatedFault.Overheating, SensorMetric.TemperatureC),
            (SimulatedFault.SensorDrift, SensorMetric.PressureBar),
            (SimulatedFault.Spike, SensorMetric.TemperatureC)
        };
        var results = faults.Select(item => MeasureFault(item.Item1, item.Item2, start)).ToArray();
        var falsePositiveRate = MeasureFalsePositiveRate(start);

        Console.WriteLine($"Anomaly measurement: {string.Join("; ", results.Select(item => $"{item.Fault}={item.Detected}/{item.Total} ({item.Rate:P1})"))}; normal={falsePositiveRate.Detected}/{falsePositiveRate.Total} ({falsePositiveRate.Rate:P1})");
        Assert.All(results, result => Assert.True(result.Rate >= 0.80m, $"{result.Fault} detection rate was {result.Rate:P1}."));
        Assert.True(falsePositiveRate.Rate <= 0.10m, $"False-positive rate was {falsePositiveRate.Rate:P1}.");
    }

    private static Measurement MeasureFault(SimulatedFault fault, SensorMetric metric, DateTimeOffset start)
    {
        var device = new SimulatedDevice("eval-device", "compressor", "plant-a", "1.0.0", 123);
        var baseline = Enumerable.Range(0, 60)
            .Select(index => device.Generate(start.AddMinutes(index)).Values.GetMetric(metric))
            .ToArray();
        // Compare the same diurnal hour on the next synthetic day rather than treating
        // normal daytime drift as a fault.
        var faultStart = start.AddDays(1);
        device.Inject(new FaultInjection(fault, faultStart));
        var detected = 0;
        for (var index = 0; index < 60; index++)
        {
            var value = device.Generate(faultStart.AddMinutes(index)).Values.GetMetric(metric);
            if (ExplainableAnomalyDetector.RollingZScore(baseline, value).IsAnomaly)
            {
                detected++;
            }
        }

        return new Measurement(fault, detected, 60);
    }

    private static Measurement MeasureFalsePositiveRate(DateTimeOffset start)
    {
        var baselineDevice = new SimulatedDevice("eval-normal-baseline", "compressor", "plant-a", "1.0.0", 123);
        var baseline = Enumerable.Range(0, 60)
            .Select(index => baselineDevice.Generate(start.AddMinutes(index)).Values.TemperatureC)
            .ToArray();
        var normalDevice = new SimulatedDevice("eval-normal-current", "compressor", "plant-a", "1.0.0", 456);
        var detected = Enumerable.Range(0, 60)
            .Select(index => normalDevice.Generate(start.AddDays(1).AddMinutes(index)).Values.TemperatureC)
            .Count(value => ExplainableAnomalyDetector.RollingZScore(baseline, value).IsAnomaly);
        return new Measurement(SimulatedFault.None, detected, 60);
    }

    private sealed record Measurement(SimulatedFault Fault, int Detected, int Total)
    {
        public decimal Rate => (decimal)Detected / Total;
    }
}
