using System.Diagnostics;
using Microsoft.Extensions.Options;
using SavannaLogistics.Application;
using SavannaLogistics.Domain;

namespace SavannaLogistics.Infrastructure;

public sealed class ReplayService(
    ILogisticsRepository repository,
    ITelemetryEventBus eventBus,
    EtaCalculator etaCalculator,
    AlertRuleEngine alertRuleEngine,
    AlertSuppressionWindow suppression,
    BoundaryHysteresisTracker boundaryTracker,
    GeofenceIndexCatalog geofenceCatalog) : IReplayService
{
    public async Task<ReplayResult> ReplayAsync(ReplayCommand command, CancellationToken cancellationToken)
    {
        if (command.To <= command.From) throw new ArgumentException("Replay end must be after its start.");
        if (command.Speed is not (0 or 1 or 10)) throw new ArgumentException("Replay speed must be 0 (max), 1, or 10.");

        var events = await repository.GetPingsAsync(command.From, command.To, command.VehicleId, cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        DateTimeOffset? previous = null;
        foreach (var ping in events.OrderBy(ping => ping.DeviceTimestamp).ThenBy(ping => ping.SequenceNumber))
        {
            if (command.Speed > 0 && previous.HasValue)
            {
                var delay = TimeSpan.FromTicks((ping.DeviceTimestamp - previous.Value).Ticks / (long)command.Speed);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }

            await eventBus.PublishAsync(new ProcessingEnvelope(ping, true), cancellationToken);
            previous = ping.DeviceTimestamp;
        }

        await WaitForDrainAsync(cancellationToken);
        stopwatch.Stop();
        return new ReplayResult(events.Count, events.Count, stopwatch.Elapsed, command.Speed);
    }

    public async Task<int> RebuildProjectionAsync(CancellationToken cancellationToken)
    {
        await repository.ClearVehicleStatesAsync(cancellationToken);
        etaCalculator.Clear();
        alertRuleEngine.Clear();
        suppression.Clear();
        boundaryTracker.Clear();
        geofenceCatalog.ClearMembership();

        var events = await repository.GetPingsAsync(
            DateTimeOffset.MinValue,
            DateTimeOffset.MaxValue,
            null,
            cancellationToken);
        foreach (var ping in events
                     .OrderBy(ping => ping.VehicleId)
                     .ThenBy(ping => ping.SequenceNumber))
        {
            await eventBus.PublishAsync(new ProcessingEnvelope(ping, true), cancellationToken);
        }

        await WaitForDrainAsync(cancellationToken);
        return events.Count;
    }

    private async Task WaitForDrainAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        while (eventBus.ConsumerLag > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopwatch.Elapsed >= TimeSpan.FromSeconds(30))
            {
                throw new TimeoutException("Telemetry replay did not drain within 30 seconds.");
            }

            await Task.Delay(10, cancellationToken);
        }
    }
}

