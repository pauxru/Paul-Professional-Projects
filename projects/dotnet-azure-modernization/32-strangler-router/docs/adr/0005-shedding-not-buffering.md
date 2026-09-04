# ADR 0005 — The shadow path is allowed to lose work; it is not allowed to lie

**Status:** accepted

## Context

A shadow proxy sits on the live request path. Everything it does is charged to a
real user's latency budget, and any resource it consumes is resource the primary
path does not have.

The failure mode is well known and still common: the proxy buffers every
response so it can compare them, the comparison queue backs up behind a slow
modern implementation, and the proxy becomes the outage. The migration tool
takes down the system it was supposed to de-risk.

The tempting fix — "make the buffer big enough" — is the bug.

## Decision

The shadow path sheds work under pressure, in four specific ways, and **counts
every instance**.

| refusal | why | counter |
|---|---|---|
| unsafe methods are not mirrored | replaying a POST runs the new order-placement code for real | `SkippedUnsafe` |
| responses over `MaxBodyBytes` are not buffered | unbounded buffering of attacker-influenced sizes | `SkippedTooLarge` |
| comparisons are dropped when the queue is full | bounded memory under load, never blocks the client | `DroppedQueue` |
| a panic in the modern handler is caught | a new implementation is by definition not trusted | `ShadowErrors` |

Mirroring unsafe methods is available, but **opt-in per route**
(`MirrorUnsafe func(*http.Request) bool`). The escape hatch exists; taking it is
a deliberate act with the route written down next to it.

The client is served from the legacy response and the comparison happens on a
worker afterwards, so the client never waits for the shadow request. At cutover
the modern implementation serves directly.

## Consequences

The important half of this decision is the counters.

A shadow report that says "4,000 requests compared, 100% match" while silently
having skipped every response over 1 MB, every POST, and 12% of traffic to a
full queue is **worse than no report**, because it is used to justify a cutover.

Making the exclusions countable turns *"we compared all the traffic"* from a
hope into a checkable claim. `TestStatsAccountForEveryRequest` asserts the
identity directly: `mirrored + skippedUnsafe + skippedTooLarge == requests`.

Other consequences:

- `Close()` drains work that was already accepted rather than discarding it,
  so the last comparisons before a shutdown are not silently lost.
  `TestCloseDrainsAcceptedWork` asserts `compared + dropped == requests`.
- Shadow requests get their own timeout, so one hung modern handler cannot pin a
  worker forever or block `Close()`.
- Dropping comparisons biases the sample: under load you compare a
  non-random subset of traffic, and load is exactly when behaviour diverges.
  The drop counter makes the bias visible; it does not remove it. See
  `docs/known-limitations.md`.
