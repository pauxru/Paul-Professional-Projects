# 04 — What I would do next

## Crash-recovery is the biggest gap

Everything here is fail-silent: nodes are slow, unreachable, or reordered, but
never dead, and never restarted with a different view of their own state.

Crash-recovery is where durability assumptions get tested, and it is where the
bugs are worse: a node that restarts having lost an unfsynced write is
indistinguishable, to its peers, from a node that never received it — except
that it *acknowledged* it. That class of bug does not show up in any fault this
harness currently models.

The engine already supports it structurally: a crash is "drop this node's state
and every message in flight to it". The work is deciding what survives a
restart, which means giving replicas an explicit durable/volatile split — a real
design change to `abd.rs`, not an addition to `sim.rs`.

## Per-component RNG streams, so shrinking actually shrinks

Today, `shrink` holds the seed and changes the configuration, so each candidate
is a different execution (ADR 0004). The shrunk run is a nearby bug, not a
smaller version of the same one.

The fix is standard and not cheap: give each component its own RNG stream
derived from `(seed, component_id)`, so reducing client count does not perturb
the network's draws. Then shrinking becomes real minimisation and the output can
honestly be called a minimal counterexample.

## A general sequential specification for the checker

`linearizability.rs` hardcodes a register. Generalising to
`trait Sequential { type Op; type Ret; fn apply(&mut self, op) -> Ret; }` would
let the same checker verify a queue, a lock, or a set, which is where the
interesting protocols are.

The obstacle is the memo key. Today it is `(bitmask, u64 value)`. A general
object needs a hashable state, and rich states make the memo much less
effective — the search stops sharing work between branches. Realistically this
also needs the state space bounded or the checker will stop being affordable,
which trades away the exactness argument from ADR 0002.

## Coverage feedback instead of blind seed enumeration

Right now seeds are enumerated `0..N`. That is uniform sampling over a space
where interesting executions are rare — hence the 2.3% rate.

Modern fuzzers do better by instrumenting for coverage and steering. The
distributed-systems analogue is not line coverage but *state* coverage: how many
distinct (replica-state-vector, in-flight-message-multiset) shapes were reached.
Steering towards new shapes would find rare interleavings much faster than
enumeration.

This is the single change most likely to make the harness dramatically better,
and it is also the one most likely to break determinism if done carelessly —
the steering must be a pure function of past runs, not of the machine it runs on.

## Bounded queues

The event queue is unbounded, so a slow node never sheds load. Real systems have
bounded queues, and a full queue is a failure mode with distinctive
consequences: timeouts under load, retry storms, metastable failure. None of
that is reachable here.

## What I would leave alone

**The action-emitting `Ctx`.** The borrow-checker friction that motivated it
turned out to be the smaller benefit; the real one is that a node structurally
cannot read global time or peer state, so a protocol that only works in
simulation is hard to write by accident.

**Exact checking.** It is tempting to sample orderings and buy larger histories.
But the assertion that carries the argument is "2,000 runs, *zero* violations
for the correct protocol", and that assertion is only worth making if the
checker cannot miss one. A faster checker that occasionally says "linearizable"
when it is not would make every green run meaningless.

**The small workload.** The sweep says history length is not what finds bugs;
concurrency shape is. Buying longer histories at the cost of exactness would be
paying for the wrong thing.
