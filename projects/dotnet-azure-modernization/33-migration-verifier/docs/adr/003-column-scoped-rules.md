# ADR 003: Canonicalisation rules are scoped to columns, not to values

**Status:** accepted
**Date:** after a bug that produced no error and no failing test

## Context

The first implementation of `Rule` dispatched on the runtime type of the value:

```java
// NUMERIC, first version
if (v instanceof Number n) return new BigDecimal(n.toString()).stripTrailingZeros();
```

This is the obvious design. It is also wrong, and the way it is wrong is the reason this ADR
exists.

The target stores timestamps as `INTEGER`, so JDBC returns them as `Long`. A `Long` is a
`Number`. So `NUMERIC` consumed the epoch milliseconds and turned them into a `BigDecimal`
before `TEMPORAL` ever saw them. `TEMPORAL` then received a `BigDecimal`, did not recognise it,
and passed it through untouched.

The failure mode: **whenever `NUMERIC` was enabled, `TEMPORAL` stopped working.** Nothing threw.
No test failed. The verifier simply reported more differences than it should have, and every one
of those differences looked exactly like a real finding. The rule-subset lattice in section 3 was
being computed against a `TEMPORAL` that only functioned in the subsets where `NUMERIC` was
absent -- which is to say, the central measurement of the project was quietly wrong.

I found it by asking why removing a rule ever *increased* the number of differences. It should
be monotone. It was not.

## Decision

Rules declare the columns they govern:

```java
Rule NUMERIC    = new Rule("numeric",    ..., "amount");
Rule TEMPORAL   = new Rule("temporal",   ..., "seen");
Rule IDENTIFIER = new Rule("identifier", ..., "account");
```

`Rule.Set.canonicalise(String column, Object value)` applies only the rules that `governs(column)`.
A rule that does not govern a column is not consulted about it, whatever the value's runtime type.

## Consequences

- Rule interaction is eliminated by construction rather than by ordering. There is no rule order
  to get right, and the 128-subset lattice is a lattice of genuinely independent choices.
- Rules become explainable in one line each -- "`numeric` reconciles two spellings of the amount"
  -- which is what makes the injectivity table in section 11 readable.
- It requires the comparison to know the column name, so `Comparison` compares maps rather than
  tuples. Slightly more machinery, and worth it.
- **The blast radius of a bad heuristic is one column.** `TEMPORAL`'s "any `Long` over 1e12 is
  epoch milliseconds" rule is still a heuristic and still uncomfortable (see
  `known-limitations.md` §5), but it can only misfire on `seen`. Under value-scoped dispatch it
  could have misfired on any large integer anywhere in the schema.

## The general lesson

The bug had the exact shape the project is about. A component that silences noise, silently
consuming the input another component needed, producing output that is wrong in a way that looks
like a finding rather than like an error. A verifier whose own rules interfere with each other
has the same epistemic problem as a migration whose engines interfere with each other, and it is
harder to notice, because there is no second copy of the verifier to compare against.

This is also why `Rule.isInjective()` exists as a method on the rule rather than as a comment.
Properties that matter should be interrogable by tests.
