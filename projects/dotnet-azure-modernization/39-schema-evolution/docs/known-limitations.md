# Known limitations

Written so that a reader can calibrate how far to trust each claim, and so that
a future maintainer knows which corners were cut on purpose.

## The model

**There is no PostgreSQL in the loop.** Every number in `docs/results.md` comes
from a simulator (ADR 0001). The simulator models the conflict matrix, queue
ordering, and arrival/service times. It does not model buffer pools, WAL,
autovacuum, planner behaviour, or index build cost. It cannot tell you how long
your `ALTER TABLE` will take; hold time is an *input*.

**Absolute figures are not predictions.** The workload uses Poisson arrivals
with exponential service times. Real traffic is bursty and correlated, so a
figure like "32,896 blocked query-seconds" is a property of the model, not a
forecast. Every headline claim is deliberately a *ratio between paired runs*
that differ in exactly one variable, because the arrival-process assumption
appears identically in both halves and cancels. Read the ratios; treat the
absolutes as units.

**The replica lag model is first-order.** `backfill.Replica` treats lag as a
single scalar that grows with applied work and drains at a fixed rate. Real
replication lag has multiple stages, spikes from checkpoints, and its own
feedback with the primary's WAL rate. The controller comparison in §10–§12 is
valid *relative to this plant*; the finding that multiplicative decrease damps
where additive decrease does not is a property of the controller, but the
specific breach counts are properties of this plant.

**No cross-table or catalogue lock contention.** Foreign keys take locks on the
referenced table; `ALTER TABLE` touches system catalogues that other sessions
also touch. The simulator models a single table's lock queue. Real migrations
occasionally deadlock in ways this cannot express.

**Lock queue fairness is simplified.** PostgreSQL's real behaviour around lock
groups, parallel workers, and `deadlock_timeout`-triggered reordering is more
subtle than strict FIFO. Strict FIFO is the right first-order model for the
effect being demonstrated and is wrong in the details.

## The parser

**The DDL subset is deliberately narrow.** `ALTER TABLE` subforms,
`CREATE`/`DROP INDEX`, constraints, `CREATE`/`DROP TRIGGER`, transaction
control, `SET`, `CLUSTER`, `VACUUM`, `ANALYZE`. Anything else parses to
`Kind: Unknown`, which is treated as ACCESS EXCLUSIVE and refused. On a real
codebase this would produce false positives on `GRANT`, `COMMENT ON`, extension
DDL and `DO $$ ... $$` blocks. The escape hatch is `Step.Manual`.

**No `$$`-quoted body handling.** A `DO` block or function body containing
semicolons will be split incorrectly by the statement splitter. The splitter
handles `'`, `"` and `--`/`/* */` comments, but not dollar quoting.

**`ALTER TABLE ... ADD COLUMN` handles one column per statement.** Multi-column
forms (`ADD COLUMN a int, ADD COLUMN b int`) parse only the first action.
PostgreSQL applies them under one lock, so the lock analysis is still correct;
the per-column rules only see the first.

## The rules

**`wideningOnly` sees only the target type.** This is the sharpest limitation
in the tool. Deciding whether `ALTER COLUMN ... TYPE x` is representation-
preserving requires knowing the *current* type, and the current type is not in
the statement — it is in the catalogue the tool deliberately does not read. So
the check is "is the target in a known-safe set", which is right for
`int → bigint` and wrong for `text → bigint`. It errs toward permissive on
exactly the cases it cannot see. Reading the current type from a schema dump
would fix this and is the single highest-value improvement available.

**No table size input.** `SET NOT NULL` on a 400-row lookup table is warned
about identically to `SET NOT NULL` on 400 million rows. The tool reasons about
lock *modes* and *scan classes*, not durations, so it cannot distinguish a scan
that takes 2 ms from one that takes 40 minutes. Severity is therefore a
statement about class, not about impact.

**Rule severities are judgements, not measurements.** The line between `WARN`
and `REFUSE` was drawn by hand. `SET NOT NULL` warns; `ALTER COLUMN TYPE`
refuses. Reasonable people would draw it differently, and a real deployment
would need per-team configuration, which does not exist here.

**`IsVolatileDefault` uses a fixed function list.** `now`, `random`,
`gen_random_uuid`, `clock_timestamp` and friends, plus the bare keywords. A
user-defined volatile function is not recognised, and the tool would call the
default non-volatile and permit a migration that rewrites the table. Detecting
this properly requires `pg_proc.provolatile`, which requires a database.

## The rewriter

**Generated plans are not executed.** `Validate` checks structure — every step
parses, every non-contract step has a rollback, irreversible steps are
justified — and `Lint` re-checks the SQL. Neither runs anything. `TestedRollback`
is a *claim* the plan makes about itself, not a verified fact, and it is named
to make that obvious.

**A generated plan can be riskier in aggregate than the statement it
replaces.** The shadow-column rewrite is eight steps across two deploys and
ends in an irreversible `DROP COLUMN`. Each step is safer; there is much more
of it. The tool presents the plan and does not claim the plan is always the
right call.

**Backfill steps are prose.** `-- backfill in lag-throttled batches` is a
`Manual` step. The `backfill` package models the controller that would do it,
but the two are not wired together — the tool does not generate executable
backfill code.

**The discharge mechanism is a trust boundary.** `Step.Discharges` lets a plan
downgrade a specific `REFUSE` to `INFO` when the plan supplies the mitigation
the rule asks for (currently only `rename-breaks-deployed-code`, discharged by
a preceding dual-read deploy step). Findings are annotated, never removed, and
the rule ID must be named explicitly — but a future maintainer who reaches for
this to silence an inconvenient rule will find it works. That risk is accepted
in exchange for not weakening the rule itself.

## Reproducibility

**`TestResultsAreReproducible` takes ~16 seconds** and regenerates the entire
report. It is skipped under `-short`.

**Line endings are normalised before comparison.** A Windows checkout with
`core.autocrlf=true` rewrites LF to CRLF on disk, which would fail a byte
comparison for reasons unrelated to the model.

**No `-race` coverage.** The race detector requires cgo and a C toolchain,
unavailable in the build environment. The simulator is single-goroutine and the
report generator is sequential, so there is nothing for it to find today — but
that is an argument from inspection, not from evidence.
