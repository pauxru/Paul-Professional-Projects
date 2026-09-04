# ADR 002: The source and target schemas disagree on purpose

**Status:** accepted
**Date:** after the first corpus produced nothing interesting

## Context

First version of the schema had `account INTEGER` on both sides. It seemed obviously right: the
same logical column should have the same type, and the migration should be a faithful copy.

The result was a project with nothing to say. `'0000007'` never reached the migration, because
H2 coerced it to `7` at insert time. The most important hazard in the taxonomy --
`AFFINITY_COERCION` -- was unobservable, because the damage happened before the migration
started. No verifier could see it. There was nothing to verify.

That is a real failure mode, incidentally, and worth naming: **if your test corpus is built
against the target schema, the target schema will silently sanitise your test data and every
hazard will look harmless.**

## Decision

The source and target schemas differ, deliberately, in ways that are each independently
defensible:

| column | source (H2) | target (SQLite) | why they differ |
|---|---|---|---|
| `account` | `VARCHAR(24)` | `INTEGER` | the legacy system stored account numbers as text; the modernised schema tightened it to a number |
| `amount` | `DECIMAL(18,4)` | `REAL` | the target has no exact decimal type |
| `code` | `CHAR(10)` | `TEXT` | the target has no fixed-width type |
| `seen` | `TIMESTAMP` | `INTEGER` | the target has no date type |
| `name` | `VARCHAR` + `IGNORECASE` | `TEXT COLLATE NOCASE` | both are "case-insensitive"; they disagree about which alphabet |

The source is created with `SET IGNORECASE TRUE`, because a legacy system with case-insensitive
names is the situation that makes the collation hazard real.

**The point is that every one of these differences is a decision someone would defend in a design
review, and the migration hazard is created by the pair, not by either one.** Nobody made a
mistake. `VARCHAR(24) -> INTEGER` is a schema improvement -- account numbers *are* numbers. It
is also the change that destroys the leading zeros on `'0000007'`.

## Consequences

- The corpus can hold values the target cannot represent, which is the only way to observe
  coercion.
- `Migration` must carry a per-column type map rather than assuming symmetry, and the insert path
  must use `setObject` rather than a typed setter, or the migration would coerce values on the
  way in and hide the very thing being measured.
- The `identifier` rule becomes load-bearing: without it the verifier objects to 93% of rows
  (every account differs, `String` vs `Integer`) and the gate rejects it as unusable. With it,
  precision is perfect. One rule, on one column, is the difference between a working verifier and
  an abandoned one -- and that rule is only safe because it refuses to parse (ADR 001).
- The `name` column ends up carrying a hazard that is invisible to any row-by-row comparison,
  because `IGNORECASE` folds `Äpfel`/`äpfel` while `NOCASE` folds only A-Z. This was measured,
  not assumed, and it produced the set-level check in section 5.

## Alternative rejected

Making the target schema faithful (`account TEXT`, `amount TEXT`) removes every hazard and every
reason for the project to exist. It is also what a cautious team actually does -- migrate
everything as text and tighten later -- and it is worth saying that this is a genuinely good
strategy. The project is about the case where somebody tightened.
