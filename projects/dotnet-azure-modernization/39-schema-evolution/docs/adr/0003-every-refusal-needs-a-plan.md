# ADR 0003: Every refusal must come with a plan

## Status

Accepted.

## Context

A linter that blocks a migration and offers nothing is a linter that gets an
exemption flag within a fortnight. The sequence is predictable, and everyone
who has introduced one has watched it happen:

1. The tool refuses a migration on a Tuesday afternoon.
2. The engineer needs the migration to ship, and the tool has not told them
   what else to do.
3. Somebody adds `--no-verify`, or an allowlist, or a comment the tool
   respects.
4. Within a month the escape hatch is in the runbook, and the tool is
   decoration.

The failure is not that the refusal was wrong. It was right. The failure is
that being right is not enough — a safety tool has to be *more convenient than
going around it*, and refusal alone is strictly less convenient than doing
nothing.

## Decision

For every statement the linter flags, the tool generates the multi-phase
migration that achieves the same end state safely, and that generated plan must
pass the tool's own validator and linter.

`plan.Rewrite` handles seven classes:

| Refused statement | Generated plan |
| --- | --- |
| `ADD COLUMN ... NOT NULL` (no default) | add nullable, backfill, `CHECK ... NOT VALID`, `VALIDATE`, `SET NOT NULL`, drop check |
| `ALTER COLUMN ... TYPE` (narrowing) | shadow column, sync trigger, throttled backfill, verification query, dual-write deploy, transactional rename, drop trigger, drop old |
| `ALTER COLUMN ... SET NOT NULL` | `CHECK ... NOT VALID`, backfill, `VALIDATE`, `SET NOT NULL`, drop check |
| `CREATE INDEX` | `CREATE INDEX CONCURRENTLY`, validity check |
| `ADD CONSTRAINT ... UNIQUE`/`PRIMARY KEY` | `CREATE UNIQUE INDEX CONCURRENTLY`, `ADD CONSTRAINT ... USING INDEX` |
| `ADD CONSTRAINT ... CHECK`/`FOREIGN KEY` | `... NOT VALID`, then `VALIDATE CONSTRAINT` |
| `RENAME COLUMN` | add new, sync trigger, backfill, dual-read deploy, cut over, drop old |

The property is that **flagging and rewriting coincide exactly**: every flagged
statement gets a plan, no clean statement gets one. It is checked in §13 of the
report and in `TestRewriteOutputSurvivesItsOwnLinter`.

## Consequences

**The check found five bugs that unit tests did not.** All five were in the
seam between the two halves, which is exactly where unit tests do not look:

- A seven-step plan generated for `ADD COLUMN ... NOT NULL DEFAULT 'new'`,
  which PostgreSQL 11+ executes in milliseconds.
- `CREATE UNIQUE INDEX` patched with `strings.Replace(raw, "CREATE INDEX",
  "CREATE INDEX CONCURRENTLY", 1)` — which matches nothing, because
  `CREATE UNIQUE INDEX` does not contain the substring `CREATE INDEX`. The
  generated plan contained the original blocking build, presented as the fix.
- `ADD CONSTRAINT ... USING INDEX` refused by the rule that recommends it.
- The transactional rename refused because the parser did not know `BEGIN`.
- `ALTER COLUMN TYPE` and `SET NOT NULL` — the two most common risky statements
  in any real migration — refused with no alternative at all.

**The property had to be weakened once, and that was informative.** It
originally read "every *refused* statement gets a plan". `SET NOT NULL` broke
it: the linter warns rather than refuses, because on a small table the scan is
over before anyone notices. Tying the rewriter to refusals would mean
withholding the better form from the one person who asked. The property is
about whether the tool flagged something, not how loudly — hence
`lint.Report.Flagged()` alongside `Refused()`.

**Some plans need steps the tool cannot execute.** A trigger definition, a
throttled backfill, a code deploy. These are marked `Step.Manual` and skipped
by the validator. `Manual` is an explicit flag rather than an inference from
"the parser found nothing", because inferring it makes a typo — `ALTER TABEL
orders` — indistinguishable from a prose note, and a migration tool that
silently ignores a step it did not understand is worse than one that cannot
read the step at all.

**A generated plan can be more dangerous than the statement it replaces.** The
shadow-column rewrite is eight steps, spans two deploys, and ends in an
irreversible `DROP COLUMN`. It is safer *per step* and riskier *in aggregate*,
because there is more of it and it takes days. The tool presents the plan; it
does not claim the plan is always the right call. That judgement is disclosed
in the plan's own `Justification` fields and belongs to the reviewer.

## Alternatives considered

**Refuse, and link to documentation.** Rejected: this is the status quo the
decision exists to improve on. The gap between "read this page about
expand/migrate/contract" and "here are the eight statements in order" is where
migrations go wrong.

**Generate the plan but do not lint it.** Rejected: it is the linting that
finds the bugs. An unlinted generated plan is an untested code path that
produces SQL somebody runs against production.

**Apply the rewrite automatically.** Rejected outright. The rewrite changes
when the migration completes — from one statement to, in one case, two deploys
and a backup cycle. That is a scheduling decision, and a tool that makes it
silently has exceeded its authority.
