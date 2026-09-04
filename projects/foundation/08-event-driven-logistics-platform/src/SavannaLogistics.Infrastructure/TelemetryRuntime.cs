using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SavannaLogistics.Application;
using SavannaLogistics.Domain;

namespace SavannaLogistics.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public static class LogisticsTelemetry
{
    public const string MeterName = "SavannaLogistics";
    public const string ActivitySourceName = "SavannaLogistics.Processing";
    public static readonly Meter Meter = new(MeterName);
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Counter<long> PingsIngested = Meter.CreateCounter<long>("logistics.pings.ingested");
    public static readonly Counter<long> PingsProcessed = Meter.CreateCounter<long>("logistics.pings.processed");
    public static readonly Counter<long> LateArrivals = Meter.CreateCounter<long>("logistics.pings.late");
    public static readonly Counter<long> AlertsEmitted = Meter.CreateCounter<long>("logistics.alerts.emitted");
    public static readonly Counter<long> GeofenceEvaluations = Meter.CreateCounter<long>("logistics.geofence.evaluations");
    public static readonly Histogram<double> ProcessingLagMs = Meter.CreateHistogram<double>("logistics.processing.lag.ms");
}

public sealed class GeofenceIndexCatalog(GeofenceSpatialIndex index)
{
    private readonly ConcurrentDictionary<Guid, Geofence> _geofences = [];
    private readonly ConcurrentDictionary<Guid, HashSet<Guid>> _activeByVehicle = [];

    public void Replace(IEnumerable<Geofence> geofences)
    {
        var array = geofences.ToArray();
        _geofences.Clear();
        foreach (var geofence in array)
        {
            _geofences[geofence.Id] = geofence;
        }

        index.Replace(array);
    }

    public IReadOnlyList<Geofence> Relevant(Guid vehicleId, GeoPoint point)
    {
        var ids = index.Candidates(point).Select(geofence => geofence.Id).ToHashSet();
        if (_activeByVehicle.TryGetValue(vehicleId, out var active))
        {
            lock (active)
            {
                ids.UnionWith(active);
            }
        }

        return ids.Where(_geofences.ContainsKey).Select(id => _geofences[id]).ToArray();
    }

    public void Mark(Guid vehicleId, Guid geofenceId, bool inside)
    {
        var active = _activeByVehicle.GetOrAdd(vehicleId, _ => []);
        lock (active)
        {
            if (inside)
            {
                active.Add(geofenceId);
            }
            else
            {
                active.Remove(geofenceId);
            }
        }
    }

    public void ClearMembership() => _activeByVehicle.Clear();
}

public sealed class TelemetryEventBusHostedService : ITelemetryEventBus, IHostedService
{
    private readonly PartitionedChannelBus<ProcessingEnvelope> _bus;
    private readonly CancellationTokenSource _shutdown = new();
    private int _stopped;

    public TelemetryEventBusHostedService(
        IServiceScopeFactory scopeFactory,
        IOptions<TelemetryOptions> options,
        ILogger<TelemetryEventBusHostedService> logger)
    {
        var configured = options.Value;
        _bus = new PartitionedChannelBus<ProcessingEnvelope>(
            configured.Partitions,
            configured.ChannelCapacityPerPartition,
            envelope => envelope.Ping.VehicleId,
            async (envelope, cancellationToken) =>
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<OrderedPingProcessor>();
                await processor.ProcessAsync(envelope, cancellationToken);
            },
            async (deadLetter, cancellationToken) =>
            {
                logger.LogError(
                    deadLetter.Exception,
                    "Telemetry event dead-lettered for vehicle {VehicleId} sequence {SequenceNumber}",
                    deadLetter.Message.Ping.VehicleId,
                    deadLetter.Message.Ping.SequenceNumber);
                await using var scope = scopeFactory.CreateAsyncScope();
                var repository = scope.ServiceProvider.GetRequiredService<ILogisticsRepository>();
                await repository.AddDeadLetterAsync(
                    new DeadLetter(
                        Guid.NewGuid(),
                        deadLetter.Message.Ping.VehicleId,
                        deadLetter.Message.Ping.SequenceNumber,
                        deadLetter.FailedAt,
                        deadLetter.Exception.Message),
                    cancellationToken);
            });

