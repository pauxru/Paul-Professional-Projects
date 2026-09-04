# ADR 0002 — Six operations instead of one "ignore"

**Status:** accepted

## Context

Every response-diffing tool offers some form of "ignore this path". That single
operation is the reason shadow-traffic programmes stop catching bugs.

`ignore` is a blunt instrument used for a graded problem. The fields people
ignore are volatile in *specific, bounded* ways:

- `requestId` changes every request, but it is always a UUID.
- `generatedAt` changes every request, but it is always an RFC3339 timestamp.
- `durationMs` changes every request, and its value genuinely carries no
  information — but it must still be present, and still a number.
- `lines[]` comes back in a different order, but with the same contents.
- `subtotal` differs in the twelfth decimal place because the addition happened
  in a different order — but not in the second decimal place.

Collapsing all five of those into `ignore` throws away four constraints that
were free to keep.

## Decision

Six operations, ordered from most to least constraining:

| op | keeps checking |
|---|---|
| `Unordered` | contents, membership, cardinality — just not position |
| `RelTolerance` | the value, to a relative epsilon |
| `Tolerance` | the value, to an absolute epsilon |
| `Format` | presence, type, and a named shape (uuid, rfc3339, digits, …) |
| `IgnoreValue` | presence and type |
| `Ignore` | nothing |

The rule of use: **pick the strongest operation that absorbs the noise.**

## Consequences

Measured over 4,000 pairs with 812 known defects (`docs/results.md`):

- `Format` instead of `IgnoreValue` on two fields recovers **162 defects**, 20
  percentage points of recall, at **zero** additional false positives.
- `IgnoreValue` instead of `Ignore` on the `meta` subtree recovers **81** more.
- `RelTolerance` instead of `Tolerance` recovers **69** more, because an
  absolute 0.01 tolerance wide enough to absorb float noise on a 581.17 total is
  by construction wide enough to absorb a penny.

The false-positive column is **0 for every one of these rulesets**. The weaker
operation is never buying quiet. It is only ever buying keystrokes.

Two consequences that took work:

- `IgnoreValue` must still fail on a *missing* field and on a *type change*.
  "Ignore the value" is not "ignore the field", and the distinction catches the
  `customer.id` integer that became a string.
- A `Format` rule pointed at a non-string is reported as a difference rather
  than silently skipped. A format rule that quietly does nothing is worse than
  no rule at all, because it looks like coverage.
