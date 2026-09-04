using SavannaLogistics.Application;
using SavannaLogistics.Domain;
using Xunit.Abstractions;

namespace SavannaLogistics.UnitTests;

public sealed class EtaAccuracyBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Benchmark")]
    public void EtaSimulatorBenchmark_RecordsPredictionAccuracyAgainstSyntheticActualArrivals()
    {
        var random = new Random(8082026);
        var errors = new List<double>();
        var startPoint = new GeoPoint(-1.286389, 36.817223);
        var endPoint = new GeoPoint(-1.210700, 36.923100);
        var routeId = Guid.NewGuid();
        var finalStop = new RouteStop(Guid.NewGuid(), routeId, 0, "Embakasi hub", endPoint, 0.2, 0);
        var totalDistance = GeoMath.HaversineKm(startPoint, endPoint);

        for (var run = 0; run < 100; run++)
        {
            var vehicleId = Guid.NewGuid();
            var actualSpeed = 35 + random.NextDouble() * 35;
            var signalDelayMinutes = random.NextDouble() * 2;
            var startedAt = new DateTimeOffset(2026, 9, 3, 6, 0, 0, TimeSpan.Zero).AddMinutes(run);
            var actualArrival = startedAt
                .AddHours(totalDistance / actualSpeed)
                .AddMinutes(signalDelayMinutes);
            var calculator = new EtaCalculator(new EtaOptions
            {
                RollingSpeedSamples = 6,
                MinimumEffectiveSpeedKph = 12,
                TrafficFactor = 1.06,
                DefaultDwellMinutes = 0
            });

            for (var sample = 0; sample < 10; sample++)
            {
                var progress = sample / 12d;
                var point = new GeoPoint(
                    startPoint.Latitude + (endPoint.Latitude - startPoint.Latitude) * progress,
                    startPoint.Longitude + (endPoint.Longitude - startPoint.Longitude) * progress);
                var observedSpeed = Math.Max(5, actualSpeed + (random.NextDouble() - 0.5) * 8);
                var observedAt = startedAt.AddHours(totalDistance * progress / actualSpeed);
                var prediction = calculator.Compute(vehicleId, point, [finalStop], observedSpeed, observedAt);
                errors.Add(Math.Abs((prediction.FinalArrival - actualArrival).TotalSeconds));
            }
        }

        errors.Sort();
        var mean = errors.Average();
        var p90 = errors[(int)Math.Ceiling(errors.Count * 0.9) - 1];
        var maximum = errors[^1];
        output.WriteLine(
            "ETA_BENCHMARK runs=100 predictions={0} meanAbsoluteErrorSeconds={1:F2} p90AbsoluteErrorSeconds={2:F2} maxAbsoluteErrorSeconds={3:F2}",
            errors.Count,
            mean,
            p90,
            maximum);
        Assert.InRange(mean, 0, 300);
        Assert.InRange(p90, 0, 420);
    }
}
