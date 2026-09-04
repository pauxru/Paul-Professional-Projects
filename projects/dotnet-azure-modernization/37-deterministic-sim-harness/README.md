# Deterministic Simulation Harness

A FoundationDB-style deterministic simulator, and a quorum-replicated register
to point it at.

The new services pass every test and fail in production every third Tuesday.
The bug is a timing interleaving, so it is not in the code you are reading, it
is in the order two messages happened to arrive in. You cannot fix what you
cannot reproduce.

This repository makes that class of bug reproducible. A whole distributed
execution — message latencies, losses, duplications, reorderings, network
partitions, clock skew, straggler links, and the interleaving of every client —
is a pure function of one `u64` seed. Find a failure once and you can replay it
forever.

## The headline

An ABD-style quorum register is implemented twice, differing in one flag. With
`read_repair = true` the read writes its observed value back to a majority
before returning. With `read_repair = false` it returns immediately, which looks
like a free optimisation and is not linearizable.

| network | read-repair | runs | violations |
|---|---|---|---|
| perfect | on | 2000 | 0 |
| **perfect** | **off** | **2000** | **0** |
| faulty | on | 2000 | 0 |
| **faulty** | **off** | **2000** | **46 (2.30%)** |

The row that matters is `perfect / off`. **The broken protocol is invisible on a
healthy network.** That is why this class of bug ships, survives code review,
passes staging, and then costs someone a weekend.

Every one of those 46 failures replays byte-identically from its seed:

```
$ cargo run --release -- replay 89 broken
seed 89
  network: 213 delivered, 7 dropped, 4 duplicated, 20 reordered, 56 partition changes
  history: 16 operations (14 completed, 2 abandoned)
  NOT LINEARIZABLE (operation 8)
  read by client 0 returned 62 at t=118049, but by the time it was invoked
  (t=71321) the completed writes were [12, 62]; no total order over the
  remaining operations makes that value current
```

## What is actually here

**A deterministic executor** (`src/sim.rs`). Logical time, a totally ordered
event queue, and a fault-injecting network. No threads, no sockets, no wall
clock. Ties in the event queue are broken by an explicit sequence number,
because "two events at the same instant" is otherwise resolved by whatever the
heap felt like and the run stops reproducing.

**An exact linearizability checker** (`src/linearizability.rs`). The Wing–Gong
search with memoisation over a bitmask of linearized operations. It handles
operations that never returned — they may or may not have taken effect, and a
checker that ignores that is unsound. When a history fails, it identifies *which*
read cannot be explained and says why in English.

**A real protocol** (`src/abd.rs`). Quorum reads and writes with logical
timestamps, majority counting that is robust to duplicate responses, per-request
ids so a stale reply from an abandoned operation is not counted, and operation
timeouts that leave a pending entry in the history rather than a lie.

**Shrinking** (`src/runner.rs`). Reduces replica count, client count and
operations-per-client while the seed keeps failing.

## Findings that were not what I expected

**Bigger clusters hide the bug.** My prior was that larger quorums mean more
room to disagree. The sweep says the opposite: at 3 replicas the violation rate
reaches 2.6%, at 7 replicas it is essentially zero. A read quorum of 4-of-7
overlaps a partially-completed write far more reliably than 2-of-3 does. The
naive "test on a big cluster" instinct is exactly wrong.

**Two of the six faults do all the work.** Disabling each fault in turn:

| fault disabled | violation rate |
|---|---|
| none | 2.30% |
| message loss | 2.00% |
| duplication | 2.45% |
| **reordering** | **1.40%** |
| partitions | 2.25% |
| clock skew | 2.55% |
| **straggler links** | **1.25%** |

Loss, duplication, partitions and clock skew barely matter. Reordering and
straggler links are load-bearing, because the bug needs a write that has reached
*some* replicas and not others, and *stays* that way long enough for two reads
to disagree. Straggler links were not in the first version of the fault model,
and adding them roughly quadrupled the detection rate.

## Running it

```powershell
.\build.ps1                    # compile
.\test.ps1                     # 37 tests
.\demo.ps1                     # regenerate docs/results.md

cargo run --release -- fuzz 2000 broken   # hunt for seeds
cargo run --release -- replay 89 broken   # replay one
cargo run --release -- shrink 89          # minimise it
cargo run --release -- sweep              # workload shape sweep
```

Rust is not on `PATH` in this environment; `cargo.ps1` wraps `cargo` with the
right `CARGO_HOME`, `RUSTUP_HOME` and MSVC linker environment. `build.ps1`,
`test.ps1` and `demo.ps1` all go through it.

## Layout

```
src/rng.rs               SplitMix64, with a known-answer test
src/sim.rs               deterministic executor + fault-injecting network
src/linearizability.rs   exact Wing-Gong checker
src/abd.rs               quorum register protocol + workload
src/runner.rs            run, fuzz, shrink, report
src/main.rs              CLI and the experiment that writes docs/results.md
tests/regression_seeds.rs recorded counterexamples that must keep reproducing
docs/results.md          every number in this README, regenerated
docs/adr/                why it is built this way
docs/known-limitations.md what it does not do
docs/portfolio/          the narrative version
```

## Reading order

1. `docs/portfolio/01-the-problem.md` — why reproducibility is the whole game
2. `docs/portfolio/03-the-fault-that-mattered.md` — the ablation, and what it changed
3. `src/linearizability.rs` — the part with the actual algorithm in it
4. `docs/known-limitations.md` — the honest boundary
