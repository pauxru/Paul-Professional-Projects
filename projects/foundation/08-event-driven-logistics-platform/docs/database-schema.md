# Database Schema

SQLite is the default local adapter. EF Core migration `InitialCreate` is committed under `src/SavannaLogistics.Infrastructure/Migrations`. Timestamps that participate in ordering are stored as UTC ticks so SQLite can order them consistently.

## Entity relationship diagram

```mermaid
erDiagram
    DRIVER o|--o{ VEHICLE : assigned
    VEHICLE ||--o{ VEHICLE_PING : emits
    VEHICLE ||--o| VEHICLE_STATE : projects
    VEHICLE ||--o{ TRIP : runs
    ROUTE ||--|{ ROUTE_STOP : contains
    ROUTE ||--o{ TRIP : plans
    VEHICLE ||--o{ ALERT : raises
    TRIP o|--o{ ALERT : contextualizes
    VEHICLE ||--o{ ETA_PREDICTION : receives
    TRIP ||--o{ ETA_PREDICTION : predicts
    ROUTE_STOP ||--o{ ETA_PREDICTION : targets
    VEHICLE_PING ||--o{ LATE_ARRIVAL : classified_as
    VEHICLE ||--o{ LATE_ARRIVAL : owns
    VEHICLE ||--o{ DEAD_LETTER : owns

    DRIVER {
        uuid Id PK
        string LicenceNumber UK
        string Name
        string PhoneAlias
        bool Active
    }
    VEHICLE {
        uuid Id PK
        string Registration UK
        uuid AssignedDriverId FK
        double CapacityKg
        double CapacityCubicMetres
        int Status
    }
    VEHICLE_PING {
        uuid Id PK
        uuid VehicleId FK
        long SequenceNumber UK_PART
        long DeviceTimestampUtcTicks
        long IngestTimestampUtcTicks
        double Latitude
        double Longitude
        string ContentHash
    }
    VEHICLE_STATE {
        uuid VehicleId PK_FK
        long LastSequenceNumber
        long LastSeenAtUtcTicks
        double Latitude
        double Longitude
        uuid CurrentTripId
        int Status
    }
    ROUTE {
        uuid Id PK
        string Name
        string PolylineJson
        double DistanceKm
    }
    ROUTE_STOP {
        uuid Id PK
        uuid RouteId FK
        int Sequence UK_PART
        double Latitude
        double Longitude
        double RadiusKm
        int DwellMinutes
    }
    TRIP {
        uuid Id PK
        uuid VehicleId FK
        uuid RouteId FK
        int Status
        int CurrentStopSequence
        int Version
        long SlaDueAtUtcTicks
    }
    ALERT {
        uuid Id PK
        uuid VehicleId FK
        uuid TripId FK
        int Type
        string Fingerprint UK
        long OccurredAtUtcTicks
    }
    ETA_PREDICTION {
        uuid Id PK
        uuid VehicleId FK
        uuid TripId FK
        uuid TargetStopId FK
        long CalculatedAtUtcTicks
        long PredictedArrivalUtcTicks
        double AbsoluteErrorSeconds
    }
    LATE_ARRIVAL {
        uuid Id PK
        uuid PingId FK
        uuid VehicleId FK
        long SequenceNumber
        string Reason
    }
    DEAD_LETTER {
        uuid Id PK
        uuid VehicleId FK
        long SequenceNumber
        string Reason
    }
```

## Tables and responsibilities

| Table | Responsibility |
|---|---|
| `Vehicles` | Fleet asset, capacity, status and optional driver assignment |
| `Drivers` | Synthetic/local driver registry |
| `VehiclePings` | Immutable retained source telemetry and idempotency authority |
| `VehicleStates` | Rebuildable latest-state projection, one row per vehicle |
| `Routes` | Planned polyline serialized as JSON plus precomputed length |
| `RouteStops` | Ordered route stop/geofence definitions |
| `Trips` | Lifecycle, current stop, SLA and optimistic version |
| `Geofences` | Circle columns or polygon JSON |
| `Alerts` | Deduplicated operational findings |
| `EtaPredictions` | Prediction history and optional actual/error measurement |
| `LateArrivals` | Events that crossed the event-time watermark |
| `DeadLetters` | Processing failures after the channel handler boundary |

## Important indexes

| Table | Index | Purpose |
|---|---|---|
| `Vehicles` | unique `Registration` | Fleet identity |
| `Drivers` | unique `LicenceNumber` | Registry integrity |
| `VehiclePings` | unique `(VehicleId, SequenceNumber)` | Final idempotency boundary |
| `VehiclePings` | `DeviceTimestamp`, `(VehicleId, DeviceTimestamp)` | Time-window replay |
| `RouteStops` | unique `(RouteId, Sequence)` | Ordered stop invariant |
| `Trips` | `(VehicleId, Status)`, `RouteId` | Active trip and route lookup |
| `VehicleStates` | `LastSeenAt`, `Status` | Offline/staleness scan |
| `Alerts` | unique `Fingerprint` | Replay/restart-safe suppression |
| `Alerts` | `(VehicleId, OccurredAt)`, `(Type, AcknowledgedAt)` | Feed and operations filters |
| `EtaPredictions` | `(VehicleId, CalculatedAt)` | Vehicle ETA history |
| `EtaPredictions` | `(TripId, TargetStopId)` | Actual-arrival accuracy update |
| `LateArrivals` | `(VehicleId, SequenceNumber)` | Device reliability investigation |
| `DeadLetters` | `FailedAt` | Operations queue |

## Concurrency and retention

`Trip.Version` is an EF concurrency token. SQLite writes pass through a narrow process-wide semaphore to avoid local `SQLITE_BUSY` storms. This is a local adapter choice, not a scale-out lock.

Production would range-partition `VehiclePings`, `Alerts` and `EtaPredictions` by event date; archive raw pings to immutable object storage; apply location-specific retention; and use PostgreSQL row/version semantics. PostGIS geography columns and GiST indexes replace JSON/grid spatial storage as described in ADR-003.
