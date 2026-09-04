# Security Review — Savanna Event-Driven Logistics Platform

## Scope and method

This review covers the repository's HTTP boundary, development JWT issuer, telemetry pipeline, in-process bus, SQLite persistence, dashboard and replay controls. It uses STRIDE as a design review, supported by code/tests; it is not a penetration test.

## Assets

- Precise current and historical vehicle locations.
- Device identity, sequence and telemetry integrity.
- Driver/fleet assignment and trip plans.
- Operational alerts, ETA and SLA state.
- Replay controls and retained event history.
- JWT signing material and production identity configuration.
- Availability of ingest and processing capacity.

## Trust boundaries

1. Vehicle/device or simulator to public ingest API.
2. Browser/operator to management APIs.
3. API process to channel partitions/background workers.
4. Application to SQLite/local filesystem.
5. Replay operator to retained event history and processing pipeline.
6. Future broker/database/observability services outside the process.

## Data classification

| Data | Classification | Rationale |
|---|---|---|
| Live/historical coordinates and routes | Sensitive operational/location | Can reveal movements, depot patterns and driver activity |
| Driver demo aliases | Synthetic in this repository | Real deployment would treat driver identity as personal data |
| Vehicle registration/capacity | Internal operational | Fleet asset metadata |
| Alerts and incident replay | Internal restricted | Reveals operational exceptions and response activity |
| JWT signing key | Secret | Enables principal impersonation |
| Metrics without raw coordinates | Internal telemetry | Lower sensitivity but can reveal fleet load/patterns |

The repository contains only fictional/synthetic data. Raw request bodies and coordinates are not deliberately written to application logs.

## Threat model (STRIDE per boundary)

| Boundary | S | T | R | I | D | E |
|---|---|---|---|---|---|---|
| Device → ingest | Stolen token or spoofed vehicle ID | Changed coordinates/sequence | Device denies sending event | Location exposed in transit/logs | Telemetry flooding/oversized batches | Device token calls operator API |
| Operator → API | Stolen operator token | Trip/geofence mutation | Operator disputes replay | Fleet/location data leakage | Expensive list/replay calls | Viewer obtains write/operations scope |
| API → channel workers | Fabricated in-process envelope | Queue/state manipulation | Missing correlation evidence | Error logs leak content | Hot partition/backlog | Compromised component invokes privileged handler |
| Application → SQLite | Wrong DB/file substitution | Direct row edits/rollback | Missing mutation audit | Database file copied | Lock exhaustion/disk full | Process account has excessive filesystem rights |
| Replay control | Impersonated incident responder | Altered time range/speed | Replay not attributable | Historical locations overexposed | Huge replay starves live processing | Unauthorized projection rebuild |

## Mitigations implemented

### Identity and authorization

- JWT signature, issuer, audience, expiry and lifetime are validated.
- Policy scopes are explicit: `fleet.read`, `fleet.write`, `telemetry.ingest`, `operations`.
- The development token issuer has fixed profiles and is disabled in Production.
- Startup refuses the default development signing key in Production.
- 401 and 403 policy behaviour is integration-tested.

### Device identity spoofing

The local token identifies a development profile, not physical hardware. A caller with `telemetry.ingest` can currently claim any `vehicleId`; this is an explicit residual risk. Production must bind a device credential/certificate to an allowed vehicle ID at the gateway, verify an HMAC or asymmetric signature over the raw canonical payload and timestamp, use constant-time signature comparison, and reject nonce/timestamp replays. Per-device key rotation and revocation are required.

### Integrity and replay safety

- `(VehicleId, SequenceNumber)` is unique in SQLite.
- SHA-256 content hashes distinguish a retry from conflicting content.
- Watermark/late/dead-letter outcomes are persisted.
- Alert fingerprints make repeated incident replay idempotent.
- Correlation IDs are honoured and returned.

### Telemetry flooding and availability

- Ingest has a token-bucket limiter partitioned by subject/IP.
- A body contains at most 10,000 pings; coordinates and numeric ranges are validated.
- Deduplication runs before database insertion.
- Channel capacity is bounded and `WriteAsync` applies backpressure.
- Consumer lag, processing lag, late events and dead letters are observable.
- Pagination caps result size.

Production additionally needs gateway-level byte/request quotas, per-device quotas, authenticated broker quotas, autoscaling on lag, replay concurrency limits, raw archive lifecycle limits and circuit-breaking of non-essential consumers.

### Location privacy

- Demo data and identities are explicitly synthetic.
- Scope policies separate read, write, ingest and operations.
- The dashboard encodes displayed values.
- Telemetry bodies are not logged by application code.
- CSP, frame denial, MIME-sniffing denial, HSTS and restrictive permissions headers are applied.

Production needs TLS termination, encryption at rest with managed keys, short purpose-based retention, location access auditing, field/row-level authorization, masked/coarsened locations for non-dispatch roles, data-subject/employment-law review, secure deletion and restrictions on observability export.

### Persistence and secrets

- EF Core parameterizes generated SQL.
- Foreign keys and unique indexes constrain relationships and idempotency.
- `.env`, key/certificate files and local database files are ignored.
- `.env.example` contains only an obvious development placeholder.

## Residual risk

- Local HS256 profiles do not establish device hardware identity.
- There is no append-only operator audit table for fleet mutations/replay invocation.
- SQLite file security depends on operating-system ACLs and host hardening.
- In-memory cache, watermark and bus state are not shared across replicas.
- A compromised process can modify both events and derived state.
- No message-level encryption protects stored location fields from a database administrator.
- CSP allows inline script/style because the dashboard is a single embedded page.

## What would change for a real production deployment

- OIDC/Entra ID for people and workload identity/mTLS plus signed payloads for devices.
- API gateway/WAF, TLS, per-device quotas and managed DDoS protections.
- Kafka/Event Hubs with durable keyed partitions, retry topics, DLQ and constrained replay jobs.
- PostgreSQL/PostGIS with encrypted storage, least-privilege roles and audited access.
- Separate hot/current and cold/raw location stores with documented retention.
- Append-only audit events for registry, trip, geofence and replay commands.
- Secret rotation through Key Vault/HSM, no application-managed long-lived signing key.
- Central security monitoring for token misuse, impossible travel, conflict rates and replay activity.
- Independent threat modelling, secure code review, dependency scanning and penetration testing.

## Explicit non-claims

This is a self-directed engineering demonstration. No formal security audit, penetration test, or compliance certification (PCI DSS, SOC 2, HIPAA, ISO 27001) has been performed or is claimed.
