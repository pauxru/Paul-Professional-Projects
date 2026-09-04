# ADR-003: Keyset (cursor) pagination

*Status*: Accepted
*Date*: 2026-01-15

## Context

Audit queries typically span tens of thousands to millions of events. The API must page
through them without:

- **Skipping or duplicating rows** when concurrent inserts land between page fetches (a fatal
  flaw of `OFFSET`-based pagination in a monotonic-write system).
- Requiring the DB to scan-and-discard `OFFSET` rows on every page fetch (a fatal flaw for
  performance once the offset grows).

## Decision

Use **keyset (cursor) pagination** keyed on `AuditEvent.SequenceNumber` — a per-tenant
monotonically increasing integer.

- The cursor is opaque to the client: base64 of the last-seen sequence.
- Each page filters `WHERE SequenceNumber > @cursor ORDER BY SequenceNumber ASC LIMIT @pageSize`.
- The response includes `NextCursor` (or null if the last page fits).

## Rationale

- Stable under concurrent inserts: a new event lands at a sequence greater than any previously
  seen, so the client never sees a duplicate.
- Cheap on the DB: the composite index `(TenantId, SequenceNumber)` makes the paging query a
  simple index seek.
- Total-count-independent: the client doesn't need to know how many rows exist to page.

## Consequences

- The client cannot jump to page N. If a UI needs page numbers, it must render the sequence
  ranges instead (which is arguably a better UI anyway for an event log).
- Filter-and-page mixing (say, filter by actor + page): the same cursor rule applies, and the
  `(TenantId, ActorId, EventTime)` index carries it.

## Alternatives considered

- **OFFSET/LIMIT** — rejected: unstable under insert, expensive at scale.
- **Time-based cursor (`EventTime`)** — rejected: EventTime is caller-supplied and can be
  identical for two rows (clock skew).