        LogisticsTelemetry.Meter.CreateObservableGauge(
            "logistics.consumer.lag",
            () => _bus.ConsumerLag);
    }

    public long ConsumerLag => _bus.ConsumerLag;
    public long DeadLetterCount => _bus.DeadLetterCount;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _bus.Start(_shutdown.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) == 1)
        {
            return;
        }

        try
        {
            await _bus.CompleteAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (TimeoutException)
        {
            await _shutdown.CancelAsync();
        }
        finally
        {
            await _shutdown.CancelAsync();
        }
    }

    public ValueTask PublishAsync(ProcessingEnvelope envelope, CancellationToken cancellationToken) =>
        _bus.PublishAsync(envelope, cancellationToken);

    public bool TryPublish(ProcessingEnvelope envelope) => _bus.TryPublish(envelope);
}

public sealed class WatermarkPublisher(
    WatermarkReorderBuffer buffer,
    ITelemetryEventBus eventBus,
    ILogisticsRepository repository,
    IClock clock)
{
    public async Task<(int Published, int Late)> PublishAsync(
        WatermarkBatch batch,
        bool isReplay,
        CancellationToken cancellationToken)
    {
        foreach (var late in batch.Late)
        {
            await repository.AddLateArrivalAsync(
                new LateArrival(
                    Guid.NewGuid(),
                    late.Id,
                    late.VehicleId,
                    late.SequenceNumber,
                    clock.UtcNow,
                    "Event arrived behind the per-vehicle event-time watermark."),
                cancellationToken);
        }

        foreach (var ready in batch.Ready)
        {
            await eventBus.PublishAsync(new ProcessingEnvelope(ready, isReplay), cancellationToken);
        }

        if (batch.Late.Count > 0)
        {
            LogisticsTelemetry.LateArrivals.Add(batch.Late.Count);
        }

        return (batch.Ready.Count, batch.Late.Count);
    }

    public Task<(int Published, int Late)> FlushExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        PublishAsync(buffer.FlushExpired(now), false, cancellationToken);
}

