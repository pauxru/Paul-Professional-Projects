# ADR 005: A truncated run is never a reported run

**Status.** Accepted. Written after a safety valve published fiction.

## Context

The event loop has a ceiling on the number of iterations, added as a defence
against a configuration that fails to make progress. Without it, a bug that
stalls the loop hangs the process.

## Decision

`RunStats` carries a `truncated: bool`, set when a run hits the ceiling instead
of draining naturally. The experiment harness asserts that no reported run is
truncated, so a truncated run aborts report generation rather than appearing in
a table.

## Consequences

**The ceiling on its own was worse than no ceiling.** At reservation factors
below 1.0 the simulator hit an eviction livelock: 19,997,889 evictions, nothing
completed. The ceiling fired, the loop exited, and the statistics were computed
from the handful of requests that had finished before the thrashing began — all
of which, being early, had met their objectives. The run reported:

```
att=0.976, thr=0.00
```

97.6% SLO attainment. The only signal was one zero in one column among six, and
nothing in the output said the number was fiction.

Without a ceiling the process would have hung, and a hang is an unambiguous
instruction to investigate. **A safety valve that silently substitutes plausible
numbers for real ones is more dangerous than no safety valve at all.**

**The flag addresses a class, not a bug.** Three specific fixes addressed that
particular livelock — admission headroom, exponential backoff on preemption, and
a retry cap that converts an unsatisfiable reservation into an honest rejection.
Those fix the livelock. The flag fixes every future bug that stalls the loop,
including ones not yet written.

**The retry cap is the same principle applied one level down.** A systematically
insufficient reservation is not recoverable by rearranging which sequence is the
victim. After `max_restarts` attempts the honest response is to reject the
request, which moves the failure into the `rejected` column where it is
measured. Section 9's offered-success-rate metric counts every rejection as a
failure, so the gateway gains nothing by refusing work — the cost is visible.

**This is why the report's headline metric is offered success rate.** Measuring
attainment over *served* requests lets a policy win by refusing almost
everything, which is the standard way admission control is oversold. Both
numbers appear in section 9's table, and the difference between them is the
whole argument.

**Pinned by.**
`sim_invariants.rs::an_impossible_configuration_rejects_rather_than_thrashing`
runs a deliberately impossible configuration and requires that it terminate
un-truncated, conserve every request, and stay under 200,000 evictions.
`littles_law_holds_across_every_configuration` asserts `!truncated` on all
twelve configurations it covers.
`every_offered_request_is_either_completed_or_rejected` asserts it on all five
admission policies. `Bench::run` in `src/experiments.rs` asserts it on every
run that reaches the report.
