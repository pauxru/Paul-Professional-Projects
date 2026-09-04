# ADR 0003 — Wilson lower bounds, and evidence that does not carry across stages

**Status:** accepted

## Context

Once responses are being compared, something has to decide when the modern
implementation takes over. The universal default is a match rate with a
threshold: *promote at 99.5%*.

That gate has a specific, silent failure. It treats a point estimate as
knowledge. An endpoint that has matched on 11 of 11 requests reports 100%, sails
past a 99.5% threshold, and gets promoted on the strength of eleven samples.

This is not a hypothetical corner. Traffic is heavily skewed, and the endpoints
with the least of it are the refund path, the B2B invoice run and the one admin
screen — the places where being wrong costs the most and gets noticed the
latest.

The mirror-image failure is on the way down. An endpoint with 2,500 clean
samples that starts failing *every request* needs roughly twelve thousand more
failures before its lifetime average crosses below 99.5%. A threshold on a
cumulative average is not a rollback trigger; it is a very slow alarm.

## Decision

Three changes to the naive gate.

**1. Promote on the Wilson score lower bound, not the observed rate.**

```
requests  matched  observed  wilson(z=2.576)
      11       11    100.0%           0.6237
     200      200    100.0%           0.9679
   2,000    2,000    100.0%           0.9967
```

Eleven perfect requests are consistent with a true match rate of 62%. The bound
says so, and the endpoint stays in shadow. It is not blocked because it failed;
it is blocked because nobody has looked at it.

A flat `MinSamples` floor is kept alongside the interval. The interval alone is
mathematically sufficient, but a handful of requests in a shadow run are often
*the same request* replayed by a health checker, and the floor is cheap.

**2. Each stage requires its own evidence.**

`stageTotal`/`stageMatched` reset on every transition, and promotion is judged
on those, not on the lifetime counters.

A spotless shadow record says the modern read path produces the same bytes when
its results are thrown away. It says nothing about how the service behaves once
it is on the hot path, where connection pool limits, cache warmth, downstream
timeouts and write amplification are all different. Carrying shadow evidence
into the cutover decision means cutting over on samples taken under conditions
that no longer apply.

**3. Rollback uses a recent window, and is evaluated before promotion.**

Three divergences in the last fifty comparisons halts the endpoint, regardless
of the lifetime average. Rollback is checked *first*, so an endpoint that
crosses the sample floor on the same request that completes a failure burst is
halted rather than promoted. A transition clears the window, so an endpoint
cannot be promoted on the strength of samples taken before an incident, and
cannot recover by ageing failures out.

`Halted` is sticky. It does not auto-recover.

## Consequences

- Migrations take longer, and the endpoints they take longest on are exactly the
  ones you would want to be slow about.
- `MinSamples: 200`, `PromoteAbove: 0.995`, `z = 2.576`, `RollbackWindow: 50`,
  `RollbackFailures: 3` are judgement calls, not derivations. They are on
  `Policy` so they can be argued with per-migration.
- Wilson assumes independent Bernoulli trials. Real request streams are
  correlated — a bad deploy makes *every* request fail at once. That correlation
  makes the bound conservative in the direction we want for promotion (a burst
  of failures crushes it) and is handled separately for rollback by the recent
  window.
- Three divergences in fifty is roughly a 6% failure rate. An endpoint that
  genuinely diverges on 1% of traffic will flap around this threshold. See
  `docs/known-limitations.md`.
