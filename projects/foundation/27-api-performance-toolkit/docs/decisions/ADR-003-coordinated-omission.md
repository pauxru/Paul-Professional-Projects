# ADR-003: Record service latency and intended-start latency (coordinated omission)

* **Status:** Accepted
* **Date:** 2026-09-02

## Context

"Coordinated omission" is Gil Tene's name for a specific measurement bug in load harnesses:
when the client waits for a slow response and *only then* issues the next request, the very
slow tail (the responses a user would actually experience) is under-sampled — because the
harness stopped "coordinated with" the server. The measured p99 looks better than the real p99.

Every homegrown load tool has to make an explicit decision about this. Ignoring it is the
default and wrong.

## Options considered

### Ignore it
* Pros: simplest.
* Cons: reports look reasonable but are systematically optimistic under overload. This is
  precisely the situation where the numbers matter most.

### Record only intended-start latency
* Pros: honest under overload.
* Cons: obscures the actual service time — you can't tell whether the server got slower or
  whether you offered too much load.

### Record both (chosen)
* Pros: separates "how long did the server take to respond, once it got the request" (service
  latency) from "how long did a user perceive, including the queueing before the server got
  the request" (intended-start latency). Both are reported and can be asserted independently.
* Cons: needs the load model to expose the *intended* start time. In the open model this is
  natural: it's when the scheduler tick fired. In the closed model there is no intended start —
  the VU is dispatching sequentially — so intended-start latency degenerates to service latency
  and is reported as such.

## Decision

Record both. In the open model, the scheduler stamps the intended start on each work item
before pushing it into the worker channel. In the closed model, intended-start = actual start
and the two histograms coincide (which is documented in the report so nobody reads more into
it than there is).

## Consequences

* Every `RequestSample` carries both `ServiceLatencyNs` and `IntendedLatencyNs`.
* `MetricsCollector` maintains two `LatencyHistogram` instances per endpoint plus aggregate.
* Reports show both series: "service p95" and "intended p95". The case study makes the
  difference visible — baseline service p95 was 67 ms while intended p95 was 142 ms; the
  latter is what a user would perceive.

## Risks

* Reports become slightly noisier — five percentile columns instead of three. **Mitigation:**
  the HTML report groups them into a single table with clear labels; the CLI summary prints
  intended p95/p99 as a separate line.
* Users may not know which to gate on. **Mitigation:** the methodology doc explicitly says
  "gate on intended p95 for user-facing APIs; gate on service p95 for internal APIs with
  bounded client concurrency".

## Alternatives

* HdrHistogram's own `recordValueWithExpectedInterval` compensates within a single histogram
  by inserting synthetic samples between the reported one and its expected sibling. Rejected
  because the compensation is opaque; two separate histograms are more legible and the
  reader can see the delta.
