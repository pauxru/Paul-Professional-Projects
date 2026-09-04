# ADR 004: `temporal` is not injective, and it is enabled anyway

**Status:** accepted, uncomfortably
**Date:** after prediction P8 held

## Context

ADR 001 says a rule is safe if it is injective. `Rule.TEMPORAL` converts the target's epoch
milliseconds back to a wall-clock timestamp for comparison against the source. As a function on
`long` values it is a bijection -- so why is `isInjective()` false for it?

Because what the rule inverts is not a function.

The source column is `TIMESTAMP` -- a wall clock with no zone. The target column is `INTEGER` --
an instant. Converting between them requires a zone, and the zone is nowhere in the data. The
migration supplies it from the JVM's default. So the mapping from *the thing the source column
means* to *the thing the target column means* is one-to-many in one direction and
many-to-one in the other, and no rule operating on the stored values can repair that.

Row 23 of the corpus is `2024-03-31 02:30:00`. That wall clock does not exist in Europe/London
-- the clocks jump from 01:00 to 02:00 that morning. It is a perfectly ordinary value for the
source column to hold. Reading the target's stored millisecond value back through four different
`ZoneId`s yields four different wall clocks, and the source cannot adjudicate between them
because it never recorded which one it meant.

Prediction P8 was that the verifier would report zero differences on this row. It held. The
verifier is correct: under the zone it used for the migration, the round trip is exact. It
certifies a column whose meaning depends on the machine that ran the migration.

## Decision

`Rule.TEMPORAL.isInjective()` returns `false`, and the rule is enabled in the recommended
configuration regardless.

The comment at the declaration says why, because a future reader will otherwise assume it is a
mistake.

## Consequences

**Why enable a non-injective rule.** Without it the `seen` column differs on every row --
`Timestamp` versus `Long` -- and the verifier objects to 93% of the corpus. The gate then rejects
it as `NO_GO_UNUSABLE`, correctly. A verifier nobody reads has a detection rate of zero. So the
choice is between a rule that could hide a defect and a verifier that hides all of them.

**What it costs.** In the blindfold matrix, `temporal` never actually blinded the verifier
against any of the five planted controls. That is not evidence it is safe (see
`known-limitations.md` §2); it means no control damages this column in a way `temporal` absorbs.
A sixth defective migrator that shifted every timestamp by exactly one hour would be invisible,
and I know that by inspection rather than by measurement.

**What the honest position is.** The rule is a documented exception to the criterion, not a
counterexample to it. The criterion says "injective implies safe". `temporal` is not injective,
so the criterion declines to certify it, and the decision to ship it is a human one taken with
the cost written down. That is the correct relationship between a formal criterion and an
engineering decision -- the criterion narrows the set of things you have to argue about from
seven rules to three.

**The residual hazard is upstream of the verifier anyway.** The real fix is not a verifier rule.
It is storing the zone, or storing an instant on both sides, or pinning the migration's zone
explicitly and recording it. Section 8 of the report says so, and this is the one finding in the
project whose remediation is a schema change rather than a verifier change.

## Related

- ADR 001 for the criterion.
- `known-limitations.md` §3 for why the stronger cross-machine claim is argued rather than
  demonstrated -- `TimeZone.setDefault()` does not reach a loaded JDBC driver, which is
  prediction P7, contradicted.
- `pom.xml` pins the test JVM to UTC as a direct consequence.
