# The problem: what a transaction was actually doing for you

A monolith's `TransactionScope` does three things, and only one of them is
obvious.

The obvious one is atomicity: all six operations commit or none do. That is the
one everybody names, and it is the one people try to replace with a saga.

The second is that **failure has exactly one shape**. Inside a transaction, an
operation either returns or throws. There is no third outcome. The code that
handles failure is a `catch`, and it is correct because there is nothing else to
handle.

The third is that **rollback is somebody else's problem**. Nobody writes an
inverse for anything. The database does it, it does it correctly, and it does it
for state you forgot you were changing.

Split the monolith and all three go away, but only the first one goes away
*loudly*. The other two disappear silently, and the code that depended on them
keeps compiling.

## The third outcome

Across a network, an operation returns, is rejected, or **times out**. The third
case is not a rare edge. It is the normal case whenever anything is slow, and it
is the one that has no analogue in the transactional world, so the vocabulary for
reasoning about it does not exist in the codebase being split.

A timeout means: the request may have arrived and been processed, and the
response was lost. Or it may never have arrived. The orchestrator cannot
distinguish these, and no amount of care in the orchestrator will let it.
Everything difficult about sagas descends from this one fact.

Five of the six modelling defects in `04-bugs-the-checker-found.md` are the same
confusion: treating a fact about *this attempt* as a fact about *the step*. In a
monolith those are the same thing, because there is only ever one attempt.

## Rollback you have to write

The compensations are the part everyone expects to write, and they are still
harder than they look, because a compensation has to satisfy two requirements
that sound different and are not:

- it must be safe to run twice
- it must be safe to run for a step that never ran

Both are the statement *`comp(s)` is a no-op when the effect of `s` is not
present*. Once you see that they are one requirement, the rollback-scope question
answers itself -- and until you do, "skip the step we are not sure about" looks
like the careful choice. `results.md` measures what that choice costs: two dirty
aborts in a saga with no other defects.

## The step that has no inverse

Then there is the operation nobody wants to talk about. The confirmation email
has been sent. The parcel is on a van.

There is no compensation. There is no better compensation. The saga pattern's
answer -- the *pivot* -- is not a mechanism, it is an admission: past this point
the saga can only go forward, so put everything you might want to undo before it.

That reframes the design question from "how do we undo this?" to "what is the
last moment we can still change our mind, and is it late enough?" The extractor
in this repository exists to answer the second question, because it is a question
about the ordering of the whole transaction and not about any single step.

## What this project is

Given a transaction: find where the pivot goes, and then prove -- exhaustively,
within a stated bound -- that every reachable rollback actually puts the world
back.

Not "write a saga framework". There are enough of those, and none of them will
tell you that your compensation releases a resource its neighbour already
released.
