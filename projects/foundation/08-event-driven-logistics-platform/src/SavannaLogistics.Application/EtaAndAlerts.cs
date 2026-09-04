using System.Collections.Concurrent;
using SavannaLogistics.Domain;

namespace SavannaLogistics.Application;

public sealed record EtaComputation(
    DateTimeOffset NextStopArrival,
    DateTimeOffset FinalArrival,
    double DistanceToNextStopKm,
    double RemainingDistanceKm,
    double EffectiveSpeedKph);

public sealed class EtaCalculator
{
    private readonly EtaOptions _options;
    private readonly ConcurrentDictionary<Guid, Queue<double>> _speedSamples = [];

    public EtaCalculator(EtaOptions options)
    {
        _options = options;
    }

    public EtaComputation Compute(
        Guid vehicleId,
        GeoPoint currentPosition,
        IReadOnlyList<RouteStop> remainingStops,
        double currentSpeedKph,
        DateTimeOffset calculatedAt)
    {
        if (remainingStops.Count == 0) throw new ArgumentException("At least one remaining stop is required.", nameof(remainingStops));
        var samples = _speedSamples.GetOrAdd(vehicleId, _ => new Queue<double>());
        double average;
        lock (samples)
        {
            if (currentSpeedKph > 1)
            {
                samples.Enqueue(currentSpeedKph);
                while (samples.Count > _options.RollingSpeedSamples)
                {
                    samples.Dequeue();
                }
            }

            average = samples.Count == 0 ? _options.MinimumEffectiveSpeedKph : samples.Average();
        }

        var effectiveSpeed = Math.Max(_options.MinimumEffectiveSpeedKph, average);
        var distanceToNext = GeoMath.HaversineKm(currentPosition, remainingStops[0].Position);
        var remainingDistance = distanceToNext;
        for (var index = 1; index < remainingStops.Count; index++)
        {
            remainingDistance += GeoMath.HaversineKm(remainingStops[index - 1].Position, remainingStops[index].Position);
        }

        var nextTravel = TimeSpan.FromHours(distanceToNext / effectiveSpeed * _options.TrafficFactor);
        var dwellMinutes = remainingStops
            .Take(Math.Max(0, remainingStops.Count - 1))
            .Sum(stop => stop.DwellMinutes > 0 ? stop.DwellMinutes : _options.DefaultDwellMinutes);
        var finalTravel = TimeSpan.FromHours(remainingDistance / effectiveSpeed * _options.TrafficFactor) +
                          TimeSpan.FromMinutes(dwellMinutes);

        return new EtaComputation(
            calculatedAt + nextTravel,
            calculatedAt + finalTravel,
            distanceToNext,
            remainingDistance,
            effectiveSpeed);
    }

    public void Clear() => _speedSamples.Clear();
}

public sealed record AlertCandidate(AlertType Type, string Context, string Message);

public sealed class AlertRuleEngine
{
    private sealed class VehicleRuleState
    {
        public VehiclePing? Previous;
        public DateTimeOffset? StoppedSince;
    }

    private readonly AlertOptions _options;
    private readonly ConcurrentDictionary<Guid, VehicleRuleState> _states = [];

    public AlertRuleEngine(AlertOptions options)
    {
        _options = options;
    }

    public IReadOnlyList<AlertCandidate> Evaluate(
        VehiclePing ping,
        RoutePlan? route,
        Trip? trip,
        DateTimeOffset? finalEta)
    {
        var alerts = new List<AlertCandidate>();
        var state = _states.GetOrAdd(ping.VehicleId, _ => new VehicleRuleState());
        lock (state)
        {
            if (ping.SpeedKph > _options.SpeedLimitKph)
            {
                alerts.Add(new AlertCandidate(
                    AlertType.Speeding,
                    "speed",
                    $"Speed {ping.SpeedKph:F1} km/h exceeded {_options.SpeedLimitKph:F1} km/h."));
            }

            if (state.Previous is not null &&
                state.Previous.SpeedKph - ping.SpeedKph >= _options.HarshBrakingDeltaKph &&
                ping.DeviceTimestamp - state.Previous.DeviceTimestamp <= TimeSpan.FromSeconds(10))
            {
                alerts.Add(new AlertCandidate(
                    AlertType.HarshBraking,
                    "braking",
                    $"Speed fell by {state.Previous.SpeedKph - ping.SpeedKph:F1} km/h."));
            }

            if (ping.Ignition && ping.SpeedKph <= 1)
            {
                state.StoppedSince ??= ping.DeviceTimestamp;
                if (ping.DeviceTimestamp - state.StoppedSince >= TimeSpan.FromSeconds(_options.ProlongedStopSeconds))
                {
                    alerts.Add(new AlertCandidate(AlertType.ProlongedStop, "stopped", "Vehicle has remained stopped with ignition on."));
                }
            }
            else
            {
                state.StoppedSince = null;
            }

            if (route is not null &&
                GeoMath.DistanceToPolylineKm(ping.Position, route.GetPolyline()) > _options.RouteCorridorKm)
            {
                alerts.Add(new AlertCandidate(AlertType.RouteDeviation, route.Id.ToString("N"), "Vehicle is outside the planned route corridor."));
            }

            if (trip is not null && finalEta.HasValue && finalEta.Value > trip.SlaDueAt)
            {
                alerts.Add(new AlertCandidate(AlertType.EtaBreach, trip.Id.ToString("N"), "Predicted arrival breaches the trip SLA."));
            }

            state.Previous = ping;
        }

        return alerts;
    }

    public void Clear() => _states.Clear();
}

public sealed class AlertSuppressionWindow
{
    private readonly TimeSpan _window;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastEmitted = [];

    public AlertSuppressionWindow(TimeSpan window)
    {
        if (window < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _window = window;
    }

    public bool ShouldEmit(Guid vehicleId, AlertCandidate candidate, DateTimeOffset occurredAt)
    {
        var key = $"{vehicleId:N}:{candidate.Type}:{candidate.Context}";
        while (true)
        {
            if (!_lastEmitted.TryGetValue(key, out var previous))
            {
                if (_lastEmitted.TryAdd(key, occurredAt)) return true;
                continue;
            }

            if (occurredAt - previous < _window)
            {
                return false;
            }

            if (_lastEmitted.TryUpdate(key, occurredAt, previous))
            {
                return true;
            }
        }
    }

    public static string Fingerprint(Guid vehicleId, AlertCandidate candidate, DateTimeOffset occurredAt, TimeSpan window)
    {
        var bucketTicks = Math.Max(1, window.Ticks);
        var bucket = occurredAt.UtcTicks / bucketTicks;
        return $"{vehicleId:N}:{candidate.Type}:{candidate.Context}:{bucket}";
    }

    public void Clear() => _lastEmitted.Clear();
}

public static class OfflineDetector
{
    public static IReadOnlyList<VehicleState> Detect(
        IEnumerable<VehicleState> states,
        DateTimeOffset now,
        TimeSpan offlineAfter)
    {
        return states.Where(state => state.MarkOffline(now, offlineAfter)).ToArray();
    }
}
