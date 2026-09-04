# Architecture — Northstar Claims Modernization Lab

## Intent
The project demonstrates an incremental migration, not a simultaneous rewrite. `legacy/` intentionally keeps unsafe style and behavior visible for assessment and characterization. `modern/` is a modular monolith because claims workflow, policy lookup, settlement calculation, and documents benefit from transactional consistency and straightforward deployment.

## Container view
```mermaid
flowchart TB
  Staff[Claims staff] --> Gateway[Strangler routing facade\noperational component]
  Gateway -->|legacy screens| Legacy[Legacy MVC host :5102]
  Gateway -->|migrated API routes| Api[Northstar.Api :5002]
  Legacy --> LegacyDb[(Legacy SQLite)]
  Api --> Application[Application services / ports]
  Application --> Domain[Claim aggregate\nSettlementCalculator]
  Application --> EF[EF Core adapter]
  EF --> ModernDb[(Modern SQLite default)]
  Application --> Store[IDocumentStore]
  Store --> LocalFiles[Local filesystem adapter]
  Api --> Telemetry[Correlation, logs, OTel, health]
  LegacyDb --> ACL[LegacySqliteClaimSource + importer]
  ACL --> EF
```

## Dependency boundaries
```mermaid
flowchart LR
  Api --> Infrastructure
  Api --> Application
  Infrastructure --> Application
  Application --> Domain
  Domain -. no framework or I/O references .-> Domain
```

`Northstar.Domain` owns monetary arithmetic and allowed state edges. `Northstar.Application` owns use-case orchestration and ports. `Northstar.Infrastructure` owns EF Core, SQLite/Npgsql selection, local documents, clock, and legacy anti-corruption adapter. `Northstar.Api` is the composition root and HTTP/auth/operational edge.

## Assessment-to-settlement sequence
```mermaid
sequenceDiagram
  participant A as Adjuster
  participant API as Claims endpoint
  participant S as ClaimApplicationService
  participant C as Claim aggregate
  participant DB as EF Core / SQLite
  A->>API: POST assessment {expectedVersion}
  API->>S: StartAssessmentAsync(ct)
  S->>DB: Load claim
  S->>C: StartReview, assign adjuster, set reserve
  C-->>S: state/version invariants
  S->>DB: SaveChanges with Version predicate
  alt stale write
    DB-->>S: DbUpdateConcurrencyException
    S-->>API: ConcurrencyConflictException
    API-->>A: RFC 7807 409
  else write succeeds
    DB-->>S: persisted claim
    S-->>API: claim response
    API-->>A: 200 + X-Correlation-Id
  end
```

## Cross-cutting behavior
- Modern error mapping uses `IExceptionHandler`: `400` validation, `404` missing resource, `409` stale version, `422` domain rule, and `500` unexpected fault.
- JWT scope policies are applied by endpoint: read, adjust, and approve are distinct authority boundaries.
- Rate limiting runs before auth; correlation/security-header middleware wraps all API responses including failures.
- Successful state-changing endpoints append audit evidence with actor, action, resource, timestamp, correlation/source metadata, and hashes of supplied before/returned-after representations.
- Development applies the committed initial EF migration and idempotent synthetic seed. Tests use a held-open SQLite in-memory connection and intentionally skip startup seeding/migration.

## Deployment view
The default process needs only the .NET SDK and local filesystem. `Database:Provider=Npgsql` selects the Npgsql EF adapter but requires a separately provisioned PostgreSQL-compatible environment and has not been verified on this host. Docker files are present only as unverified deployment artifacts because Docker is unavailable.
