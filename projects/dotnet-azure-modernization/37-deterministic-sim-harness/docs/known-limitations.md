# Known limitations

Stated plainly, because a simulation harness that oversells its coverage is
worse than no harness — people stop looking.

## The simulator can only run code written for it

Nodes implement `Node<M>` and communicate through `Ctx`. Real `tokio`, real
sockets, real threads and real timers are all excluded by construction (ADR
0001). This is the fundamental trade: FoundationDB got around it by writing
their own language runtime (Flow); this repository gets around it by only
simulating protocols that were written against the harness.

So a green run says the *protocol* is sound under the modelled faults. It says
nothing about the production implementation of that protocol, unless the
production implementation is the same code.

## The fault model is not the real world

Modelled: latency (uniform), loss, duplication, reordering, asymmetric network
partitions, per-node clock skew, per-link stragglers.

Not modelled, and each is a real source of production incidents:

- **Node crashes and restarts**, with and without state loss. This is the
  biggest gap. A crash-recovery model would exercise durability assumptions the
  current harness never touches.
- **Disk faults** — torn writes, `fsync` lying, silent corruption.
- **Byzantine behaviour.** Everything here is fail-silent.
- **Bounded queues and backpressure.** The event queue is unbounded, so a slow
  node never sheds load; real ones do.
- **Latency correlation.** Delays are drawn independently per message. Real
  congestion is bursty and correlated, which changes the interleavings reachable.

## The checker only checks a register

`linearizability.rs` is specialised to a single read/write register. Extending
it to a general object requires a sequential specification and, more painfully,
a much larger state space for the memo. The bitmask caps histories at 60
operations; a richer object would hit that limit sooner.

## Absence of failure is not proof

2,000 seeds with no violation is evidence, not a proof. The search is random,
and the sweep shows the detection rate is sensitive to workload shape by an
order of magnitude — a shape that never generates concurrent reads would report
a clean run for a broken protocol forever.

Concretely: this harness would *not* have found the bug at 7 replicas, and 7
replicas is a completely ordinary production configuration.

Deterministic simulation is a bug-finding tool. Model checking (TLA+, Stateright)
is the tool for exhaustive proof over a bounded state space, and the two are
complementary rather than competing.

## Shrinking is not minimisation

`shrink` holds the seed and reduces the configuration, so each candidate is a
*different* execution. See ADR 0004. The output is "the smallest configuration
that still fails at this seed", not a minimal counterexample.

## Timing numbers are not performance numbers

`docs/results.md` reports how long the *checker* takes. Nothing here measures
protocol throughput or latency, and it could not: latency is a parameter of the
simulation, not an outcome of it.

## The workload is small on purpose

Four clients, up to eight operations each, one operation in flight per client.
Larger histories make the exact checker expensive (ADR 0002) and, per the sweep,
do not reliably help. But a single in-flight operation per client is a real
restriction: it cannot express a client that pipelines requests, and pipelining
creates interleavings this harness never reaches.
