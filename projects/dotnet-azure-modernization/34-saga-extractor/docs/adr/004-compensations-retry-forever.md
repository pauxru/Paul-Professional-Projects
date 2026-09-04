# ADR-004: Compensations retry forever; forward steps do not

## Status
Accepted.

## Context
Unbounded retries are how you build a system that hammers a dead dependency at
full rate for a week. The instinct is to cap every retry.

## Decision
Forward steps have an attempt cap. Compensations do not.

## Evidence
`results.md` caps compensation attempts on v7 and gets three stuck states, at
depths 4 and 5. With unbounded compensation retries, zero.

## Why
The asymmetry is not arbitrary. A forward step that gives up has somewhere to go:
abort, and roll back. A compensation that gives up has nowhere. It cannot go
forward -- the saga already decided to abort -- and it cannot finish going back.
The execution is left in a state no code is written to handle, which in practice
means a human reading a database by hand at 3am.

## The apparent conflict, resolved
"Retry forever" is a statement about the state machine. "Back off and page
someone" is a statement about the operator. They are not in conflict: the retry
must be unbounded in *attempts* and bounded in *rate*, and a compensation that
has been failing for an hour is an alert, not an error return.

Conflating the two is how stuck sagas get built, and it happens because "retry
forever" sounds operationally reckless to anyone who has been paged by a retry
storm.

## Escape hatch
Some compensations genuinely cannot succeed -- an authorisation hold on a card
that has since been cancelled. The answer is not a retry cap. It is
`Step.CompensationFallback` plus `Saga.AcceptableResidue`: a declared alternative
and a declared, signed-off residue. The residue is then visible in the design
rather than discovered in production, and `results.md` shows that removing the
fallback reintroduces a stuck state at depth 4.
