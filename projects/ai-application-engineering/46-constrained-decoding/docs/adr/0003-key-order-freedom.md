# ADR 0003: key-order freedom by bounded enumeration

**Status:** accepted

## Context

JSON objects are unordered. `{"a":1,"b":2}` and `{"b":2,"a":1}` are the same
document, and a model will emit whichever it prefers. An automaton that only
accepts one key order will mask away the model's natural choice and force it
down a path it considers unlikely — which, per ADR 0005, distorts the output
distribution far more than necessary.

Accepting every order means the automaton must accept every permutation of every
legal subset of the properties. For *n* optional properties that is
∑ C(n,k)·k! alternatives, which is worse than n!.

## Decision

Enumerate subsets × permutations up to an explicit cap
(`maxObjectAlternatives`, default 720 = 6!). When the cap is exceeded, fall back
to a fixed canonical order **and emit a diagnostic saying exactly that**.

## Consequences

The cost is real and measured. Comparing compilation with key-order freedom on
and off:

| schema | states, fixed order | states, free order | multiplier |
|---|---|---|---|
| flat-object | — | — | 1.00×–5.68× across the benchmark set |

The full table is in `docs/benchmark-results.md`. The multiplier is 1.0× for
single-property objects and grows quickly; 5.68× was the worst observed.

The fallback is a *subset* approximation (ADR 0002): the automaton accepts fewer
documents than the schema allows, never more. A caller who sees the diagnostic
knows the model is being forced into a canonical key order and can decide
whether to restructure the schema.

The default of 720 is a judgement call, not a derived constant. It is the point
at which compilation of the benchmark schemas stayed under two seconds on the
development machine. It is configurable precisely because that number is a
property of the machine and the schema, not of the algorithm.

## Alternatives considered

**Always require a canonical key order.** Simplest, and it is what several
libraries do. Rejected: it silently rewrites the model's preferred output, and
the distortion is invisible because the result is still valid.

**Track "which keys have been seen" as automaton state instead of enumerating.**
This is the right answer and it is what a hand-written parser would do — the
state is a bitmask over properties, so the blow-up is 2^n rather than ∑C(n,k)k!.
Rejected for this version only because the fragment builder is a pure Thompson
construction with no notion of a state variable, and threading one through would
have meant rewriting the builder. Recorded in `docs/known-limitations.md` as the
clearest available optimisation.