public sealed class TelemetrySimulatorService(
    ILogisticsRepository repository,
    ITelemetryIngestion ingestion,
    IClock clock) : ISimulatorService
{
    private static readonly GeoPoint[] DefaultRoute =
    [
        new(-1.286389, 36.817223),
        new(-1.270700, 36.834100),
        new(-1.248900, 36.871500),
        new(-1.225500, 36.900600),
        new(-1.210700, 36.923100)
    ];

    public async Task<SimulatorResult> RunAsync(SimulatorCommand command, CancellationToken cancellationToken)
    {
        Validate(command);
        var random = new Random(command.Seed);
        var vehicles = await EnsureVehiclesAsync(command.VehicleCount, cancellationToken);
        var route = await EnsureRouteAsync(cancellationToken);
        await EnsureTripsAsync(vehicles, route, cancellationToken);

        var generated = 0;
        var dropped = 0;
        var duplicateCount = 0;
        var swaps = 0;
        var delivery = new List<VehiclePingInput>();
        var baseSequence = clock.UtcNow.ToUnixTimeMilliseconds() * 1_000;
        var interval = TimeSpan.FromSeconds(1d / command.PingsPerSecond);
        var start = clock.UtcNow - TimeSpan.FromTicks(interval.Ticks * command.PingsPerVehicle);

        for (var vehicleIndex = 0; vehicleIndex < vehicles.Count; vehicleIndex++)
        {
            var clockSkew = random.Next(-command.ClockSkewSeconds, command.ClockSkewSeconds + 1);
            for (var index = 0; index < command.PingsPerVehicle; index++)
            {
                generated++;
                var progress = command.PingsPerVehicle == 1
                    ? 0d
                    : index / (double)(command.PingsPerVehicle - 1);
                var inSyntheticTunnel = progress is >= 0.45 and <= 0.55;
                var dropoutProbability = inSyntheticTunnel
                    ? Math.Min(1, command.DropoutRate * 4 + 0.25)
                    : command.DropoutRate;
                if (random.NextDouble() < dropoutProbability)
                {
                    dropped++;
                    continue;
                }

                var point = Interpolate(DefaultRoute, progress);
                point = AddJitter(point, command.GpsJitterMetres, random);
                var next = Interpolate(DefaultRoute, Math.Min(1, progress + 0.01));
                var heading = Bearing(point, next);
                var speed = index == command.PingsPerVehicle - 1 ? 0 : 42 + random.NextDouble() * 24;
                var input = new VehiclePingInput(
                    vehicles[vehicleIndex].Id,
                    point.Latitude,
                    point.Longitude,
                    speed,
                    heading,
                    30_000 + index * speed * interval.TotalHours,
                    Math.Max(5, 88 - index * 0.03),
                    true,
                    start + TimeSpan.FromTicks(interval.Ticks * index) + TimeSpan.FromSeconds(clockSkew),
                    baseSequence + vehicleIndex * command.PingsPerVehicle + index);
                delivery.Add(input);
                if (random.NextDouble() < command.DuplicateRate)
                {
                    delivery.Add(input);
                    duplicateCount++;
                }
            }
        }

        for (var index = 0; index < delivery.Count - 1; index++)
        {
            if (delivery[index].VehicleId == delivery[index + 1].VehicleId &&
                random.NextDouble() < command.OutOfOrderRate)
            {
                (delivery[index], delivery[index + 1]) = (delivery[index + 1], delivery[index]);
                swaps++;
                index++;
            }
        }

        var total = new TelemetryBatchResult(0, 0, 0, 0, 0, 0, 0);
        foreach (var batch in delivery.Chunk(10_000))
        {
            var result = await ingestion.IngestAsync(batch, cancellationToken);
            total = Add(total, result);
            if (command.RealTime)
            {
                var seconds = batch.Length / command.PingsPerSecond;
                await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
            }
        }

        return new SimulatorResult(
            vehicles.Count,
            generated,
            delivery.Count,
            dropped,
            duplicateCount,
            swaps,
            total);
    }

    private async Task<IReadOnlyList<Vehicle>> EnsureVehiclesAsync(int count, CancellationToken cancellationToken)
    {
        var page = await repository.GetVehiclesAsync(1, Math.Max(200, count), cancellationToken);
        var vehicles = page.Items.ToList();
        while (vehicles.Count < count)
        {
            var index = vehicles.Count + 1;
            var vehicle = new Vehicle(
                Guid.NewGuid(),
                $"KDL-SIM-{index:000}",
                "Isuzu",
                "NPR Simulator",
                4_500,
                24);
            await repository.AddVehicleAsync(vehicle, cancellationToken);
            vehicles.Add(vehicle);
        }

        return vehicles.Take(count).ToArray();
    }

    private async Task<RoutePlan> EnsureRouteAsync(CancellationToken cancellationToken)
    {
        var routes = await repository.GetRoutesAsync(1, 1, cancellationToken);
        if (routes.Items.Count > 0)
        {
            return routes.Items[0];
        }

        var route = new RoutePlan(Guid.NewGuid(), "Nairobi East synthetic corridor", DefaultRoute);
        var stops = new[]
        {
            new RouteStop(Guid.NewGuid(), route.Id, 0, "CBD depot", DefaultRoute[0], 0.18, 1),
            new RouteStop(Guid.NewGuid(), route.Id, 1, "Industrial Area", DefaultRoute[2], 0.18, 1),
            new RouteStop(Guid.NewGuid(), route.Id, 2, "Embakasi hub", DefaultRoute[^1], 0.18, 1)
        };
        await repository.AddRouteAsync(route, stops, cancellationToken);
        return route;
    }

    private async Task EnsureTripsAsync(
        IReadOnlyList<Vehicle> vehicles,
        RoutePlan route,
        CancellationToken cancellationToken)
    {
        foreach (var vehicle in vehicles)
        {
            if (await repository.GetActiveTripForVehicleAsync(vehicle.Id, cancellationToken) is not null)
            {
                continue;
            }

            var trip = new Trip(
                Guid.NewGuid(),
                vehicle.Id,
                route.Id,
                clock.UtcNow,
                clock.UtcNow.AddHours(3));
            await repository.AddTripAsync(trip, cancellationToken);
            trip.Start(clock.UtcNow);
            trip.MarkInTransit();
            await repository.SaveTripAsync(trip, cancellationToken);
        }
    }

    private static GeoPoint Interpolate(IReadOnlyList<GeoPoint> route, double progress)
    {
        var scaled = Math.Clamp(progress, 0, 1) * (route.Count - 1);
        var segment = Math.Min(route.Count - 2, (int)Math.Floor(scaled));
        var local = scaled - segment;
        return new GeoPoint(
            route[segment].Latitude + (route[segment + 1].Latitude - route[segment].Latitude) * local,
            route[segment].Longitude + (route[segment + 1].Longitude - route[segment].Longitude) * local);
    }

    private static GeoPoint AddJitter(GeoPoint point, double metres, Random random)
    {
        if (metres <= 0) return point;
        var angle = random.NextDouble() * Math.PI * 2;
        var distance = random.NextDouble() * metres;
        var latitude = point.Latitude + Math.Cos(angle) * distance / 111_195d;
        var longitude = point.Longitude +
                        Math.Sin(angle) * distance /
                        (111_195d * Math.Max(0.01, Math.Cos(point.Latitude * Math.PI / 180d)));
        return new GeoPoint(latitude, longitude);
    }

    private static double Bearing(GeoPoint from, GeoPoint to)
    {
        var latitude1 = from.Latitude * Math.PI / 180d;
        var latitude2 = to.Latitude * Math.PI / 180d;
        var longitudeDelta = (to.Longitude - from.Longitude) * Math.PI / 180d;
        var y = Math.Sin(longitudeDelta) * Math.Cos(latitude2);
        var x = Math.Cos(latitude1) * Math.Sin(latitude2) -
                Math.Sin(latitude1) * Math.Cos(latitude2) * Math.Cos(longitudeDelta);
        return (Math.Atan2(y, x) * 180d / Math.PI + 360d) % 360d;
    }

    private static TelemetryBatchResult Add(TelemetryBatchResult left, TelemetryBatchResult right) =>
        new(
            left.Received + right.Received,
            left.Accepted + right.Accepted,
            left.CacheDuplicates + right.CacheDuplicates,
            left.DatabaseDuplicates + right.DatabaseDuplicates,
            left.Conflicts + right.Conflicts,
            left.LateArrivals + right.LateArrivals,
            left.Published + right.Published);

    private static void Validate(SimulatorCommand command)
    {
        if (command.VehicleCount is < 1 or > 100) throw new ArgumentException("vehicleCount must be between 1 and 100.");
        if (command.PingsPerVehicle is < 1 or > 10_000) throw new ArgumentException("pingsPerVehicle must be between 1 and 10,000.");
        if (command.PingsPerSecond is <= 0 or > 10_000) throw new ArgumentException("pingsPerSecond must be between 0 and 10,000.");
        if (command.DuplicateRate is < 0 or > 1 ||
            command.OutOfOrderRate is < 0 or > 1 ||
            command.DropoutRate is < 0 or > 1)
            throw new ArgumentException("Fault rates must be between 0 and 1.");
        if (command.GpsJitterMetres is < 0 or > 5_000) throw new ArgumentException("gpsJitterMetres must be between 0 and 5,000.");
        if (command.ClockSkewSeconds is < 0 or > 3_600) throw new ArgumentException("clockSkewSeconds must be between 0 and 3,600.");
    }
}
