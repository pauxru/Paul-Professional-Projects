using System.Text.Json;

namespace SavannaLogistics.Domain;

public enum VehicleStatus
{
    Available,
    Assigned,
    InTransit,
    Maintenance,
    Offline
}

public enum TripStatus
{
    Planned,
    Started,
    InTransit,
    AtStop,
    Completed,
    Aborted
}

public enum GeofenceShape
{
    Circle,
    Polygon
}

public enum AlertType
{
    RouteDeviation,
    GeofenceEntry,
    GeofenceExit,
    Speeding,
    ProlongedStop,
    HarshBraking,
    Offline,
    EtaBreach
}

public sealed class Vehicle
{
    private Vehicle()
    {
    }

    public Vehicle(
        Guid id,
        string registration,
        string make,
        string model,
        double capacityKg,
        double capacityCubicMetres)
    {
        if (id == Guid.Empty) throw new ArgumentException("Vehicle id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(registration)) throw new ArgumentException("Registration is required.", nameof(registration));
        if (capacityKg <= 0) throw new ArgumentOutOfRangeException(nameof(capacityKg));
        if (capacityCubicMetres <= 0) throw new ArgumentOutOfRangeException(nameof(capacityCubicMetres));

        Id = id;
        Registration = registration.Trim().ToUpperInvariant();
        Make = make.Trim();
        Model = model.Trim();
        CapacityKg = capacityKg;
        CapacityCubicMetres = capacityCubicMetres;
        Status = VehicleStatus.Available;
    }

    public Guid Id { get; private set; }
    public string Registration { get; private set; } = string.Empty;
    public string Make { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public double CapacityKg { get; private set; }
    public double CapacityCubicMetres { get; private set; }
    public VehicleStatus Status { get; private set; }
    public Guid? AssignedDriverId { get; private set; }

    public void AssignDriver(Guid driverId)
    {
        if (driverId == Guid.Empty) throw new ArgumentException("Driver id is required.", nameof(driverId));
        if (Status == VehicleStatus.Maintenance) throw new InvalidOperationException("A vehicle in maintenance cannot be assigned.");
        AssignedDriverId = driverId;
        Status = VehicleStatus.Assigned;
    }

    public void SetStatus(VehicleStatus status) => Status = status;
}

public sealed class Driver
{
    private Driver()
    {
    }

    public Driver(Guid id, string name, string licenceNumber, string phoneAlias)
    {
        if (id == Guid.Empty) throw new ArgumentException("Driver id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(licenceNumber)) throw new ArgumentException("Licence number is required.", nameof(licenceNumber));

        Id = id;
        Name = name.Trim();
        LicenceNumber = licenceNumber.Trim().ToUpperInvariant();
        PhoneAlias = phoneAlias.Trim();
        Active = true;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string LicenceNumber { get; private set; } = string.Empty;
    public string PhoneAlias { get; private set; } = string.Empty;
    public bool Active { get; private set; }
}

public sealed class VehiclePing
{
    private VehiclePing()
    {
    }

    public VehiclePing(
        Guid id,
        Guid vehicleId,
        GeoPoint position,
        double speedKph,
        double headingDegrees,
        double odometerKm,
        double fuelPercent,
        bool ignition,
        DateTimeOffset deviceTimestamp,
        long sequenceNumber,
        DateTimeOffset ingestTimestamp,
        string contentHash)
    {
        if (id == Guid.Empty) throw new ArgumentException("Ping id is required.", nameof(id));
        if (vehicleId == Guid.Empty) throw new ArgumentException("Vehicle id is required.", nameof(vehicleId));
        if (!position.IsValid) throw new ArgumentOutOfRangeException(nameof(position));
        if (speedKph < 0 || speedKph > 300) throw new ArgumentOutOfRangeException(nameof(speedKph));
        if (headingDegrees < 0 || headingDegrees >= 360) throw new ArgumentOutOfRangeException(nameof(headingDegrees));
        if (odometerKm < 0) throw new ArgumentOutOfRangeException(nameof(odometerKm));
        if (fuelPercent < 0 || fuelPercent > 100) throw new ArgumentOutOfRangeException(nameof(fuelPercent));
        if (sequenceNumber < 0) throw new ArgumentOutOfRangeException(nameof(sequenceNumber));

        Id = id;
        VehicleId = vehicleId;
        Latitude = position.Latitude;
        Longitude = position.Longitude;
        SpeedKph = speedKph;
        HeadingDegrees = headingDegrees;
        OdometerKm = odometerKm;
        FuelPercent = fuelPercent;
        Ignition = ignition;
        DeviceTimestamp = deviceTimestamp;
        SequenceNumber = sequenceNumber;
        IngestTimestamp = ingestTimestamp;
        ContentHash = contentHash;
    }

    public Guid Id { get; private set; }
    public Guid VehicleId { get; private set; }
    public double Latitude { get; private set; }
    public double Longitude { get; private set; }
    public double SpeedKph { get; private set; }
    public double HeadingDegrees { get; private set; }
    public double OdometerKm { get; private set; }
    public double FuelPercent { get; private set; }
    public bool Ignition { get; private set; }
    public DateTimeOffset DeviceTimestamp { get; private set; }
    public long SequenceNumber { get; private set; }
    public DateTimeOffset IngestTimestamp { get; private set; }
    public string ContentHash { get; private set; } = string.Empty;
    public GeoPoint Position => new(Latitude, Longitude);
}

public sealed class RoutePlan
{
    private RoutePlan()
    {
    }

    public RoutePlan(Guid id, string name, IReadOnlyList<GeoPoint> polyline)
    {
        if (id == Guid.Empty) throw new ArgumentException("Route id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required.", nameof(name));
        if (polyline.Count < 2) throw new ArgumentException("A route requires at least two points.", nameof(polyline));
        if (polyline.Any(point => !point.IsValid)) throw new ArgumentOutOfRangeException(nameof(polyline));

        Id = id;
        Name = name.Trim();
        PolylineJson = JsonSerializer.Serialize(polyline);
        DistanceKm = GeoMath.PolylineLengthKm(polyline);
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string PolylineJson { get; private set; } = "[]";
    public double DistanceKm { get; private set; }
    public IReadOnlyList<GeoPoint> GetPolyline() =>
        JsonSerializer.Deserialize<List<GeoPoint>>(PolylineJson) ?? [];
}

public sealed class RouteStop
{
    private RouteStop()
    {
    }

    public RouteStop(
        Guid id,
        Guid routeId,
        int sequence,
        string name,
        GeoPoint position,
        double radiusKm,
        int dwellMinutes)
    {
        if (id == Guid.Empty || routeId == Guid.Empty) throw new ArgumentException("Stop and route ids are required.");
        if (sequence < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Stop name is required.", nameof(name));
        if (!position.IsValid) throw new ArgumentOutOfRangeException(nameof(position));
        if (radiusKm <= 0) throw new ArgumentOutOfRangeException(nameof(radiusKm));
        if (dwellMinutes < 0) throw new ArgumentOutOfRangeException(nameof(dwellMinutes));

        Id = id;
        RouteId = routeId;
        Sequence = sequence;
        Name = name.Trim();
        Latitude = position.Latitude;
        Longitude = position.Longitude;
        RadiusKm = radiusKm;
        DwellMinutes = dwellMinutes;
    }

    public Guid Id { get; private set; }
    public Guid RouteId { get; private set; }
    public int Sequence { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public double Latitude { get; private set; }
    public double Longitude { get; private set; }
    public double RadiusKm { get; private set; }
    public int DwellMinutes { get; private set; }
    public GeoPoint Position => new(Latitude, Longitude);
}

public sealed class Trip
{
    private Trip()
    {
    }

    public Trip(
        Guid id,
        Guid vehicleId,
        Guid routeId,
        DateTimeOffset plannedStart,
        DateTimeOffset slaDueAt)
    {
        if (id == Guid.Empty || vehicleId == Guid.Empty || routeId == Guid.Empty)
            throw new ArgumentException("Trip, vehicle and route ids are required.");
        if (slaDueAt <= plannedStart) throw new ArgumentException("SLA due time must be after the planned start.");

        Id = id;
        VehicleId = vehicleId;
        RouteId = routeId;
        PlannedStart = plannedStart;
        SlaDueAt = slaDueAt;
        Status = TripStatus.Planned;
    }

    public Guid Id { get; private set; }
    public Guid VehicleId { get; private set; }
    public Guid RouteId { get; private set; }
    public TripStatus Status { get; private set; }
    public int CurrentStopSequence { get; private set; }
    public DateTimeOffset PlannedStart { get; private set; }
    public DateTimeOffset SlaDueAt { get; private set; }
    public DateTimeOffset? ActualStart { get; private set; }
    public DateTimeOffset? ActualCompleted { get; private set; }
    public DateTimeOffset? LastArrivalAt { get; private set; }
    public DateTimeOffset? LastDepartureAt { get; private set; }
    public int Version { get; private set; }

    public void Start(DateTimeOffset at)
    {
        if (Status != TripStatus.Planned) throw new InvalidOperationException("Only a planned trip can start.");
        Status = TripStatus.Started;
        ActualStart = at;
        Version++;
    }

    public void MarkInTransit()
    {
        if (Status is not (TripStatus.Started or TripStatus.AtStop))
            throw new InvalidOperationException("Trip cannot transition to in-transit.");
        Status = TripStatus.InTransit;
        Version++;
    }

    public void ArriveAtStop(int stopSequence, DateTimeOffset at)
    {
        if (Status is TripStatus.Completed or TripStatus.Aborted or TripStatus.Planned)
            throw new InvalidOperationException("Trip is not active.");
        if (stopSequence != CurrentStopSequence)
            throw new InvalidOperationException("Arrival is not for the current stop.");

        Status = TripStatus.AtStop;
        LastArrivalAt = at;
        Version++;
    }

    public void DepartStop(DateTimeOffset at, bool isFinalStop)
    {
        if (Status != TripStatus.AtStop) throw new InvalidOperationException("Trip is not at a stop.");
        LastDepartureAt = at;
        if (isFinalStop)
        {
            Status = TripStatus.Completed;
            ActualCompleted = at;
        }
        else
        {
            CurrentStopSequence++;
            Status = TripStatus.InTransit;
        }

        Version++;
    }

    public void Abort(DateTimeOffset at)
    {
        if (Status is TripStatus.Completed or TripStatus.Aborted)
            throw new InvalidOperationException("Trip is already terminal.");
        Status = TripStatus.Aborted;
        ActualCompleted = at;
        Version++;
    }
}

public sealed class Geofence
{
    private Geofence()
    {
    }

    private Geofence(Guid id, string name, GeofenceShape shape)
    {
        if (id == Guid.Empty) throw new ArgumentException("Geofence id is required.", nameof(id));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required.", nameof(name));
        Id = id;
        Name = name.Trim();
        Shape = shape;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public GeofenceShape Shape { get; private set; }
    public double? CenterLatitude { get; private set; }
    public double? CenterLongitude { get; private set; }
    public double? RadiusKm { get; private set; }
    public string? PolygonJson { get; private set; }

    public static Geofence Circle(Guid id, string name, GeoPoint center, double radiusKm)
    {
        if (!center.IsValid) throw new ArgumentOutOfRangeException(nameof(center));
        if (radiusKm <= 0) throw new ArgumentOutOfRangeException(nameof(radiusKm));
        return new Geofence(id, name, GeofenceShape.Circle)
        {
            CenterLatitude = center.Latitude,
            CenterLongitude = center.Longitude,
            RadiusKm = radiusKm
        };
    }

    public static Geofence Polygon(Guid id, string name, IReadOnlyList<GeoPoint> points)
    {
        if (points.Count < 3) throw new ArgumentException("A polygon needs at least three points.", nameof(points));
        if (points.Any(point => !point.IsValid)) throw new ArgumentOutOfRangeException(nameof(points));
        return new Geofence(id, name, GeofenceShape.Polygon)
        {
            PolygonJson = JsonSerializer.Serialize(points)
        };
    }

    public IReadOnlyList<GeoPoint> GetPolygon() =>
        PolygonJson is null
            ? []
            : JsonSerializer.Deserialize<List<GeoPoint>>(PolygonJson) ?? [];

    public GeoBoundingBox GetBoundingBox() =>
        Shape == GeofenceShape.Circle
            ? GeoMath.BoundingBoxForCircle(
                new GeoPoint(CenterLatitude!.Value, CenterLongitude!.Value),
                RadiusKm!.Value)
            : GeoMath.BoundingBoxForPolygon(GetPolygon());

    public bool Contains(GeoPoint point) =>
        Shape == GeofenceShape.Circle
            ? GeoMath.HaversineKm(
                point,
                new GeoPoint(CenterLatitude!.Value, CenterLongitude!.Value)) <= RadiusKm!.Value + 1e-9
            : GeoMath.PointInPolygon(point, GetPolygon());
}

public sealed class VehicleState
{
    private VehicleState()
    {
    }

    public VehicleState(Guid vehicleId)
    {
        VehicleId = vehicleId == Guid.Empty
            ? throw new ArgumentException("Vehicle id is required.", nameof(vehicleId))
            : vehicleId;
    }

    public Guid VehicleId { get; private set; }
    public double Latitude { get; private set; }
    public double Longitude { get; private set; }
    public double SpeedKph { get; private set; }
    public double HeadingDegrees { get; private set; }
    public double OdometerKm { get; private set; }
    public double FuelPercent { get; private set; }
    public bool Ignition { get; private set; }
    public long LastSequenceNumber { get; private set; } = -1;
    public DateTimeOffset LastSeenAt { get; private set; }
    public VehicleStatus Status { get; private set; } = VehicleStatus.Offline;
    public Guid? CurrentTripId { get; private set; }

    public bool Apply(VehiclePing ping, Guid? currentTripId)
    {
        if (ping.VehicleId != VehicleId) throw new InvalidOperationException("Ping belongs to another vehicle.");
        if (ping.SequenceNumber <= LastSequenceNumber) return false;

        Latitude = ping.Latitude;
        Longitude = ping.Longitude;
        SpeedKph = ping.SpeedKph;
        HeadingDegrees = ping.HeadingDegrees;
        OdometerKm = ping.OdometerKm;
        FuelPercent = ping.FuelPercent;
        Ignition = ping.Ignition;
        LastSequenceNumber = ping.SequenceNumber;
        LastSeenAt = ping.DeviceTimestamp;
        CurrentTripId = currentTripId;
        Status = currentTripId.HasValue
            ? VehicleStatus.InTransit
            : ping.Ignition ? VehicleStatus.Available : VehicleStatus.Offline;
        return true;
    }

    public bool MarkOffline(DateTimeOffset now, TimeSpan threshold)
    {
        if (LastSeenAt == default || now - LastSeenAt < threshold || Status == VehicleStatus.Offline)
            return false;
        Status = VehicleStatus.Offline;
        return true;
    }
}

public sealed class AlertRecord
{
    private AlertRecord()
    {
    }

    public AlertRecord(
        Guid id,
        Guid vehicleId,
        AlertType type,
        string message,
        string fingerprint,
        DateTimeOffset occurredAt,
        Guid? tripId = null)
    {
        Id = id;
        VehicleId = vehicleId;
        Type = type;
        Message = message;
        Fingerprint = fingerprint;
        OccurredAt = occurredAt;
        TripId = tripId;
    }

    public Guid Id { get; private set; }
    public Guid VehicleId { get; private set; }
    public Guid? TripId { get; private set; }
    public AlertType Type { get; private set; }
    public string Message { get; private set; } = string.Empty;
    public string Fingerprint { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset? AcknowledgedAt { get; private set; }
    public void Acknowledge(DateTimeOffset at) => AcknowledgedAt = at;
}

public sealed class EtaPrediction
{
    private EtaPrediction()
    {
    }

    public EtaPrediction(
        Guid id,
        Guid vehicleId,
        Guid tripId,
        Guid targetStopId,
        DateTimeOffset calculatedAt,
        DateTimeOffset predictedArrival,
        double remainingDistanceKm,
        double effectiveSpeedKph)
    {
        Id = id;
        VehicleId = vehicleId;
        TripId = tripId;
        TargetStopId = targetStopId;
        CalculatedAt = calculatedAt;
        PredictedArrival = predictedArrival;
        RemainingDistanceKm = remainingDistanceKm;
        EffectiveSpeedKph = effectiveSpeedKph;
    }

    public Guid Id { get; private set; }
    public Guid VehicleId { get; private set; }
    public Guid TripId { get; private set; }
    public Guid TargetStopId { get; private set; }
    public DateTimeOffset CalculatedAt { get; private set; }
    public DateTimeOffset PredictedArrival { get; private set; }
    public double RemainingDistanceKm { get; private set; }
    public double EffectiveSpeedKph { get; private set; }
    public DateTimeOffset? ActualArrival { get; private set; }
    public double? AbsoluteErrorSeconds { get; private set; }

    public void RecordActualArrival(DateTimeOffset actual)
    {
        ActualArrival = actual;
        AbsoluteErrorSeconds = Math.Abs((actual - PredictedArrival).TotalSeconds);
    }
}

public sealed class LateArrival
{
    private LateArrival()
    {
    }

    public LateArrival(Guid id, Guid pingId, Guid vehicleId, long sequenceNumber, DateTimeOffset receivedAt, string reason)
    {
        Id = id;
        PingId = pingId;
        VehicleId = vehicleId;
        SequenceNumber = sequenceNumber;
        ReceivedAt = receivedAt;
        Reason = reason;
    }

    public Guid Id { get; private set; }
    public Guid PingId { get; private set; }
    public Guid VehicleId { get; private set; }
    public long SequenceNumber { get; private set; }
    public DateTimeOffset ReceivedAt { get; private set; }
    public string Reason { get; private set; } = string.Empty;
}

public sealed class DeadLetter
{
    private DeadLetter()
    {
    }

    public DeadLetter(Guid id, Guid vehicleId, long sequenceNumber, DateTimeOffset failedAt, string reason)
    {
        Id = id;
        VehicleId = vehicleId;
        SequenceNumber = sequenceNumber;
        FailedAt = failedAt;
        Reason = reason;
    }

    public Guid Id { get; private set; }
    public Guid VehicleId { get; private set; }
    public long SequenceNumber { get; private set; }
    public DateTimeOffset FailedAt { get; private set; }
    public string Reason { get; private set; } = string.Empty;
}
