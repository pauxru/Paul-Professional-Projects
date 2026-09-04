# ADR 0001 — Where the determinism boundary sits

## Status
Accepted.

## Context

The claim this repository makes is "any failure replays exactly from its seed".
That claim is either true or the repository is worthless, so the boundary
between deterministic and nondeterministic has to be explicit rather than
aspirational.

Sources of nondeterminism in a normal Rust program that would silently break it:

- `SystemTime` / `Instant`
- threads and any scheduler
- `HashMap` / `HashSet` *iteration order* (randomly seeded per process)
- pointer addresses, allocator behaviour
- floating point across targets
- anything reading the environment

## Decision

Exactly one `Rng`, seeded once per run, owned by the simulator. Every random
choice the *engine* makes draws from it. Each client owns a second `Rng`
derived deterministically from the run seed, so client decisions are also a
function of the seed.

Concretely:

- **No threads.** The executor is a single loop over a `BinaryHeap`.
- **No wall clock.** Logical time is a `u64` that advances only when the queue
  says so. `std::time::Instant` appears exactly once, in `main.rs`, to time the
  checker for a documentation table — never inside a run.
- **Total order on events.** Every scheduled event carries a monotonically
  increasing sequence number and the heap orders on `(time, seq)`. Without this,
  two events at the same logical instant are ordered arbitrarily.
- **No `HashMap` iteration.** The one `HashMap` in the codebase is the
  linearizability memo, which is only ever probed by key.
- **Floating point is confined to `Rng::chance`,** which compares against an
  integer draw scaled by `2^53`. That is exactly representable, so the
  comparison is the same on every target.

## Consequences

Replay is free: rerun the binary with the same seed. There is no trace file to
capture, ship, or keep in sync with the code — which is the failure mode of
record/replay systems.

The cost is that the simulator cannot host code that is not written for it. Real
`tokio`, real sockets and real threads are all out. That is a real restriction
and it is the price of the property; `docs/known-limitations.md` states it
plainly.

The `tests/regression_seeds.rs` file guards this decision. It pins ten seeds and
asserts they still fail. Any change that perturbs the RNG stream or the event
ordering breaks it loudly, which is correct: such a change silently invalidates
every counterexample anyone has ever recorded.
