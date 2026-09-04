# ADR-002: Support both open and closed load models, default to open for CI gating

* **Status:** Accepted
* **Date:** 2026-09-02

## Context

Load-testing tools are either **closed-model** (N virtual users, each running requests
sequentially — throughput is what the system happens to allow) or **open-model** (arrivals
are scheduled at wall-clock intervals — throughput is what *we* demand, latency is what the
system does under that demand). The two models measure genuinely different things and are
not interchangeable.

## Options considered

### Closed model only
* Pros: simpler implementation, matches how JMeter and older LoadRunner worked.
* Cons: **hides overload** — when the server slows down, the offered rate slows down with it
  because the same N VUs are stuck waiting. You measure a stable p95 right up until the server
  falls over.
* Real-world fit: user-session pools (a fixed number of concurrent sessions) *do* behave this
  way, so the model has genuine uses.

### Open model only
* Pros: the theoretically correct model for anything driven by external arrivals (public API,
  event stream, background jobs) — the offered load is decoupled from server speed, and
  slowness manifests as queueing (which the toolkit surfaces via intended-start latency).
* Cons: implementation is harder — needs a scheduler + a worker pool + backpressure. Also,
  many real systems (background workers, DB clients) are closed by nature; forcing open on
  them models a fictional scenario.

### Both, with a clear default for CI (chosen)
* Pros: the tool matches the reality of what's being tested. CI defaults to open with a
  `MaxVUs` guard because open is what catches queueing regressions.
* Cons: more code to test, and users must understand which they picked. Documented in
  `docs/methodology.md`.

## Decision

Ship both. The CLI defaults to whatever the scenario says. **Recommend open (constant or
ramping arrival rate) for CI gating** because that's where overload amplification shows up
before it becomes a production incident.

## Consequences

* `MetricsCollector` tracks two latency series: *service* (measured request duration) and
  *intended-start* (wall-clock time from when the request should have started to when it
  completed). Both are exposed in reports and both can be asserted.
* The open-model scheduler uses a bounded `Channel<T>` between the arrival ticker and the
  worker pool. When workers can't keep up, the channel accumulates — the scheduler does not
  back off, which is exactly the behaviour that reveals queueing.
* Closed-model tests remain useful for sizing user-session pools and for chaos experiments
  where you don't want the offered load to be independent.

## Risks

* Users may misinterpret which model they need. **Mitigation:** the methodology doc has a
  decision tree; ADRs cross-reference this.
* Under a hopelessly slow server, an unbounded open-model schedule would OOM. **Mitigation:**
  `MaxVUs` caps the worker pool; the scheduler backpressures when the channel is full.

## Alternatives

* Copy k6's `scenarios: { ... executor: 'constant-arrival-rate' }` DSL directly — considered
  and rejected as it would blur the boundary between "we implemented this" and "we copied
  the DSL surface".
