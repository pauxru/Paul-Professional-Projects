using Northstar.Reliability.Domain.Common;
using Northstar.Reliability.Domain.Telemetry;

namespace Northstar.Reliability.Application.Simulation;

public enum SyntheticIncidentKind
{
    PartialOutage,
    LatencyDegradation,
    DependencyFailureCascade,
    DeploymentErrorSpike
}

public sealed record IncidentInjection(
    SyntheticIncidentKind Kind,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    decimal Severity)
{
    public bool IsActiveAt(DateTimeOffset at) => at >= StartsAt && at < EndsAt;
}

public sealed record TrafficSimulationRequest(
    string ServiceSlug,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    TimeSpan Resolution,
    long BaseRequestsPerMinute,
    string Endpoint,
    string Region,
    string Tier,
    decimal BackgroundErrorRate,
    IncidentInjection? Incident,
    int RandomSeed = 26026,
    IReadOnlyList<string>? CascadeServiceSlugs = null);

public sealed class SyntheticTelemetrySimulator
{
    public IReadOnlyList<MetricSample> Generate(TrafficSimulationRequest request)
    {
        if (request.EndsAt <= request.StartsAt || request.Resolution <= TimeSpan.Zero || request.BaseRequestsPerMinute <= 0)
        {
            throw new DomainRuleViolationException("Simulation requires a positive duration, resolution, and base traffic.");
        }

        if (request.BackgroundErrorRate < 0m || request.BackgroundErrorRate >= 1m)
        {
            throw new DomainRuleViolationException("Background error rate must be between zero and one.");
        }

        var random = new Random(request.RandomSeed);
        var samples = new List<MetricSample>();
        for (var at = request.StartsAt; at < request.EndsAt; at = at.Add(request.Resolution))
        {
            var diurnalMultiplier = 0.70 + 0.30 * Math.Sin(((at.UtcDateTime.Hour - 14) / 24d) * Math.PI * 2d);
            var jitter = 0.96 + random.NextDouble() * 0.08;
            var durationMinutes = (decimal)request.Resolution.TotalMinutes;
            var requests = Math.Max(1L, (long)Math.Round(request.BaseRequestsPerMinute * diurnalMultiplier * jitter * (double)durationMinutes));
            var errorRate = request.BackgroundErrorRate;
            var latencyFailureRate = 0.015m;
            var p50 = 85d + random.NextDouble() * 20d;
            var p95 = 175d + random.NextDouble() * 45d;

            if (request.Incident?.IsActiveAt(at) == true)
            {
                var severity = Math.Clamp(request.Incident.Severity, 0m, 1m);
                switch (request.Incident.Kind)
                {
                    case SyntheticIncidentKind.PartialOutage:
                        errorRate = Math.Max(errorRate, 0.08m + severity * 0.72m);
                        p95 = 550d + (double)(severity * 900m);
                        break;
                    case SyntheticIncidentKind.LatencyDegradation:
                        latencyFailureRate = 0.25m + severity * 0.65m;
                        p50 = 280d + (double)(severity * 400m);
                        p95 = 750d + (double)(severity * 1_500m);
                        break;
                    case SyntheticIncidentKind.DependencyFailureCascade:
                        errorRate = Math.Max(errorRate, 0.04m + severity * 0.46m);
                        latencyFailureRate = 0.10m + severity * 0.45m;
                        p95 = 600d + (double)(severity * 1_200m);
                        break;
                    case SyntheticIncidentKind.DeploymentErrorSpike:
                        errorRate = Math.Max(errorRate, 0.03m + severity * 0.37m);
                        break;
                }
            }

            var errors = Math.Min(requests, (long)Math.Round(requests * (double)errorRate, MidpointRounding.AwayFromZero));
            var latencyGood = Math.Min(requests, (long)Math.Round(requests * (double)(1m - latencyFailureRate), MidpointRounding.AwayFromZero));
            var qualityValid = requests;
            var qualityGood = Math.Max(0L, qualityValid - (long)Math.Round(qualityValid * (double)(errorRate / 2m)));
            var freshnessValid = requests;
            var freshnessGood = Math.Max(0L, freshnessValid - (long)Math.Round(freshnessValid * (double)(latencyFailureRate / 2m)));
            var under100 = Math.Min(latencyGood, (long)Math.Round(requests * 0.55d));
            var under300 = Math.Max(0L, latencyGood - under100);
            var under1000 = Math.Max(0L, requests - latencyGood);

            samples.Add(MetricSample.Create(
                request.ServiceSlug,
                at,
                request.Endpoint,
                request.Region,
                request.Tier,
                requests,
                errors,
                latencyGood,
                qualityGood,
                qualityValid,
                freshnessGood,
                freshnessValid,
                errors == 0 ? 1 : 0,
                1,
                p50,
                Math.Max(p50, p95),
                [
                    new LatencyHistogramBucket(100m, under100),
                    new LatencyHistogramBucket(300m, under300),
                    new LatencyHistogramBucket(1_000m, under1000)
                ],
                ResolutionFor(request.Resolution)));
        }

        return samples;
    }

    private static MetricResolution ResolutionFor(TimeSpan resolution) =>
        resolution >= TimeSpan.FromHours(1)
            ? MetricResolution.Hour
            : resolution >= TimeSpan.FromMinutes(5)
                ? MetricResolution.FiveMinutes
                : MetricResolution.Minute;
}
