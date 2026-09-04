# SLO Mathematics — Northstar Reliability Control Room

This document is the contract behind the dashboard. Percentages are stored as fractions: 99.9% is `0.999`, not `99.9`.

## 1. Evaluation scope

For every SLI, metrics are first restricted to:

1. the SLI’s service;
2. timestamps in the half-open interval `[start, end)`; and
3. optional endpoint, region, and tier filters.

The half-open interval matters: a sample at `start` is included and a sample exactly at `end` belongs to the next evaluation. This prevents a minute from being counted twice in adjacent evaluations.

## 2. Request-based SLI

For a request-based SLI:

```text
SLI = good events / valid events
bad events = valid events - good events
```

Definitions by measurement:

| SLI | Good events | Valid events |
|---|---:|---:|
| Availability | `requests - errors` | `requests` |
| Latency threshold T | histogram requests in buckets at or below T | `requests` |
| Quality | `qualityGoodEvents` | `qualityValidEvents` |
| Freshness | `freshnessGoodEvents` | `freshnessValidEvents` |

If no valid events exist, the implementation reports attainment `1.0` and a zero absolute budget. It does not manufacture a denominator.

### Worked request example

Two request aggregates contain 100 requests / 3 errors and 200 requests / 2 errors:

```text
good  = (100 - 3) + (200 - 2) = 295
valid = 100 + 200 = 300
SLI   = 295 / 300 = 0.983333...
```

The calculation is traffic weighted. Averaging `97%` and `99%` would be wrong if their traffic volumes differed.

## 3. Window-based SLI

Window-based SLIs answer a different question: “how many valid minutes were wholly good?”

```text
window SLI = good minutes / valid minutes
```

Samples are grouped by UTC minute after filters are applied. A minute is valid when it has a relevant request/probe denominator. It is good only when every relevant event in that minute is good. For availability with probe data, probe good/total is used; otherwise request errors determine the minute.

Example: probe outcomes are good, bad, good over three minutes:

```text
good minutes  = 2
valid minutes = 3
SLI           = 2 / 3 = 0.666667
```

This intentionally differs from request-based availability: one bad request can make a minute bad even if that minute carried thousands of good requests.

## 4. SLO windows

### Rolling window

For a 30-day rolling SLO evaluated at `t`:

```text
start = t - 30 days
end   = t
```

Every evaluation moves both bounds. At `2026-02-01T00:00Z`, a 28-day rolling window starts at `2026-01-04T00:00Z`; month boundaries do not reset it.

### Calendar window

For a monthly SLO:

```text
start = first instant of current UTC month
end   = evaluation time
period end = first instant of next UTC month
```

At `2026-02-01T00:05Z`, the monthly window starts `2026-02-01T00:00Z`, not in January. Quarterly windows use the first day of January, April, July, or October and end three months later.

Rolling windows make recent behavior continuously relevant. Calendar windows make a fixed reporting period and a reset date explicit. Neither is “more correct” without a product decision.

## 5. Error budget

Let `T` be SLO target and `V` valid events/minutes:

```text
allowed error rate = 1 - T
budget total       = V × (1 - T)
budget consumed    = bad events/minutes
budget remaining   = max(0, budget total - budget consumed)
consumed %         = budget consumed / budget total × 100
remaining %        = budget remaining / budget total × 100
```

For a 99.9% (`T = 0.999`) SLO with 10,000 valid events:

```text
allowed rate = 1 - 0.999 = 0.001
total budget = 10,000 × 0.001 = 10 events

10 errors: consumed = 10, remaining = 0, consumed = 100%
```

The absolute budget can be fractional for a small observation set. The policy evaluates percentage headroom; the UI retains fractional precision rather than rounding a budget up and silently granting errors.

## 6. Burn rate

Burn rate normalizes observed error rate by the SLO’s allowed error rate:

```text
observed error rate = bad / valid
burn rate           = (bad / valid) / (1 - T)
```

For a 99.9% SLO, `1 - T = 0.001`.

| Window fixture | Bad / valid | Error rate | Burn |
|---|---:|---:|---:|
| 1 hour | 144 / 10,000 | 0.0144 | 14.4× |
| 5 minutes | 6 / 1,000 | 0.006 | 6× |
| 6 hours / 30 minutes / 1 day | 30 / 10,000 | 0.003 | 3× |
| 3 days | 100 / 100,000 | 0.001 | 1× |

The test suite asserts these hand-computed values directly. A raw error percentage is not a burn rate: 0.3% is benign for a 99% target but burns a 99.9% SLO at 3×.

## 7. Multi-window, multi-burn-rate alerting

Each alert rule requires **both** windows:

| Rule | Long confirmation | Short confirmation | Intent for a 30d SLO |
|---|---|---|---:|
| Fast page | 14.4× for 1h | 6× for 5m | 2% of budget in 1h |
| Slow page | 6× for 6h | 3× for 30m | 5% of budget in 6h |
| Sustained ticket | 1× for 3d | 3× for 1d | 10% of budget in 3d |

The intended budget spend represented by a long-window threshold is:

```text
budget spend % = long window duration / compliance period duration
                  × long burn threshold × 100
```

For fast page on a 30-day period (720 hours):

```text
1 hour / 720 hours × 14.4 × 100 = 2%
```

For slow page:

```text
6 hours / 720 hours × 6 × 100 = 5%
```

The short window is a reset-speed and present-tense confirmation signal; it is not a second budget-spend calculation. Requiring `long >= threshold AND short >= threshold` limits stale long-window pages after recovery and short transient noise.

### Fast-page worked stream

To produce independently correct overlapping readings:

```text
Older part of 1h: 9,000 valid, 138 bad
Newest 5m:        1,000 valid,   6 bad
1h total:        10,000 valid, 144 bad

1h burn = (144 / 10,000) / 0.001 = 14.4×
5m burn = (  6 /  1,000) / 0.001 =  6.0×
```

Both predicates are true, so fast page fires. If the newest 5m has 5 errors, short burn is 5× and the rule does **not** fire even if the long window remains above 14.4×.

## 8. Detection, recovery, and suppression

An alert records:

```text
detection lag = max(0, evaluation time - newest source metric timestamp)
```

When a rule first meets both predicates, `DetectedAt` and `FiredAt` are recorded. When either window returns below its threshold, an active/acknowledged alert transitions to `Resolved` and records `RecoveredAt`. A declared maintenance window or any open incident affecting that service changes the evaluation result to `Suppressed`, with the reason preserved. Suppression prevents duplicate pages; it does not erase the raw burn calculation.

## 9. Projected exhaustion

This is a directional projection, not a promise. Given current remaining percent `R`, current burn `B`, and compliance period duration `P`:

```text
time to exhaustion = (R / 100) / B × P
```

For 50% remaining, 2× burn, and a 30-day period:

```text
(0.50 / 2) × 30 days = 7.5 days = 180 hours
```

At zero burn there is no finite projected exhaustion. For a calendar SLO, the projection is capped at the calendar period end; the budget resets then. For a rolling SLO, the value describes the current trend while the rolling denominator continues to move.

## 10. Incident attribution and timing

When an incident resolves, each affected service’s SLO is evaluated over `[incident start, resolution)`. The resulting bad events/minutes and consumed absolute budget are attached to the incident. This is attribution, not causal proof: concurrent unrelated errors can share an incident window and must be discussed in the postmortem.

```text
MTTD = first detection (or declaration fallback) - incident started
MTTA = first acknowledgement - first detection
MTTR = resolution - first detection
```

The implementation keeps mitigation distinct from resolution. A rollback can mitigate impact while monitoring continues; only an explicit resolution ends the incident.
