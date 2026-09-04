using Iiot.Domain;

namespace Iiot.Application;

public sealed record IngestionResult(int Accepted, int Duplicates, int Rejected);
public sealed record TelemetryRetentionPolicy(TimeSpan RawRetention, TimeSpan MinuteRollupRetention, TimeSpan HourRollupRetention);

public sealed class TelemetryIngestionService(ITelemetryStore store, IClock clock)
{
    public async Task<IngestionResult> IngestAsync(
        IReadOnlyList<TelemetryReading> readings,
        CancellationToken cancellationToken = default)
    {
        if (readings.Count == 0)
        {
            throw new DomainRuleViolation("Telemetry batch cannot be empty.");
        }

        var accepted = 0;
        var duplicates = 0;
        var rejected = 0;
        foreach (var reading in readings)
        {
            try
            {
                reading.EnsureValid();
                if (await store.TryAppendAsync(reading, cancellationToken))
                {
                    accepted++;
                }
                else
                {
                    duplicates++;
                }
            }
            catch (DomainRuleViolation)
            {
                rejected++;
            }
        }

        return new IngestionResult(accepted, duplicates, rejected);
    }

    public async Task<IReadOnlyList<TelemetryRollup>> RecalculateRollupsAsync(
        string deviceId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        var telemetry = await store.QueryTelemetryAsync(deviceId, from, to, cancellationToken);
        var minute = TelemetryRollupCalculator.Calculate(telemetry, TimeSpan.FromMinutes(1));
        var hour = TelemetryRollupCalculator.Calculate(telemetry, TimeSpan.FromHours(1));
        await store.ReplaceRollupsAsync(minute, TimeSpan.FromMinutes(1), cancellationToken);
        await store.ReplaceRollupsAsync(hour, TimeSpan.FromHours(1), cancellationToken);
        return [.. minute, .. hour];
    }

    public Task<int> ApplyRawRetentionAsync(TimeSpan retention, CancellationToken cancellationToken = default) =>
        store.PruneRawTelemetryBeforeAsync(clock.UtcNow - retention, cancellationToken);

    public async Task<(int Raw, int Minute, int Hour)> ApplyRetentionAsync(
        TelemetryRetentionPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var raw = await store.PruneRawTelemetryBeforeAsync(clock.UtcNow - policy.RawRetention, cancellationToken);
        var minute = await store.PruneRollupsBeforeAsync(TimeSpan.FromMinutes(1), clock.UtcNow - policy.MinuteRollupRetention, cancellationToken);
        var hour = await store.PruneRollupsBeforeAsync(TimeSpan.FromHours(1), clock.UtcNow - policy.HourRollupRetention, cancellationToken);
        return (raw, minute, hour);
    }
}

public static class TelemetryRollupCalculator
{
    public static IReadOnlyList<TelemetryRollup> Calculate(IReadOnlyList<TelemetryReading> readings, TimeSpan resolution)
    {
        if (resolution <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(resolution));
        }

        return readings
            .Where(reading => reading.Quality == QualityFlag.Good)
            .SelectMany(reading => Enum.GetValues<SensorMetric>().Select(metric => new
            {
                Reading = reading,
                Metric = metric,
                Value = reading.Values.GetMetric(metric),
                Bucket = Bucket(reading.DeviceTimestamp, resolution)
            }))
            .GroupBy(item => new { item.Reading.DeviceId, item.Metric, item.Bucket })
            .Select(group =>
            {
                var values = group.Select(item => item.Value).ToArray();
                var average = values.Average();
                var variance = values.Select(value => (value - average) * (value - average)).Average();
                return new TelemetryRollup(
                    group.Key.DeviceId,
                    group.Key.Metric,
                    group.Key.Bucket,
                    resolution,
                    values.Min(),
                    values.Max(),
                    average,
                    (decimal)Math.Sqrt((double)variance),
                    values.Length);
            })
            .OrderBy(rollup => rollup.BucketStart)
            .ThenBy(rollup => rollup.Metric)
            .ToArray();
    }

    private static DateTimeOffset Bucket(DateTimeOffset timestamp, TimeSpan resolution)
    {
        var ticks = timestamp.UtcDateTime.Ticks / resolution.Ticks * resolution.Ticks;
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }
}
