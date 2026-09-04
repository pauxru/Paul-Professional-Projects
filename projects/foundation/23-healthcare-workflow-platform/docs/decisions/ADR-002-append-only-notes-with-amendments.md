# ADR-002 — Append-only clinical notes with amendments

**Status:** Accepted
**Date:** 2026-09-01

## Context
Clinical notes have specific legal characteristics: they must be attributable to their author,
must be tamper-evident, and their history must be reconstructible on demand. Overwriting a
note in place is not acceptable; deleting is not acceptable.

## Options
1. **Mutable notes with an audit trail alongside:** simple; but the audit trail and the row
   drift, and if the audit trail is compromised the "real" note history is unrecoverable.
2. **Blob-per-note history** (store JSON of every version in a single row): keeps versions
   together at the cost of queryability.
3. **New rows for every version, chained by a root id (this ADR):** the `ClinicalNote` table
   is append-only; an amendment is a new row with `Version = N + 1` and the same
   `RootNoteId`. Original notes are never modified.
4. **Content-addressable store** (e.g. IPFS-style): overkill.

## Decision
Model notes as new rows keyed by `Id` with `RootNoteId` linking them together. The initial
note has `RootNoteId = Id, Version = 1, IsAmendment = false`. An amendment is created via
`Encounter.AmendNote(...)` and has `RootNoteId = original.RootNoteId, Version = existing + 1,
IsAmendment = true, AmendmentReason = <required>`.

Enforce append-only at three layers:
1. **Domain:** `ClinicalNote` has no public setters and no `Update` method.
2. **Persistence:** `AppDbContext.SaveChanges` calls `RejectImmutableWrites()` which throws
   on `EntityState.Modified` (properties actually changed) or `EntityState.Deleted` for
   `ClinicalNote` and `AuditEvent`.
3. **API:** No endpoint exposes a `PUT /notes/{id}` or `DELETE /notes/{id}`. Amendment is
   an explicit `POST /encounters/{id}/notes/{noteId}/amend`.

Test coverage: `ClinicalNotesTests.Notes_Are_Append_Only_At_The_Persistence_Layer` +
`Amendment_Creates_New_Version_Not_Overwrite`.

## Consequences
- The notes table grows monotonically; a soft-delete concept simply does not exist.
- Version-aware queries are cheap: `WHERE RootNoteId = ? ORDER BY Version` returns the
  full history in insertion order.
- Storage cost is higher than mutable notes for records that are frequently amended, but
  is bounded by the number of amendments (in practice, one or two per note).

## Risks
- A future contributor may add a `PUT /notes/{id}` endpoint that bypasses domain guards.
  Persistence-layer enforcement catches this at runtime.
- EF Core's `ChangeTracker` can flag unrelated loads as `Modified` after a value-converter
  round trip. We mitigate by inspecting `IsModified && !Equals(OriginalValue, CurrentValue)`
  on each property and downgrading false positives to `Unchanged`.

## Alternatives considered
- Blob-per-note: rejected for queryability.
- Event sourcing entire encounter aggregate: rejected as project-scope creep.
