# ADR 0004 — Shrink the configuration, not the history

## Status
Accepted, with a caveat that is stated rather than hidden.

## Context

A counterexample with 16 operations across 4 clients is hard to read. Property
testing normally answers this with shrinking: remove parts of the input until it
stops failing.

For a simulated distributed run, "the input" is a seed. There is no
sub-structure to remove. Deleting an operation from a failing history does not
produce a smaller failing *run* — it produces a history that never happened.

Two options:

1. **Shrink the recorded history.** Search for a minimal subset that is still
   non-linearizable. Genuinely minimal, and genuinely misleading: the subset may
   not correspond to any execution the system can actually produce, so a reader
   can spend an afternoon reasoning about an impossible scenario.
2. **Shrink the configuration and re-run.** Reduce replicas, clients, and
   operations per client, keeping the seed fixed, and keep any reduction that
   still fails.

## Decision

Option 2.

```rust
pub fn shrink(seed: u64, cfg: &Config, net: &NetConfig) -> Config
```

It greedily reduces `ops_per_client`, then `clients`, then `replicas` (by two,
to stay odd), accepting a reduction only if `run_one` at the same seed still
reports a violation.

## Consequences

Everything it produces is a real execution. You can replay it, read the trace,
and reason about it knowing it happened.

The caveat, which matters: **holding the seed fixed while changing the
configuration produces a different run, not a subsequence of the original.** The
RNG stream is consumed by different code in a different order. So the shrunk run
is not "the same bug, smaller" — it is "a bug of the same kind, found nearby in
seed space". In the measured case it shrank an 8-operations-per-client
configuration to 4 and the resulting counterexample was the same shape, but that
is an observation, not a guarantee.

Calling this "the smallest configuration that still fails at this seed" is
accurate. Calling it "the minimal counterexample" would not be, and the
docstring says so.

A stronger design would record the RNG draw sequence per component so that
reducing one dimension does not perturb the others. That is real work and is
listed in `docs/known-limitations.md` rather than pretended away.
