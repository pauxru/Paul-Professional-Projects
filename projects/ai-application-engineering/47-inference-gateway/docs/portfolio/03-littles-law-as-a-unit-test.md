# Little's Law as a unit test

`L = λW`. The mean number of items in a system equals the arrival rate times the
mean time each spends there.

It is usually taught as an analysis tool — a way to reason about a queue you
cannot instrument. This project uses it the other way round: as an **audit of
the simulator itself**, computed two independent ways and asserted to agree.

That reversal caught the worst bug in the codebase, and it is the technique from
this project I would carry to any other.

## Why it works as a check

Little's Law assumes almost nothing. It holds for any stable queueing system
regardless of arrival distribution, service distribution, scheduling policy, or
number of servers. There is no "M/M/1" qualifier on it. It is essentially a
conservation argument about area under a curve.

That is what makes it useless as a prediction — it tells you nothing you can act
on — and ideal as an invariant. **Because it must hold, it can be tested.**

The two sides come from genuinely different code paths:

- **`L`** is measured by integrating the in-system count over time inside the
  event loop. It depends on the clock advance being correct.
- **`λ` and `W`** are measured from completion records. They depend on
  per-request timestamps being correct.

A bookkeeping error in the event loop breaks the first and not the second.
Nothing else in the crate would notice — every individual number would remain
plausible, because each is computed correctly from a state that is wrong.

## What it caught

An early event loop kept a single global clock. Each iteration stepped every
replica, then advanced the clock by the *minimum* elapsed time reported. A
replica that had spent 25ms on a large batch was recorded as having spent 12ms,
because a different replica finished a small batch faster.

Single-replica runs were correct, because with one replica the minimum is the
only value. Multi-replica runs produced throughput that scaled slightly better
than linearly. **That is not obviously wrong.** You have to already believe
scaling should be sublinear to find 2.1× suspicious, and if you were expecting
linear scaling you would have congratulated yourself.

Little's Law disagreed by several percent on multi-replica runs and by nothing
at all on single-replica runs. That pattern is the entire diagnosis: an error
appearing only when replicas differ is an error in how replicas are combined.

It then caught the *fix*. With per-replica clocks, occupancy has to be sampled
at every replica-clock boundary — including the boundary belonging to a replica
that has just emptied. Omitting those over-counts the integral, and the identity
broke again, this time for a pure bookkeeping reason with no physical meaning.
The two failures were indistinguishable from the outside; only the residual
told me which one I was looking at.

## The measured result

| configuration | measured L | λ (req/s) | W (s) | λ × W | relative error |
|---|---|---|---|---|---|
| fifo, ρ 0.85 | 46.31 | 3.14 | 14.73 | 46.31 | 0.0066% |
| 2 replicas, ρ 0.95 | 33.40 | 3.77 | 8.86 | 33.43 | 0.0813% |
| kv 60k, factor 0.85 | 65.01 | 2.03 | 31.97 | 65.00 | 0.0189% |

The registered prediction was 2%, with the residual coming from finite-run edge
effects. The worst disagreement across every configuration in the report is
**0.0813%**, which is tighter than predicted for a reason worth stating: the
simulator drains rather than stopping at a horizon, so no request contributes to
occupancy without also contributing a completion time. Truncating the run would
have produced exactly the finite-run residual I expected, and the fact that it
does not is evidence the drain is correct.

## The general technique

**Find a quantity your system must satisfy for structural reasons. Compute both
sides from independent code paths. Assert they agree.**

It is the cheapest real check available on a simulation, and it has three
properties a golden-output test does not:

**It keeps working when the results legitimately change.** Retuning
`EngineConfig` invalidates every stored number in a golden file. It does not
invalidate `L = λW`, because the identity does not care what the constants are.
During calibration — where the engine parameters changed a dozen times — this
was the only assertion that stayed meaningful throughout.

**It has no opinion about which component is wrong.** That sounds like a
weakness and is the opposite. A unit test tells you that the component you
suspected is behaving as you specified. This tells you that the *system* is
inconsistent, which is what you need when you do not yet know where to look — it
found a bug in the clock while I was investigating the scheduler.

**It scales to configurations you did not think to test.** The assertion runs on
all twelve configurations in `littles_law_holds_across_every_configuration`, and
it would have caught the lockstep bug on any of the multi-replica ones. A
hand-written expectation only covers the case someone thought of.

## Where else this applies

The pattern is not specific to queueing:

- **A ledger.** Sum of balances must equal sum of transactions. Compute the
  first from account state and the second from the journal.
- **A cache.** Hits plus misses must equal lookups, and evictions plus current
  size must equal insertions. Count each from a different layer.
- **A distributed log.** Bytes written by producers must equal bytes read by a
  full consumer scan, per partition.
- **A scheduler.** Total busy time plus total idle time must equal wall time ×
  worker count.

In each case the check costs a few lines, has no maintenance burden, and fails
in exactly the situation where every individual metric still looks fine.

Its limit is worth stating: it verifies **internal consistency, not fidelity**.
A simulator with a perfectly consistent but physically wrong engine model
satisfies Little's Law exactly. It tells you the bookkeeping is right. Whether
the thing being booked resembles reality is what
[`../known-limitations.md`](../known-limitations.md) is for.
