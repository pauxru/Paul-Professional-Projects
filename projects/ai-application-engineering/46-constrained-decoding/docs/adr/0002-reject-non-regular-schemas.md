# ADR 0002: reject schemas that are not regular, rather than approximate them

**Status:** accepted

## Context

A DFA can only recognise a regular language. Most of JSON Schema, restricted to
statically bounded nesting, is regular. Two things are not:

- **Recursive `$ref`.** A tree node containing a list of tree nodes needs
  unbounded balanced nesting, which is context-free, not regular.
- **Cross-field constraints.** `"required"` interacts with key ordering in ways
  that are regular only because the number of keys is bounded; genuinely
  relational constraints (`"if"`/`"then"`, `"dependentRequired"` across
  arbitrary depth) are not.

Every constrained-decoding library faces this and most of them choose to
approximate: enforce the parts that are regular, ignore the rest, and let the
caller validate afterwards.

## Decision

The compiler **fails** with a specific error message when it meets a construct
it cannot represent exactly. It never silently accepts a superset of the schema.

Where an approximation is unavoidable but sound — bounding an unbounded string
length, capping the number of key-order permutations — the compiler records a
**diagnostic** that the caller can read through `cdec_diagnostics`, and the
approximation is always a *subset* of the schema's language, never a superset.

## Consequences

The library refuses schemas that competitors accept. That is the intended
trade: the failure mode of a superset approximation is that the decoder emits a
document which is syntactically well-formed, passes the automaton, and violates
the schema. Because it parses, it flows straight into downstream code. A loud
compile-time rejection is strictly better than a quiet runtime lie.

The distinction between subset and superset approximation is load-bearing and
is tested: `schema_rejects_unsatisfiable_pattern_length_combination` asserts the
compiler reports an unsatisfiable conjunction instead of producing an automaton
that accepts nothing and wedges the decoder on its first token.

## Alternatives considered

**A pushdown automaton for recursive schemas.** Genuinely correct, and it would
lift the recursion restriction. Rejected for now on cost grounds, not principle:
the mask computation becomes a question about a stack-augmented configuration,
the per-state mask cache stops being sound because the mask depends on stack
depth, and the entire measured caching story in `docs/benchmark-results.md`
would have to be redesigned. Documented in `docs/known-limitations.md` as the
single biggest gap.

**Enforce what is regular, validate the rest afterwards.** Rejected: it
reintroduces the retry loop this project exists to remove, while adding the
false confidence of a constraint mechanism that only half works.
