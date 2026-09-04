# ADR-001: Model the state space explicitly instead of writing more tests

## Status
Accepted.

## Context
The failures this project exists to find are all of the form "a timeout on one
step, then a rejection on a later one". They are not deep -- the deepest
counterexample in `results.md` is six transitions -- and any one of them could be
written as an integration test.

The problem is that nobody writes it. Each half looks uninteresting alone: a
timeout that resolves on retry is not a bug, and a rejection that aborts cleanly
is not a bug. The bug is in the pair, and there are O(n^2) pairs, O(n^3) triples,
and no reason to expect the interesting one to be near the front of the list.

## Decision
Enumerate the reachable state space by breadth-first search and assert properties
at every state, rather than asserting outcomes for hand-chosen scenarios.

## Consequences
Positive: the search is unimaginative, which is the whole advantage. It has no
intuition about which combinations are worth trying, so it does not skip the ones
a person would find boring. Every counterexample is minimal-length, because BFS
reaches each state by a shortest path first.

Negative: it requires the participants to be pure functions of a small state
vector. That is a real modelling restriction and it is the reason this checks a
design rather than an implementation.

Negative: the space is only finite because crashes are bounded. `results.md`
measures how much that bound hides, and the answer on a defective saga is "more
than you would like".

## Alternative considered
TLA+ or Alloy would be a better model checker than this one by every technical
measure. They were rejected because the artefact here is a *tool that reads a
transaction and produces a saga*, and the checking is one stage of it. A model in
a separate language is a document that drifts from the code; a checker in the
same language as the extractor can consume the extractor's output directly, which
is what makes the design-mutation analysis possible at all.
