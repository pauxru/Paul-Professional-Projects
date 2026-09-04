# ADR 001: Corruption is loss of injectivity

**Status:** accepted
**Date:** during construction of the corpus

## Context

The project needs a definition of "silent data corruption" precise enough to label 29 rows with
ground truth. Without one, every measurement is circular: the verifier finds what the verifier
was built to find, and the report says so at length.

The obvious definitions all fail:

- *"The value changed."* Then every row is corrupted. `true` became `1` and `10.0000` became
  `10`. Useless.
- *"The value changed in a way that matters."* Matters to whom? This defers the question to a
  stakeholder who is not in the room.
- *"The verifier flagged it."* Circular.
- *"A round trip does not return the original."* Closer, but it makes the definition depend on
  which reader you use. A `getString` round trip and a `getObject` round trip disagree.

## Decision

**A migration corrupts a value if the mapping from source value to target value is not
injective on the domain of values the column can hold.**

If two distinct source values can produce the same target value, information is destroyed and no
process -- no lookup table, no reverse migration, no clever verifier -- can recover which one
was there. If the mapping is injective, the original is recoverable in principle, and what
changed is representation.

Worked through the corpus:

| change | injective? | verdict |
|---|---|---|
| `10.0000` (BigDecimal) -> `10` (Integer) | yes, scale is recoverable from the column type | representation |
| `true` -> `1` | yes | representation |
| `2024-01-15 09:00` -> `1705309200000` | yes *given a zone*; the zone is not stored | see ADR 004 |
| `'OK        '` -> `'OK'` | yes, the width is in the schema | representation |
| `'0000007'` -> `7` | **no** -- `'7'`, `'07'`, `'0000007'` all become `7` | **corruption** |
| `99999999999999.1234` -> `1.0E14` | **no** -- a range of decimals collapses to one double | **corruption** |
| `'Äpfel'` and `'äpfel'` distinct -> both compare equal | **no** | **corruption of the constraint** |

The last row is the one that justifies the whole decision. No individual value changed. Every
byte is identical. What was destroyed is a *relation* -- the source's `UNIQUE(name)` guaranteed
26 distinct names under its collation, and the target's collation folds a smaller alphabet, so
the same 27 rows now satisfy a weaker constraint. Injectivity catches this and nothing
value-shaped does.

## Consequences

**The good.** The same criterion applies to the verifier's own canonicalisation rules, and this
turned out to be the most valuable property of the definition. A rule is a function applied to
both sides before comparison. If it is injective it cannot make two different values look equal,
so it can suppress noise but cannot suppress a defect. If it is not injective it can do both,
and the verifier's silence stops being evidence. That symmetry is not something I designed; it
fell out and it is the core result of the project (section 11 of `docs/results.md`).

**The cost.** `Rule.IDENTIFIER` must not parse. The natural implementation of an
"identifier-equality" rule is to parse both sides as numbers, which reconciles `'0000007'` with
`7` and makes the account column quiet. It is also exactly the operation the definition calls
corruption. So `IDENTIFIER` uses `String.valueOf` -- it reconciles `Integer 7` with `String "7"`
and refuses to reconcile either with `"0000007"`. This is the sharpest practical consequence of
the ADR and it looked like a bug the first three times I read it.

**The residue.** "The domain of values the column can hold" is doing real work in the
definition. `DECIMAL_TO_BINARY_FLOAT` is non-injective over the reals but injective over the
values actually present in a small corpus -- which is why `0.10` survives and
`99999999999999.1234` does not, and why `Row.corrupting` is a per-row fact rather than a
per-hazard one. That per-row distinction is one of the more useful things the corpus taught me.