public sealed class TelemetryIngestionService(
    ILogisticsRepository repository,
    ExpiringLruDeduplicator deduplicator,
    WatermarkReorderBuffer buffer,
    WatermarkPublisher publisher,
    IClock clock) : ITelemetryIngestion
{
    public async Task<TelemetryBatchResult> IngestAsync(
        IReadOnlyList<VehiclePingInput> pings,
        CancellationToken cancellationToken)
    {
        if (pings.Count is < 1 or > 10_000)
        {
            throw new ArgumentException("A telemetry batch must contain between 1 and 10,000 pings.", nameof(pings));
        }

        var requestedVehicleIds = pings.Select(ping => ping.VehicleId).Distinct().ToArray();
        var existingVehicleIds = await repository.GetExistingVehicleIdsAsync(requestedVehicleIds, cancellationToken);
        var unknownVehicleIds = requestedVehicleIds.Where(id => !existingVehicleIds.Contains(id)).ToArray();
        if (unknownVehicleIds.Length > 0)
        {
            throw new ArgumentException(
                $"Unknown vehicleId value(s): {string.Join(", ", unknownVehicleIds.Take(5))}.",
                nameof(pings));
        }

        var accepted = 0;
        var cacheDuplicates = 0;
        var databaseDuplicates = 0;
        var conflicts = 0;
        var late = 0;
        var published = 0;

        var candidates = new List<VehiclePing>(pings.Count);
        foreach (var input in pings)
        {
            Validate(input);
            var hash = PingContentHasher.Hash(input);
            switch (deduplicator.CheckAndRemember(input.VehicleId, input.SequenceNumber, hash))
            {
                case DeduplicationDecision.Duplicate:
                    cacheDuplicates++;
                    continue;
                case DeduplicationDecision.Conflict:
                    conflicts++;
                    continue;
            }

            candidates.Add(new VehiclePing(
                Guid.NewGuid(),
                input.VehicleId,
                new GeoPoint(input.Latitude, input.Longitude),
                input.SpeedKph,
                input.HeadingDegrees,
                input.OdometerKm,
                input.FuelPercent,
                input.Ignition,
                input.DeviceTimestamp.ToUniversalTime(),
                input.SequenceNumber,
                clock.UtcNow,
                hash));
        }

        var persistenceResults = await repository.TryAddPingsAsync(candidates, cancellationToken);
        for (var index = 0; index < candidates.Count; index++)
        {
            var ping = candidates[index];
            var persistence = persistenceResults[index];
            if (persistence == PingPersistenceResult.Duplicate)
            {
                databaseDuplicates++;
                continue;
            }

            if (persistence == PingPersistenceResult.Conflict)
            {
                conflicts++;
                continue;
            }

            accepted++;
            var outcome = await publisher.PublishAsync(buffer.Add(ping), false, cancellationToken);
            published += outcome.Published;
            late += outcome.Late;
        }

        var flushed = await publisher.FlushExpiredAsync(clock.UtcNow, cancellationToken);
        published += flushed.Published;
        late += flushed.Late;
        LogisticsTelemetry.PingsIngested.Add(accepted);
        return new TelemetryBatchResult(
            pings.Count,
            accepted,
            cacheDuplicates,
            databaseDuplicates,
            conflicts,
            late,
            published);
    }

    private static void Validate(VehiclePingInput ping)
    {
        if (ping.VehicleId == Guid.Empty) throw new ArgumentException("vehicleId is required.");
        if (!new GeoPoint(ping.Latitude, ping.Longitude).IsValid) throw new ArgumentException("Latitude or longitude is invalid.");
        if (ping.SpeedKph is < 0 or > 300) throw new ArgumentException("speedKph must be between 0 and 300.");
        if (ping.HeadingDegrees is < 0 or >= 360) throw new ArgumentException("headingDegrees must be between 0 and less than 360.");
        if (ping.OdometerKm < 0) throw new ArgumentException("odometerKm cannot be negative.");
        if (ping.FuelPercent is < 0 or > 100) throw new ArgumentException("fuelPercent must be between 0 and 100.");
        if (ping.SequenceNumber < 0) throw new ArgumentException("sequenceNumber cannot be negative.");
    }
}

public sealed class WatermarkFlushWorker(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    ILogger<WatermarkFlushWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var publisher = scope.ServiceProvider.GetRequiredService<WatermarkPublisher>();
                await publisher.FlushExpiredAsync(clock.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to flush telemetry watermark buffers.");
            }
        }
    }
}

