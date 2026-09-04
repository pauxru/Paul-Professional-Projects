# ADR 0005 — Coalescing is a cache with no metrics, and its single-flight is group-owned

**Status:** accepted

## Context

Request coalescing merges concurrent duplicate requests into one backend call.
The first caller becomes the leader; the rest follow and receive its answer.

For **exact** duplicates this is unambiguously correct — same bytes, same
answer, fewer calls. The temptation is to extend it to *semantically similar*
requests: if the cache would have served A's answer for B, why not let B join
A's in-flight call?

## The finding

Because coalescing has no error metric, and cannot have one.

Sweeping only the coalescing threshold, with the cache fixed at 0.60:

| coalescing threshold | coalescer precision | **cache-reported precision** |
|----------------------|---------------------|------------------------------|
| off | — | 95.5% |
| 0.90 | 100.0% | 95.5% |
| 0.75 | 99.1% | 95.5% |
| 0.60 | 97.2% | 95.5% |
| 0.45 | 94.9% | 95.5% |

The coalescer's precision falls by five points. **The cache's reported precision
does not move at all.**

The reason is structural, not a missing counter. Every coalesced request was a
cache **miss** — the cache was asked, correctly said "I don't have that", and
was bypassed. Its counters are working perfectly. They are counting a different
population. A team watching cache precision would see a flat line while their
coalescer served an increasing number of wrong answers.

This is the same shape as ADR 0003: a metric that appears to describe the system
actually describes a subset of traffic, and the subset it excludes is where the
errors are.

## Decision

1. **Coalescing defaults to exact-key only.** Semantic coalescing exists in the
   codebase, is measured, and is off. The gain over exact coalescing is small
   because true concurrent duplicates are usually verbatim; the risk is a class
   of error nothing downstream can see.

2. **`Stats` separates `CacheFalse` from `CoalescedWrong`**, and exposes both
   `Precision()` (true) and `CacheReportedPrecision()` (what the cache can see).
   The gap between them is the blind spot, and printing both is the finding.
   `TestCacheReportedPrecisionExcludesCoalescingErrors` asserts the gap opens
   under a reckless threshold — the report's claim, pinned as a test.

3. **`CacheCoalescedResults` defaults to off.** Writing a coalesced answer into
   the cache converts a transient mistake into a permanent entry: the wrong
   answer is then served to everyone who asks a similar question, forever.
   `Stats.Poisoned` counts these. In the measured run, three transient
   coalescing errors became three permanent entries.

## The simulation had to be fixed before it could measure this

The first version of the gateway filled the cache when a request **arrived**
rather than when its backend call **completed**. Coalesced counts were 0 for
every mode, and the number looked plausible enough to nearly survive.

It defined away the entire phenomenon. Coalescing exists to cover the window
between a request starting and its answer existing; a cache filled on arrival
has no such window, because the second request finds the answer already there.
The fix is a `pendingPut` queue drained by `retire(now)` in arrival-time order.
`TestCacheIsFilledOnCompletionNotOnArrival` pins it by asserting that a
duplicate arriving at `latency/2` is *not* a cache hit.

This was the most important bug in the project, and it was found by a prediction
that said "coalescing should absorb a large share of burst traffic" followed by
a number that said zero.

## The concurrent implementation

`internal/flight` is a real streaming single-flight group — the only genuinely
concurrent code here. Two decisions:

**The group owns the subscriber list, not the leader.** An earlier design had
the leader hold its followers and fan out directly. It has a race that no amount
of careful leader code fixes: a follower can arrive after the leader has decided
its subscriber set but before it publishes completion, and is lost forever. The
group holds the lock, so a joiner is either registered before completion or sees
the finished result. There is no interval in which it can be neither.

**A late joiner receives the prefix already emitted, replayed under the lock,
then the live tail.** This is what makes it a *streaming* single-flight rather
than a request deduplicator: a subscriber joining mid-stream sees the identical
byte sequence as one that joined at the start. The subscriber channel is
buffered to `len(prefix)+64` specifically so the replay cannot block while the
group lock is held — a deadlock that the first version had.

`Do()` is a retry loop rather than a recursive call. The recursive version could
recurse indefinitely when a flight completed between the lookup and the join.

## Consequences

- Semantic coalescing remains available and measured. Turning it on is a
  deliberate choice with a documented error class, not a default.
- `flight.Group` is not exercised by the report's simulation — the gateway is a
  discrete-event model and single-threaded. Section 5 runs a live demo (33
  concurrent requests, 1 backend call, 1 leader, 32 followers, 1 late joiner,
  1 distinct body) so the concurrent implementation is not merely asserted.
- **`-race` is unavailable on this machine** (no C toolchain). `test.ps1`
  compensates with a `-count=50` stress run at `GOMAXPROCS=8`, which is not the
  same thing. See known-limitations.
