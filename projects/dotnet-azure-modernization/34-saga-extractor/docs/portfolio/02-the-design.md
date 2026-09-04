# The design: why a state machine and not a test suite

## The shape of the failures

Every counterexample in `results.md` is between zero and six transitions from the
initial state. That is short. Short enough that each one could be an integration
test somebody writes by hand.

Nobody writes them, and the reason is not laziness. The failing traces need a
timeout on one step **and then** a rejection on a later one. Each half is
uninteresting alone -- a timeout that resolves on retry is not a bug, a rejection
that aborts cleanly is not a bug -- so neither half suggests the test. The bug
lives in the combination, and there is no shortage of combinations.

This is why exhaustive search wins here, and it is worth being precise about the
reason. It is not that the bugs are deep. It is that the search is
**unimaginative**: it has no sense of which combinations are worth trying, so it
does not skip the boring ones. That was a prediction I got wrong -- I expected
long counterexamples and wrote that expectation down before measuring, and
`results.md` records it as contradicted.

## What is in the state

A configuration is: the world, the phase, a cursor, an attempt count, a crash
count, and a packed record of what has landed.

The packed record is the part that took two attempts. One bit per step is the
obvious encoding and it cannot represent a non-idempotent step that landed twice
-- see ADR-002. Two bits, saturating, fifteen steps maximum, and a constructor
that refuses a longer saga rather than corrupting a neighbour's counter.

Everything else is small on purpose. The state vector is a handful of integers
because the search visits every reachable combination, and each variable
multiplies the space.

## Breadth-first, not depth-first

Depth-first would use less memory and find the same violations. BFS was chosen
for one reason: **the first time it reaches a state is by a shortest path**, so
every counterexample it prints is minimal. There is no shorter explanation for
the failure.

That is a usability property, not a correctness one, and it is worth the memory.
A fifteen-transition trace is something an engineer nods at and files. A
four-transition trace is something they read.

Keeping the parent pointer on *first discovery only* is what makes this true, and
it is an easy thing to get wrong -- writing it on every visit still produces
working output, just not minimal output, and you would never notice from reading
it. There is a test.

## Properties in two families

Some properties are facts about the design and need no exploration: the shape
rule, the algebraic reverse-order check, the idempotence-declaration check. They
hold or fail before the saga runs, so they carry no trace and cost microseconds.

The rest need the state space: invariants, compensation neutrality, dirty aborts,
stuck states.

These are reported through the same list but with an empty trace for the first
family, and that distinction is deliberate. A well-shaped saga is not a proved
saga, and merging the two channels would let a reader believe otherwise.

## Design mutation

The last piece is the one I would keep if I could keep only one. Revert each
design fix individually and re-check. A fix that can be reverted with no
violation appearing is a fix that was not needed -- or a hole in the property set.

All six reverts fire. That is the boring outcome and the one I predicted.

The interesting outcome came from a related test asking whether every *property*
is reachable from some design. Two were not. Both were real, correct, useful
checks, and one of them was a fully-written function that nothing ever called.
The checker had been confidently reporting `Safe` on designs it had no ability to
reject.

There is a general lesson in that and it is not about sagas. A test suite tells
you what fails. It takes a different kind of question -- *can this check fail at
all?* -- to find the checks that cannot.
