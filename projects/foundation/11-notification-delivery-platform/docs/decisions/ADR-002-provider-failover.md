# ADR-002 — Provider failover and circuit breaking

## Context

Each channel has at least two provider simulators. Providers throttle, fail
transiently, fail permanently, and misbehave silently. The delivery pipeline
must fail over to a secondary quickly, refuse to hammer a dead provider, and
recover automatically when the provider comes back.

## Options

1. **Retry the same provider until success or attempt cap.**
2. **Round-robin between providers per attempt.**
3. **Primary-first with per-provider circuit breaker and half-open probe.**
4. **External library (Polly).**

## Decision

Adopt **option 3**, implemented in domain (`ProviderHealth` aggregate) and
infrastructure (`DeliveryPipeline`):

- Each provider has a `ProviderHealth` row storing `State`,
  `ConsecutiveFailures`, and `NextAttemptAt`.
- The pipeline attempts providers in the configured order. A provider whose
  breaker is `Open` is skipped (marked transient so the notification retries
  later, not fails).
- On success the breaker closes and the counter resets.
- On failure the counter increments; when it hits the threshold the breaker
  opens.
- After `OpenDuration` a single half-open probe runs; it closes the breaker
  or reopens it.

We did not adopt Polly for this project because the state machine is small
enough to test directly and we wanted the state persisted per provider
(surviving process restarts) which Polly does not do by default.

## Consequences

- The pipeline can respond to a provider going bad in a bounded number of
  attempts.
- Circuit state survives restart.
- Every provider transition is a domain event and is unit tested in
  `DomainInvariantTests.CircuitBreakerLifecycle`.

## Risks

- Wrong `ThresholdCount` will either flap the breaker (too low) or delay
  recovery (too high). Defaults are chosen conservatively and are exposed
  as options.
- Two providers failing simultaneously will exhaust the failover chain and
  fall into retry-with-backoff. That is intentional — the DLQ catches the
  message eventually.

## Alternatives revisited

If the provider count grows, the model generalises to weighted rings; that
would replace ordered iteration with a health-aware selector.