public sealed class OrderedPingProcessor(
    ILogisticsRepository repository,
    IOptions<AlertOptions> alertOptions,
    IOptions<TelemetryOptions> telemetryOptions,
    EtaCalculator etaCalculator,
    AlertRuleEngine alertRules,
    AlertSuppressionWindow suppression,
    BoundaryHysteresisTracker boundaryTracker,
    GeofenceIndexCatalog geofenceCatalog,
    IClock clock,
    ILogger<OrderedPingProcessor> logger)
{
    private readonly AlertOptions _alertOptions = alertOptions.Value;
    private readonly TelemetryOptions _telemetryOptions = telemetryOptions.Value;

    public async Task ProcessAsync(ProcessingEnvelope envelope, CancellationToken cancellationToken)
    {
        var ping = envelope.Ping;
        using var activity = LogisticsTelemetry.ActivitySource.StartActivity("process vehicle ping");
        activity?.SetTag("vehicle.id", ping.VehicleId);
        activity?.SetTag("telemetry.sequence", ping.SequenceNumber);
        activity?.SetTag("telemetry.replay", envelope.IsReplay);

        var state = await repository.GetVehicleStateAsync(ping.VehicleId, cancellationToken) ??
                    new VehicleState(ping.VehicleId);
        var trip = await repository.GetActiveTripForVehicleAsync(ping.VehicleId, cancellationToken);
        if (!state.Apply(ping, trip?.Id))
        {
            return;
        }

        RoutePlan? route = null;
        IReadOnlyList<RouteStop> stops = [];
        EtaComputation? eta = null;
        if (trip is not null)
        {
            route = await repository.GetRouteAsync(trip.RouteId, cancellationToken);
            stops = await repository.GetRouteStopsAsync(trip.RouteId, cancellationToken);
            var remaining = stops.Where(stop => stop.Sequence >= trip.CurrentStopSequence).OrderBy(stop => stop.Sequence).ToArray();
            if (remaining.Length > 0)
            {
                eta = etaCalculator.Compute(
                    ping.VehicleId,
                    ping.Position,
                    remaining,
                    ping.SpeedKph,
                    ping.DeviceTimestamp);
                if (!envelope.IsReplay)
                {
                    await PersistEtaAsync(ping, trip, remaining, eta, cancellationToken);
                    await ProcessTripBoundaryAsync(ping, trip, remaining[0], stops.Count, cancellationToken);
                }
            }
        }

        await ProcessNamedGeofencesAsync(ping, trip, cancellationToken);

        foreach (var candidate in alertRules.Evaluate(ping, route, trip, eta?.FinalArrival))
        {
            await EmitAlertAsync(ping, trip?.Id, candidate, cancellationToken);
        }

        await repository.UpsertVehicleStateAsync(state, cancellationToken);
        LogisticsTelemetry.PingsProcessed.Add(1);
        LogisticsTelemetry.ProcessingLagMs.Record(Math.Max(0, (clock.UtcNow - ping.IngestTimestamp).TotalMilliseconds));
        logger.LogDebug(
            "Processed ping for vehicle {VehicleId} sequence {SequenceNumber} replay={IsReplay}",
            ping.VehicleId,
            ping.SequenceNumber,
            envelope.IsReplay);
    }

    private async Task PersistEtaAsync(
        VehiclePing ping,
        Trip trip,
        IReadOnlyList<RouteStop> remaining,
        EtaComputation eta,
        CancellationToken cancellationToken)
    {
        await repository.AddEtaPredictionAsync(
            new EtaPrediction(
                Guid.NewGuid(),
                ping.VehicleId,
                trip.Id,
                remaining[0].Id,
                ping.DeviceTimestamp,
                eta.NextStopArrival,
                eta.DistanceToNextStopKm,
                eta.EffectiveSpeedKph),
            cancellationToken);

        var final = remaining[^1];
        if (final.Id != remaining[0].Id)
        {
            await repository.AddEtaPredictionAsync(
                new EtaPrediction(
                    Guid.NewGuid(),
                    ping.VehicleId,
                    trip.Id,
                    final.Id,
                    ping.DeviceTimestamp,
                    eta.FinalArrival,
                    eta.RemainingDistanceKm,
                    eta.EffectiveSpeedKph),
                cancellationToken);
        }
    }

    private async Task ProcessTripBoundaryAsync(
        VehiclePing ping,
        Trip trip,
        RouteStop stop,
        int stopCount,
        CancellationToken cancellationToken)
    {
        var inside = GeoMath.HaversineKm(ping.Position, stop.Position) <= stop.RadiusKm;
        var transition = boundaryTracker.Update(
            $"trip:{trip.Id:N}:{stop.Id:N}",
            inside,
            ping.DeviceTimestamp,
            TimeSpan.FromSeconds(_alertOptions.GeofenceEnterDwellSeconds),
            TimeSpan.FromSeconds(_alertOptions.GeofenceExitDwellSeconds));

        if (transition == BoundaryTransition.Entered && trip.Status != TripStatus.AtStop)
        {
            trip.ArriveAtStop(stop.Sequence, ping.DeviceTimestamp);
            await repository.RecordArrivalAccuracyAsync(trip.Id, stop.Id, ping.DeviceTimestamp, cancellationToken);
            await repository.SaveTripAsync(trip, cancellationToken);
            await EmitAlertAsync(
                ping,
                trip.Id,
                new AlertCandidate(AlertType.GeofenceEntry, $"stop:{stop.Id:N}", $"Arrived at stop {stop.Name}."),
                cancellationToken);
        }
        else if (transition == BoundaryTransition.Exited && trip.Status == TripStatus.AtStop)
        {
            trip.DepartStop(ping.DeviceTimestamp, stop.Sequence == stopCount - 1);
            await repository.SaveTripAsync(trip, cancellationToken);
            await EmitAlertAsync(
                ping,
                trip.Id,
                new AlertCandidate(AlertType.GeofenceExit, $"stop:{stop.Id:N}", $"Departed stop {stop.Name}."),
                cancellationToken);
        }
    }

    private async Task ProcessNamedGeofencesAsync(VehiclePing ping, Trip? trip, CancellationToken cancellationToken)
    {
        var relevant = geofenceCatalog.Relevant(ping.VehicleId, ping.Position);
        LogisticsTelemetry.GeofenceEvaluations.Add(relevant.Count);
        foreach (var geofence in relevant)
        {
            var inside = geofence.Contains(ping.Position);
            var transition = boundaryTracker.Update(
                $"named:{ping.VehicleId:N}:{geofence.Id:N}",
                inside,
                ping.DeviceTimestamp,
                TimeSpan.FromSeconds(_alertOptions.GeofenceEnterDwellSeconds),
                TimeSpan.FromSeconds(_alertOptions.GeofenceExitDwellSeconds));
            if (transition == BoundaryTransition.None)
            {
                continue;
            }

            geofenceCatalog.Mark(ping.VehicleId, geofence.Id, transition == BoundaryTransition.Entered);
            var candidate = transition == BoundaryTransition.Entered
                ? new AlertCandidate(AlertType.GeofenceEntry, geofence.Id.ToString("N"), $"Entered geofence {geofence.Name}.")
                : new AlertCandidate(AlertType.GeofenceExit, geofence.Id.ToString("N"), $"Exited geofence {geofence.Name}.");
            await EmitAlertAsync(ping, trip?.Id, candidate, cancellationToken);
        }
    }

    private async Task EmitAlertAsync(
        VehiclePing ping,
        Guid? tripId,
        AlertCandidate candidate,
        CancellationToken cancellationToken)
    {
        var suppressionWindow = TimeSpan.FromSeconds(_alertOptions.SuppressionWindowSeconds);
        if (!suppression.ShouldEmit(ping.VehicleId, candidate, ping.DeviceTimestamp))
        {
            return;
        }

        var alert = new AlertRecord(
            Guid.NewGuid(),
            ping.VehicleId,
            candidate.Type,
            candidate.Message,
            AlertSuppressionWindow.Fingerprint(ping.VehicleId, candidate, ping.DeviceTimestamp, suppressionWindow),
            ping.DeviceTimestamp,
            tripId);
        if (await repository.TryAddAlertAsync(alert, cancellationToken))
        {
            LogisticsTelemetry.AlertsEmitted.Add(1);
        }
    }
}

