using System.Diagnostics;
using SavannaLogistics.Application;
using SavannaLogistics.Domain;
using Xunit.Abstractions;

namespace SavannaLogistics.UnitTests;

public sealed class SpatialIndexTests(ITestOutputHelper output)
{
    [Fact]
    public void SpatialIndex_ReturnsSameContainingSetAsBruteForce_ForRandomPoints()
    {
        var random = new Random(808);
        var geofences = Enumerable.Range(0, 250)
            .Select(index => Geofence.Circle(
                Guid.NewGuid(),
                $"circle-{index}",
                new GeoPoint(-1.5 + random.NextDouble() * 0.7, 36.6 + random.NextDouble() * 0.8),
                0.1 + random.NextDouble() * 2.5))
            .ToArray();
        var index = new GeofenceSpatialIndex(0.025);
        index.Replace(geofences);

        for (var sample = 0; sample < 1_000; sample++)
        {
            var point = new GeoPoint(-1.5 + random.NextDouble() * 0.7, 36.6 + random.NextDouble() * 0.8);
            var expected = geofences.Where(geofence => geofence.Contains(point)).Select(geofence => geofence.Id).Order().ToArray();
            var actual = index.Containing(point).Select(geofence => geofence.Id).Order().ToArray();
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void SpatialIndex_IndexesPolygonAcrossAllCoveredCells()
    {
        var polygon = Geofence.Polygon(
            Guid.NewGuid(),
            "large polygon",
            [new(-1.3, 36.7), new(-1.3, 36.9), new(-1.1, 36.9), new(-1.1, 36.7)]);
        var index = new GeofenceSpatialIndex(0.02);
        index.Replace([polygon]);
        Assert.Contains(polygon.Id, index.Candidates(new GeoPoint(-1.2, 36.8)).Select(item => item.Id));
    }

    [Fact]
    [Trait("Category", "Benchmark")]
    public void GeofenceIndexBenchmark_RecordsMeasuredCandidateReductionAndTiming()
    {
        var random = new Random(20260903);
        var geofences = Enumerable.Range(0, 2_000)
            .Select(index => Geofence.Circle(
                Guid.NewGuid(),
                $"benchmark-{index}",
                new GeoPoint(-1.6 + random.NextDouble(), 36.5 + random.NextDouble()),
                0.1 + random.NextDouble() * 1.2))
            .ToArray();
        var points = Enumerable.Range(0, 5_000)
            .Select(_ => new GeoPoint(-1.6 + random.NextDouble(), 36.5 + random.NextDouble()))
            .ToArray();
        var index = new GeofenceSpatialIndex(0.02);
        index.Replace(geofences);

        var indexedMatches = 0;
        var indexedEvaluations = 0;
        var indexedWatch = Stopwatch.StartNew();
        foreach (var point in points)
        {
            var candidates = index.Candidates(point);
            indexedEvaluations += candidates.Count;
            indexedMatches += candidates.Count(geofence => geofence.Contains(point));
        }
        indexedWatch.Stop();

        var bruteMatches = 0;
        var bruteWatch = Stopwatch.StartNew();
        foreach (var point in points)
        {
            bruteMatches += geofences.Count(geofence => geofence.Contains(point));
        }
        bruteWatch.Stop();

        Assert.Equal(bruteMatches, indexedMatches);
        output.WriteLine(
            "GEOFENCE_BENCHMARK geofences={0} points={1} bruteEvaluations={2} indexedEvaluations={3} bruteMs={4:F2} indexedMs={5:F2} matches={6}",
            geofences.Length,
            points.Length,
            geofences.Length * points.Length,
            indexedEvaluations,
            bruteWatch.Elapsed.TotalMilliseconds,
            indexedWatch.Elapsed.TotalMilliseconds,
            indexedMatches);
        Assert.True(indexedEvaluations < geofences.Length * points.Length / 20);
    }
}
