# ADR-003: Declarative data-quality expectations with a fail-vs-warn gate and circuit breaker

- Status: Accepted
- Date: 2026-09
- Deciders: Solo engineer (self-directed case study)

## Context

A data platform is only trustworthy if bad data cannot silently reach the serving layer, and if the
"bad data" judgement is explicit and reviewable rather than buried in transform code. We also need to
distinguish defects that must **halt promotion** (a broken load) from anomalies that should be
**recorded and surfaced** but not stop the pipeline (expected, self-healing messiness).

The synthetic source deliberately injects realistic messiness: nulls in required fields, negative
amounts, invalid FKs, duplicate ids, bad dates, mixed/unknown currencies, and — importantly —
customers that are **deleted or late-arriving** and therefore missing from the conformed dimension.

## Options considered

- **A. Imperative checks inside each transform.** Easy at first, but the rules are invisible, untestable
  in isolation, and inconsistently applied; there is no single place to see "what does 'good' mean?".
- **B. Declarative expectations as data, with a single runner.** A suite is a list of typed
  expectations (`not_null`, `unique`, `accepted_range`, `accepted_values`, `referential_integrity`,
  `freshness`, `row_count_anomaly`, `distribution_drift`), each with a **severity**. One runner
  evaluates them and produces a `DataQualityReport`. Adding a check is a one-line change.
- **C. Adopt an external DQ framework** (e.g. Great Expectations / Deequ). Powerful, but Python/JVM and
  external — violates the zero-infra directive and the from-first-principles goal.

Orthogonally, how strict is the gate?
- **Fail-closed on every failure** — any failed expectation blocks promotion. Simple but brittle:
  expected messiness (orphan late/deleted customers) would permanently wedge the pipeline.
- **Severity-graded** — `Fail` blocks (trips a circuit breaker); `Warn` is recorded and promotion
  continues.

## Decision

Adopt **B** with a **severity-graded gate**. `QualitySuites` declares the silver and gold expectations;
`DataQualityRunner` evaluates them; `CircuitBreaker.Assert` throws `CircuitBreakerException` when the
report has a **blocking (Fail)** failure, which the DAG turns into `Blocked` states for every
downstream gold task. Bad rows are **quarantined** (kept with a reason), never silently dropped.

The pivotal policy call: **referential integrity of orders->customers is `Warn` at silver, and
conformed at gold by inferring the missing dimension member.** Deleted/late customers are legitimate;
failing on them would be wrong. The **blocking** silver signal is instead a `row_count_anomaly` on the
quarantine table — if reject volume balloons past a rolling baseline, that is a real incident and the
gate fails closed.

## Consequences

- Positive: "what is good data" is one readable file; every expectation type has pass **and** fail
  unit tests (`ExpectationTests`), and the breaker's promotion-blocking is tested end-to-end
  (`PipelineTests.Tripped_silver_gate_blocks_promotion_to_gold`, `CircuitBreakerTests`).
- Positive: real runs are legible — a seed run shows silver 14 pass / 1 Warn (24 orphan orders) and
  gold 10 pass / 0 fail, and the Warn does not block because gold infers those members.
- Negative: severity is a judgement call; a mis-classified `Warn` could let a real defect through. This
  is mitigated by the quarantine-volume anomaly acting as a backstop and by making severities explicit
  and reviewable.

## Risks

- **Baseline bootstrapping**: `row_count_anomaly` skips when the baseline is <= 0 (first run), so it
  cannot trip on run one. Accepted: the baseline is established after the first successful gate and the
  demo/tests seed a baseline to exercise the trip path.
- Alert fatigue if too many checks are `Warn`. Mitigated by keeping suites small and intentional.

## Alternatives not chosen

Imperative checks (A) — invisible and untestable. External framework (C) — violates zero-infra.
Fail-on-everything — brittle against expected, self-healing messiness.