public sealed class OfflineDetectionWorker(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IOptions<TelemetryOptions> telemetryOptions,
    IOptions<AlertOptions> alertOptions,
    ILogger<OfflineDetectionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var repository = scope.ServiceProvider.GetRequiredService<ILogisticsRepository>();
                var states = await repository.GetVehicleStatesAsync(stoppingToken);
                var offline = OfflineDetector.Detect(
                    states,
                    clock.UtcNow,
                    TimeSpan.FromSeconds(telemetryOptions.Value.OfflineAfterSeconds));
                foreach (var state in offline)
                {
                    await repository.UpsertVehicleStateAsync(state, stoppingToken);
                    var candidate = new AlertCandidate(AlertType.Offline, "offline", "Vehicle telemetry is stale.");
                    var alert = new AlertRecord(
                        Guid.NewGuid(),
                        state.VehicleId,
                        candidate.Type,
                        candidate.Message,
                        AlertSuppressionWindow.Fingerprint(
                            state.VehicleId,
                            candidate,
                            clock.UtcNow,
                            TimeSpan.FromSeconds(alertOptions.Value.SuppressionWindowSeconds)),
                        clock.UtcNow,
                        state.CurrentTripId);
                    await repository.TryAddAlertAsync(alert, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Offline detection cycle failed.");
            }
        }
    }
}
