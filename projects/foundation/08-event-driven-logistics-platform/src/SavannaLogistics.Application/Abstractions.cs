using System.ComponentModel.DataAnnotations;
using SavannaLogistics.Domain;

namespace SavannaLogistics.Application;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    [Required] public string Provider { get; set; } = "Sqlite";
    [Required] public string ConnectionString { get; set; } = "Data Source=savanna-logistics.db;Default Timeout=10;Cache=Shared";
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    [Required] public string Issuer { get; set; } = "SavannaLogistics";
    [Required] public string Audience { get; set; } = "SavannaLogistics.Api";
    [Required, MinLength(32)] public string SigningKey { get; set; } = "dev-only-not-a-real-secret-change-me-0123456789";
    public const string DefaultSigningKey = "dev-only-not-a-real-secret-change-me-0123456789";
}

public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";
    [Range(1, 64)] public int Partitions { get; set; } = 4;
    [Range(1, 100_000)] public int ChannelCapacityPerPartition { get; set; } = 2_048;
    [Range(0, 300)] public int AllowedLatenessSeconds { get; set; } = 5;
    [Range(16, 1_000_000)] public int DedupCacheCapacity { get; set; } = 100_000;
    [Range(1, 86_400)] public int DedupExpirySeconds { get; set; } = 900;
    [Range(1, 86_400)] public int OfflineAfterSeconds { get; set; } = 120;
}

public sealed class AlertOptions
{
    public const string SectionName = "Alerts";
    [Range(1, 250)] public double SpeedLimitKph { get; set; } = 80;
    [Range(1, 100)] public double HarshBrakingDeltaKph { get; set; } = 25;
    [Range(0.01, 100)] public double RouteCorridorKm { get; set; } = 0.5;
    [Range(1, 86_400)] public int ProlongedStopSeconds { get; set; } = 300;
    [Range(1, 86_400)] public int SuppressionWindowSeconds { get; set; } = 120;
    [Range(0, 300)] public int GeofenceEnterDwellSeconds { get; set; } = 10;
    [Range(0, 300)] public int GeofenceExitDwellSeconds { get; set; } = 10;
}

public sealed class EtaOptions
{
    public const string SectionName = "Eta";
    [Range(1, 100)] public int RollingSpeedSamples { get; set; } = 8;
    [Range(1, 200)] public double MinimumEffectiveSpeedKph { get; set; } = 12;
    [Range(0.1, 10)] public double TrafficFactor { get; set; } = 1.25;
    [Range(0, 120)] public int DefaultDwellMinutes { get; set; } = 4;
}

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
}

public enum PingPersistenceResult
{
    Inserted,
    Duplicate,
    Conflict
}

public sealed record VehiclePingInput(
    Guid VehicleId,
    double Latitude,
    double Longitude,
    double SpeedKph,
    double HeadingDegrees,
    double OdometerKm,
    double FuelPercent,
    bool Ignition,
    DateTimeOffset DeviceTimestamp,
    long SequenceNumber);

public sealed record TelemetryBatchResult(
    int Received,
    int Accepted,
    int CacheDuplicates,
    int DatabaseDuplicates,
    int Conflicts,
    int LateArrivals,
    int Published);

public sealed record ProcessingEnvelope(VehiclePing Ping, bool IsReplay);

public sealed record ReplayCommand(
    DateTimeOffset From,
    DateTimeOffset To,
    double Speed,
    Guid? VehicleId);

public sealed record ReplayResult(int EventsRead, int EventsPublished, TimeSpan Elapsed, double Speed);

public sealed record SimulatorCommand(
    int VehicleCount,
    int PingsPerVehicle,
    double PingsPerSecond,
    int Seed,
    double DuplicateRate,
    double OutOfOrderRate,
    double DropoutRate,
    double GpsJitterMetres,
    int ClockSkewSeconds,
    bool RealTime);

public sealed record SimulatorResult(
    int Vehicles,
    int Generated,
    int Delivered,
    int Dropped,
    int DuplicatesInjected,
    int OutOfOrderSwaps,
    TelemetryBatchResult Ingestion);

