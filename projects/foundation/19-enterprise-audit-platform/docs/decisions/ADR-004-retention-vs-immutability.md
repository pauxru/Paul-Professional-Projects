# ADR-004: Retention vs immutability reconciliation via tombstones

*Status*: Accepted
*Date*: 2026-01-15

## Context

Regulators can require both:

1. **Retention** — delete personal or sensitive audit data after a defined period.
2. **Immutability** — never lose the ability to prove that a historical record hasn't been
   altered.

A naive delete breaks the chain: the next event's `PreviousChainHash` no longer resolves.

## Decision

Introduce **tombstoning**:

- Rows are never removed by the pruner. Instead, `PayloadJson` is rewritten to a deterministic
  stub (`{"tombstone":true,"contentHash":"<hex>"}`) and `IsTombstoned=true`.
- The chain link is defined as `SHA256(ContentHash || "|" || PreviousChainHash)`. Because the
  chain link is a function of the `ContentHash` **and not the raw payload bytes**, tombstoning
  a row does not change the recomputed chain hash. Verification passes without payload access.
- The `AppendOnlyInterceptor` permits exactly one mutation on an `AuditEvent`: the tombstone
  transition (`IsTombstoned` false→true + the deterministic new payload) with every other
  column unchanged. Anything else is rejected as an `AppendOnlyViolation`.

## Rationale

- Preserves the entire integrity chain across pruning.
- The stored `ContentHash` still lets an original-holder (say, the data subject who kept a
  copy) prove that the platform's version wasn't altered before it was pruned.
- Retention decisions are auditable: the pruner emits `audit.retention.pruned` events with the
  counts.

## Consequences

- Verification code has to handle tombstones specifically. Test `Retention_PrunesOldEvents_AndChainStillVerifies`
  proves the round-trip.
- Storage is not zero after pruning — the row is still there, just with the payload wiped. If
  the concern is disk usage rather than PII, this is an unhelpful trade; but the concern is
  usually PII, and this design meets that need.

## Alternatives considered

- **Hard delete + hash-of-hash chain repair** — considered; requires the pruner to write new
  events and re-key the chain, which is significantly more code and reasoning surface.
- **Move to a "shadow tombstone table" and null the row** — worse: the interceptor would have
  to permit `Deleted`, weakening the append-only guarantee.
