# ADR 0005 — Money is an integer, and half-open requires consecutive probes

**Status:** accepted
**Date:** 2024-06

## Context

Sections 1–5 are a measurement exercise. Section 6 is the part that decides
whether any of it survives contact with production.

A cost-optimising router does something dangerous by design: it concentrates
traffic on whichever model answers well for the money. Then that provider
degrades, and every request retries into a timeout. **The routing policy is
correct throughout and the service is down.** The router's optimality is what
caused the outage.

`internal/budget` is the answer to that: per-tenant spend caps, failover to a
different model, graceful degradation to a cheaper one, and a circuit breaker.
Two decisions in it are worth recording because both are places where the obvious
implementation is wrong.

## Decision 1 — money is stored in integer tenths of a cent

Spend caps are enforced by accumulating a per-tenant total across thousands of
requests. In `float64`, that accumulation drifts: adding 0.1 a million times does
not give 100000. A cap enforced against a drifting total is a cap that is
sometimes wrong, and it is wrong in an amount that grows with traffic, which is
the worst possible failure shape — it works in test and fails in production.

All money in `internal/budget` is `int64` tenths of a cent. Conversion from the
simulator's float costs happens once, at the boundary, by rounding.

`TestSpendDoesNotDrift` performs 1,000,000 reservations of 1 unit and asserts the
total is exactly 1,000,000. It is a fast test and it is the only kind of evidence
that means anything for this class of bug.

The report shows the consequence: tenant `acme` spends **exactly** 120.0¢ against
a 120.0¢ cap. Not 119.97, not 120.03.

## Decision 2 — half-open requires N *consecutive* successful probes

The textbook circuit breaker has three states. Closed passes traffic. After
`failureThreshold` consecutive failures it opens and rejects immediately. After a
cooldown it goes half-open and admits a probe; if the probe succeeds it closes.

**That last clause is the bug.** A degraded provider does not fail every request.
It fails most of them. A single probe against a provider with a 30% success rate
closes the breaker roughly one time in three, and full traffic is restored to a
provider that is still broken — which immediately re-trips it. The system
oscillates, and each oscillation costs a burst of real user-facing failures.

So half-open requires **three consecutive** successes to close, and **any**
failure in half-open reopens the breaker and restarts the cooldown from zero.

The measured transition sequence in the report is the point of the whole design:

```
00:01:22   closed      -> open        5 consecutive failures
00:01:52   open        -> half-open   cooldown elapsed
00:01:52   half-open   -> open        probe failed
00:02:22   open        -> half-open   cooldown elapsed
00:02:22   half-open   -> open        probe failed
00:02:52   open        -> half-open   cooldown elapsed
00:02:52   half-open   -> open        probe failed
00:03:22   open        -> half-open   cooldown elapsed
00:03:22   half-open   -> open        probe failed
00:03:52   open        -> half-open   cooldown elapsed
00:03:53   half-open   -> closed      3 probes succeeded
```

Four false dawns before it closes. A single-probe breaker would have restored
full traffic at 00:01:52, on the first one.

**The tests assert the transition sequence, not the final state.** Most breaker
bugs are in the path — a breaker that eventually reaches the right state after
flapping through it six times is broken in exactly the way that matters, and a
final-state assertion cannot see it.

## Decision 3 — every excluded request is counted, and the total is checked

```
4000 requests: 2842 served, 745 failed over, 1109 degraded, 49 rejected
served + degraded + denied == 4000    ->  asserted, true
```

The identity is *asserted at runtime*, not eyeballed. An experiment that silently
drops requests reports an accuracy computed over an unknown denominator, which is
how a policy that rejects the hard queries comes to look excellent.

Two things fall out of the numbers that are easy to miss:

- **acme burns its cap and is then served on the small model rather than being
  rejected.** Only 40 of its requests are refused outright; 1,109 are silently
  answered by a worse model. **That degraded count is the number to alert on.**
  Nothing in the error rate will show it — the customer is getting a worse
  product and every request returns 200.
- Failover (745) and degradation (1,109) are separate counters for separate
  causes: failover is *the provider is unwell*, degradation is *you are out of
  money*. Collapsing them into one "fallback" metric loses the only information
  that distinguishes an incident from an invoice.

## Consequences

- The breaker's cooldown and probe count are per-provider configuration, not
  global. A provider with a 60-second recovery profile and one with a 5-minute
  profile need different numbers, and there is no defensible default.
- Requiring three consecutive probes lengthens recovery time after a genuine
  transient blip. That is the trade being made deliberately: slower recovery from
  brief failures in exchange for not amplifying long ones.
- Integer money means the simulator's float costs are rounded at the boundary, so
  the total spend in section 6 differs from sections 2–5 by rounding. That is
  correct behaviour and worth knowing when comparing the two.