public interface ILogisticsRepository
{
    Task<PagedResult<Vehicle>> GetVehiclesAsync(int page, int pageSize, CancellationToken cancellationToken);
    Task<Vehicle?> GetVehicleAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlySet<Guid>> GetExistingVehicleIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken);
    Task AddVehicleAsync(Vehicle vehicle, CancellationToken cancellationToken);
    Task<PagedResult<Driver>> GetDriversAsync(int page, int pageSize, CancellationToken cancellationToken);
    Task<Driver?> GetDriverAsync(Guid id, CancellationToken cancellationToken);
    Task AddDriverAsync(Driver driver, CancellationToken cancellationToken);
    Task SaveVehicleAsync(Vehicle vehicle, CancellationToken cancellationToken);

    Task<PagedResult<RoutePlan>> GetRoutesAsync(int page, int pageSize, CancellationToken cancellationToken);
    Task<RoutePlan?> GetRouteAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<RouteStop>> GetRouteStopsAsync(Guid routeId, CancellationToken cancellationToken);
    Task AddRouteAsync(RoutePlan route, IReadOnlyList<RouteStop> stops, CancellationToken cancellationToken);

    Task<PagedResult<Geofence>> GetGeofencesAsync(int page, int pageSize, CancellationToken cancellationToken);
    Task<IReadOnlyList<Geofence>> GetAllGeofencesAsync(CancellationToken cancellationToken);
    Task AddGeofenceAsync(Geofence geofence, CancellationToken cancellationToken);

    Task<PagedResult<Trip>> GetTripsAsync(int page, int pageSize, CancellationToken cancellationToken);
    Task<Trip?> GetTripAsync(Guid id, CancellationToken cancellationToken);
    Task<Trip?> GetActiveTripForVehicleAsync(Guid vehicleId, CancellationToken cancellationToken);
    Task AddTripAsync(Trip trip, CancellationToken cancellationToken);
    Task SaveTripAsync(Trip trip, CancellationToken cancellationToken);

    Task<PingPersistenceResult> TryAddPingAsync(VehiclePing ping, CancellationToken cancellationToken);
    Task<IReadOnlyList<PingPersistenceResult>> TryAddPingsAsync(
        IReadOnlyList<VehiclePing> pings,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<VehiclePing>> GetPingsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        Guid? vehicleId,
        CancellationToken cancellationToken);
    Task<int> CountPingsAsync(CancellationToken cancellationToken);
    Task AddLateArrivalAsync(LateArrival lateArrival, CancellationToken cancellationToken);
    Task AddDeadLetterAsync(DeadLetter deadLetter, CancellationToken cancellationToken);
    Task<int> CountLateArrivalsAsync(CancellationToken cancellationToken);
    Task<int> CountDeadLettersAsync(CancellationToken cancellationToken);

    Task<VehicleState?> GetVehicleStateAsync(Guid vehicleId, CancellationToken cancellationToken);
    Task<IReadOnlyList<VehicleState>> GetVehicleStatesAsync(CancellationToken cancellationToken);
    Task UpsertVehicleStateAsync(VehicleState state, CancellationToken cancellationToken);
    Task ClearVehicleStatesAsync(CancellationToken cancellationToken);

    Task<bool> TryAddAlertAsync(AlertRecord alert, CancellationToken cancellationToken);
    Task<PagedResult<AlertRecord>> GetAlertsAsync(int page, int pageSize, CancellationToken cancellationToken);
    Task<int> CountAlertsAsync(CancellationToken cancellationToken);

    Task AddEtaPredictionAsync(EtaPrediction prediction, CancellationToken cancellationToken);
    Task<IReadOnlyList<EtaPrediction>> GetEtaHistoryAsync(Guid vehicleId, int take, CancellationToken cancellationToken);
    Task RecordArrivalAccuracyAsync(Guid tripId, Guid stopId, DateTimeOffset actualArrival, CancellationToken cancellationToken);
    Task<(int Samples, double MeanAbsoluteErrorSeconds, double P90AbsoluteErrorSeconds)> GetEtaAccuracyAsync(
        CancellationToken cancellationToken);
}

public interface ITelemetryEventBus
{
    long ConsumerLag { get; }
    long DeadLetterCount { get; }
    ValueTask PublishAsync(ProcessingEnvelope envelope, CancellationToken cancellationToken);
    bool TryPublish(ProcessingEnvelope envelope);
}

public interface ITelemetryIngestion
{
    Task<TelemetryBatchResult> IngestAsync(
        IReadOnlyList<VehiclePingInput> pings,
        CancellationToken cancellationToken);
}

public interface IReplayService
{
    Task<ReplayResult> ReplayAsync(ReplayCommand command, CancellationToken cancellationToken);
    Task<int> RebuildProjectionAsync(CancellationToken cancellationToken);
}

public interface ISimulatorService
{
    Task<SimulatorResult> RunAsync(SimulatorCommand command, CancellationToken cancellationToken);
}
