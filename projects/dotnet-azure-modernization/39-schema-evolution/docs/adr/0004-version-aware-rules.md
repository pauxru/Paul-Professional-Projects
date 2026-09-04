# ADR 0004: Version-aware rules, with no default version

## Status

Accepted.

## Context

`ALTER TABLE t ADD COLUMN status text NOT NULL DEFAULT 'new'` is one of the
most common statements in any migration. On PostgreSQL 10 it rewrites every row
in the table under ACCESS EXCLUSIVE. On PostgreSQL 11 and later it is
catalogue-only and completes in milliseconds, because 11 introduced the
missing-value optimisation: the default is recorded in `pg_attribute` and
existing rows read it back without being written.

Same bytes. Same table. On one server it is a routine change and on the
previous major version it is an outage.

This is not an isolated case. The rules that changed:

| Version | What became safe |
| --- | --- |
| 11 | `ADD COLUMN ... DEFAULT <non-volatile>` no longer rewrites |
| 12 | `SET NOT NULL` can use a validated `CHECK` as proof and skip its scan |
| 12 | Some type widenings (`varchar(n)` → `text`, `int` → `bigint` where representation permits) no longer rewrite |
| 14 | `REINDEX CONCURRENTLY` and improved `DROP INDEX CONCURRENTLY` behaviour |

A linter that does not know the target version is not being conservative; it is
being wrong in one direction or the other. Assume 16 and it green-lights a
statement that takes the site down on the 11 box in the corner. Assume 10 and
it refuses half the safe migrations on a modern fleet, which returns us to
ADR 0003's exemption-flag failure mode.

## Decision

Every rule takes the PostgreSQL major version as a parameter, and there is no
default. `lint.Lint(stmts, version)` will not compile without one.

The version threads through `Rewrites`, `Scans`, `LockOf` and `plan.Rewrite`.
`plan.Rewrite` may decline to produce a plan on a version where the technique
does not work — `SET NOT NULL` returns no plan below 12, because the
`CHECK`-constraint route only skips the scan from 12 onward and emitting the
same five steps on 11 would be theatre ending in the exact outage the user was
avoiding.

## Consequences

**The verdict genuinely changes across versions**, which §7 of the report
demonstrates by running one five-statement script against 10 through 16 and
tabulating the refusals. `TestVersionSweepChangesTheVerdict` pins the counts so
a regression in the version logic fails a test rather than quietly making the
tool version-blind again.

**Callers must know their version.** This is a real burden and it is
deliberate: a team that cannot say which major version production runs has a
larger problem than DDL linting, and the tool surfacing that is a feature.

**Every rule now has a version dimension in its test matrix**, roughly tripling
the case count for version-sensitive rules. Worth it — the bugs this catches
are precisely the ones that only appear on the one server nobody upgraded.

**A version-aware rule can be too clever.** `wideningOnly` decides whether a
type change is representation-preserving using only the *target* type, because
the parser does not see the current column type — it is not in the statement.
`int` → `bigint` is safe; `text` → `bigint` is not, and both look the same to
this function, so it must be conservative in the direction of "target type is
in the known-widening set" and accept that it will occasionally allow something
it should not have. This is the sharpest known limitation in the tool and is
documented in `docs/known-limitations.md` and in the function's own comment.

**Minor versions are ignored.** All the behaviour changes that matter here are
major-version changes. A rule needing minor-version granularity would need a
different representation, and none currently does.

## Alternatives considered

**Detect the version by connecting to the database.** Rejected: the tool is
designed to run in CI against a migration file, with no database reachable. A
tool that needs production credentials to tell you whether your SQL is safe has
inverted the problem.

**Default to the oldest supported version.** Rejected: maximally conservative
and maximally wrong on modern fleets. Refusing safe migrations is not the safe
error — it is the error that gets the tool switched off, after which nothing is
checked at all.

**Default to the newest.** Rejected for the opposite and worse reason: it fails
silently in the dangerous direction, on the servers least likely to be watched.

**Take a version range and report the worst case.** Genuinely attractive for a
heterogeneous fleet, and the natural next feature. Deferred because the
single-version case has to be right first, and because the report is more
legible with one version per verdict.
