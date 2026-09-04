# ADR-003: Compensate the uncertain step, do not skip it

## Status
Accepted, and it is the most counter-intuitive decision here.

## Context
A step times out. The orchestrator does not know whether it ran. Two policies:

1. Compensate it anyway. A correct compensation is a no-op when the effect is
   absent, so this covers both cases.
2. Skip it. Compensating something that never happened is itself a change.

Policy 2 is the one people reach for, because it sounds more careful.

## Decision
Policy 1. `CompensationScope.IncludeUncertainStep` is the default.

## Evidence
`results.md` runs both on v7 -- a saga with no other defects at all. Policy 1: no
violations. Policy 2: two dirty aborts, at depths 3 and 7. The saga is otherwise
identical.

## Why
"Compensate unconditionally" and "a compensation must be idempotent" are the same
requirement seen from two directions.

If `comp(s)` is a no-op when `s` did not land, running it on an uncertain step
costs nothing and covers the case where the step *did* land. If `comp(s)` is not
a no-op in that case, the compensation is wrong -- and it is wrong independently
of when you choose to run it, so skipping it is not a fix, it is a way of not
finding out.

The checker enforces exactly this as `CompensationNotNeutral`, which is why the
requirement is not merely stated but checked.

## Consequences
Every compensation must be written to tolerate a step that never ran. This is a
real constraint on the people writing them and it is worth the constraint,
because the alternative constraint -- "the orchestrator must know what happened"
-- is not satisfiable over a network.
