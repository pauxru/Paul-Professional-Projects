# ADR-001 — Double-entry ledger over a single balance column

- **Status**: Accepted
- **Date**: 2026
- **Context tags**: correctness, accounting, auditability

## Context

The system must track money across many accounts and must make it **impossible to create or destroy
money**. The naïve design stores a single `balance` column per account and mutates it on every
transaction. That design has no structural guarantee that value is conserved: a bug, a partial
failure, or a race can increment one balance without decrementing another, and nothing in the schema
notices. There is also no first-class record of *why* a balance is what it is.

## Options considered

1. **Single balance column per account**, updated in place per transaction.
2. **Event-sourced balances** — store an event stream and fold it to a balance, with no accounting
   structure.
3. **Classical double-entry bookkeeping** — every transaction is a balanced journal entry of 2..N
   postings (Σ debits = Σ credits per currency); balances are derived from postings.

## Decision

Adopt **double-entry bookkeeping** (option 3). Every movement of value is a `JournalEntry` containing
at least two `Posting` legs that must balance per currency, validated in the domain constructor
(`UnbalancedEntryException` / `MixedCurrencyException` otherwise). Balances are **derivable** from the
postings; a cached running total is kept for O(1) reads but is provably reconcilable against the
derived value.

## Consequences

**Positive**
- Value conservation is **structural**: an entry cannot exist unless its debits equal its credits, so
  the trial balance sums to zero by construction.
- Full auditability — every balance is explained by a sequence of postings with descriptions,
  references, correlation ids, and value dates.
- Corrections become **reversals** (new balanced entries) rather than mutations, which pairs naturally
  with the append-only and hash-chain decisions (ADR-004).
- Standard financial reporting (trial balance, statements) falls out of the model directly.

**Negative / costs**
- More rows and more code than a single column: entries, postings, and derivation logic.
- Cached balances must be maintained *and* reconciled (mitigated by the integrity self-check).

## Risks and mitigations

- **Risk**: cached balances drift from the postings. **Mitigation**: `IntegrityService` reconciles
  cached vs derived balances and a test asserts they match after concurrent load.
- **Risk**: developers post unbalanced entries. **Mitigation**: the invariant is enforced in the
  domain constructor, not in a service, so it cannot be bypassed.

## Alternatives not chosen

- *Single balance column* — rejected: no structural conservation, poor audit trail.
- *Pure event sourcing without accounting semantics* — rejected: reinvents double-entry with weaker
  guarantees and no standard reporting.
