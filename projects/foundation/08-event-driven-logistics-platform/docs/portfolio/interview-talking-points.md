# Interview Talking Points

## 1. What real problem does it solve?

It turns unreliable vehicle telemetry into trustworthy operational state. The failure cases—duplicates, reordering, dropouts, jitter and clock skew—are first-class inputs rather than test afterthoughts.

## 2. Why is the architecture non-trivial?

Stateful rules need serial history per vehicle, but a whole fleet needs parallelism. A bounded event-time buffer feeds fixed vehicle-keyed channels, preserving local order while processing partitions concurrently.

## 3. What could fail?

- a future-skewed device advances the watermark;
- a hot vehicle backs up one partition;
- the SQLite file locks/fills;
- a handler dead-letters;
- a noisy GPS boundary flaps;
- a replay duplicates side effects;
- a stolen device token spoofs another vehicle.

## 4. How does it recover?

Source pings are retained. Dead and late events have sinks. Current state is disposable and can be rebuilt by replay. Alert fingerprint uniqueness protects repeat replay. Runbooks define backlog, replay and alert-storm response.

## 5. How is it secured?

JWT issuer/audience/signature/lifetime validation, scope policies, production key guard, rate limits, bounded inputs, security headers and no raw telemetry logging. Be explicit that local profiles do not prove hardware identity; production needs per-device mTLS/signatures.

## 6. How is it tested?

58 tests cover geometry/property equivalence, event ordering, cache and DB dedup, partition order/parallelism, backpressure/dead letters, hysteresis, route alerts, ETA, FakeClock offline detection, API auth/validation, replay and a 10,000-ping batch.

## 7. How is it observed?

OpenTelemetry counters/histograms for ingest, processed, late, alerts, geofence evaluations, processing lag and consumer lag; ASP.NET traces and liveness/readiness.

## 8. What trade-offs were made?

SQLite/channels make the project runnable anywhere. A write gate and in-memory partition state trade scale-out for deterministic local behaviour. The ADRs map these seams to PostgreSQL/PostGIS and Kafka/Event Hubs.

## 9. How would it scale?

Key a durable broker topic by vehicle, autoscale a consumer group by partition lag, use PostgreSQL/PostGIS and Redis read models, archive raw events by date and isolate replay consumer groups.

## 10. What would change in an enterprise deployment?

OIDC and device PKI, signed telemetry, gateway quotas, audit logging, data retention/privacy controls, broker retries/DLQ, shadow projection rebuilds, PostGIS/H3 and externally validated ETA models.

## Useful deep dives

- Why event time and sequence are complementary.
- Why a spatial index is candidate selection, not the exact predicate.
- Why cache dedup cannot replace a unique database constraint.
- Why replay side effects need stronger idempotency than current-state updates.
- Why the simple measured ETA model is an explainable fallback, not a production ML claim.
