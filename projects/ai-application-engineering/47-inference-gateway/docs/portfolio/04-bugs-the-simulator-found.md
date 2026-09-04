# Six bugs the simulator found in itself

Five of these six bugs produced *plausible output*. None of them crashed. That
is the reason this document exists: in a simulation, the dangerous failure is
not the one that panics, it is the one that returns a number shaped exactly
like the number you were expecting.

Each entry below records what the bug was, what it looked like from the
outside, what actually found it, and what changed as a result. Where a test now
pins the fix, it is named.

---

## 1. Lockstep replica clocks

**What it was.** The first event loop kept a single global clock. On each
iteration it stepped every replica, collected the elapsed time each one
reported, and advanced the clock by the *minimum* of those. A replica that had
taken 25ms to run a large batch was credited with having only spent 12ms,
because some other replica had finished a small batch faster.

**What it looked like.** Nothing. Single-replica runs were correct — with one
replica the minimum is the only value. Multi-replica runs produced throughput
that scaled slightly better than linearly, which is wrong but not obviously
wrong; you have to already believe scaling should be sublinear to find 2.1×
suspicious.

**What found it.** Little's Law. `L = λW` must hold for any stable queueing
system regardless of arrival distribution, service distribution, scheduling
policy or server count. The simulator measures `L` by integrating in-system
occupancy inside the event loop, and measures `λ` and `W` from completion
records — two independent code paths. The identity disagreed by several
percent on multi-replica runs and by nothing at all on single-replica runs.

That pattern is the whole diagnosis: an error that appears only when replicas
differ is an error in how replicas are combined.

**The fix.** Per-replica clocks. Each replica advances its own time; the
gateway processes whichever replica is next to become free. The global clock
became a derived quantity rather than a driver.

**The second-order fix, which mattered more.** Once clocks were per-replica,
the occupancy integral needed every replica-clock boundary to be an event —
*including a replica that had just emptied*. Missing those boundaries left the
integral over-counting, and Little's Law broke again, this time for a pure
bookkeeping reason with no physical meaning at all. Both failures looked
identical from the outside.

**Pinned by.** `tests/sim_invariants.rs::littles_law_holds_across_every_configuration`,
which runs twelve configurations including 2 and 4 replicas and requires
relative error below 2%. The measured worst case across the whole report is
0.0813%.

**What generalises.** Find a quantity your system must satisfy for structural
reasons. Compute both sides from independent code paths. Assert they agree.
Unlike a golden-output test, it keeps working when the results legitimately
change — and unlike a unit test, it has no opinion about which component is
wrong, which is exactly what you want when you do not yet know.

---

## 2. Greedy replica fill

**What it was.** The gateway filled replicas by iterating over them in index
order and admitting as much as each would take. Replica 0 therefore reached its
batch or memory limit before replica 1 received anything.

**What it looked like.** A second replica bought roughly half of what it should
have. That is not an absurd number — sublinear scaling is exactly what you
*expect* from a shared-nothing fleet if you are not paying attention, and a
plausible story ("head-of-line blocking limits the benefit") was available for
free.

**What found it.** Writing section 9, which compares adding capacity against
shedding load. Doubling the fleet came out worth about 1.5×, which made the
shedding policies look competitive. That result was interesting enough to check
the mechanism, and the mean-batch column showed replica 0 at 47 and replica 1 at
6.

The instructive part: the bug was found because the result was *interesting*,
not because it was wrong-looking. A less interesting section would have shipped
it.

**The fix.** Least-loaded-first fill with an `exhausted` set: repeatedly pick
the replica with the smallest current batch, try to admit one request, and mark
a replica exhausted when it refuses. This is O(replicas) per admission and the
fleet sizes here are single digits.

**Pinned by.** `tests/sim_invariants.rs::replicas_are_filled_evenly`, which
requires two replicas to deliver between 1.8× and 2.3× the measured capacity of
one. The upper bound matters as much as the lower: a fleet that delivers more
than 2× from two replicas has a different bug.

---

## 3. KV double-counting

**What it was.** A sequence charges KV capacity in two ways. It has a
*reservation*, made at admission time from a predicted output length, and it has
*actual* tokens held, which grow one per generated token. The first
implementation summed them. A sequence that had reserved 1000 tokens and grown
into 300 of them was billed 1300.

**What it looked like.** Mean batch size around 14 against a configured maximum
of 48, and SLO attainment that never exceeded about 60% at any utilisation —
including at ρ=0.4, where the system should be almost entirely idle. Every
individual number was reasonable. The *combination* was not, but only if you
knew what to expect from the combination.

This bug was caught during calibration rather than by a test, and it is worth
being honest about why: I was tuning `EngineConfig` to make the physics
plausible, and no setting made ρ=0.4 look like ρ=0.4. Tuning that cannot reach a
sensible operating point is usually a modelling error, not a tuning error.

**The fix.** `Running::charge()` returns `max(kv_held, reserved_kv)`. Summing
double-counts; using only `kv_held` makes the reservation a no-op and deletes
the entire point of admitting on a projection. The maximum is the only
definition that expresses "this sequence holds its reservation until it
outgrows it".

**Pinned by.** Four tests in `tests/engine_test.rs`:
`charge_is_max_not_sum`, `charge_follows_actual_use_once_the_reservation_is_exceeded`,
`charge_never_falls_below_the_reservation`, and the two `grows_next_token` tests
that decide whether the *next* token consumes fresh capacity.

---

## 4. The eviction livelock

The worst one, and the reason `RunStats::truncated` exists.

**What it was.** At reservation factors below 1.0 — the deliberate
over-subscription that section 10 exists to study — the run performed
**19,997,889 evictions and completed nothing**.

The cycle: every sequence reserves less than it will need. Memory runs short.
The gateway evicts a sequence, which frees a large block. That makes the evicted
sequence immediately admissible again. It is re-admitted, over-grows, and is
evicted once more. Forever.

**What it looked like.** This is the part worth dwelling on:

```
att=0.976, thr=0.00
```

97.6% SLO attainment. The run terminated only because of an event ceiling I had
added as a safety net, and on termination it computed its statistics from the
handful of requests that had completed before the thrashing started — all of
which, being early, had met their objectives. **The safety valve silently
substituted plausible numbers for real ones.** The `thr=0.00` was the only
signal, and it was one column among six.

**Three fixes were needed.**

*Admission headroom.* `can_admit` has to reserve one token for **every sequence
already decoding**, not just for the newcomer. Without it, the gateway admits
into memory that its own next decode step is about to consume. This was the
proximate cause.

*Exponential backoff.* A preempted request gets a `not_before_us` deadline that
doubles with each restart. Linear backoff still lets a pathological
configuration spend an entire run thrashing; it just thrashes more slowly.

*A retry cap.* A systematically insufficient reservation is not recoverable by
rearranging which sequence is the victim. After a few attempts the honest
response is to reject the request. This converts a livelock into a number in the
`rejected` column, which is a result rather than a hang.

**The fourth fix, which mattered more than the other three.**
`RunStats::truncated`, set when a run hits the event ceiling instead of
draining, plus an assertion in the experiment harness that no reported run is
truncated.

The three fixes above address *this* livelock. The flag addresses the class:
any future bug that stalls the loop now fails loudly instead of publishing
whatever the partial state happened to imply. **A safety valve that silently
returns plausible numbers is more dangerous than no safety valve at all** — with
no valve, the run hangs and you investigate.

**Pinned by.** `tests/sim_invariants.rs::an_impossible_configuration_rejects_rather_than_thrashing`,
which runs a deliberately impossible configuration (20k KV, 0.2 reservation
factor) and requires that it terminate un-truncated, conserve every request,
and stay under 200,000 evictions. Also
`tests/engine_test.rs::admission_reserves_headroom_for_the_existing_batch`,
which pins the headroom arithmetic directly. Every one of the twelve
configurations in `littles_law_holds_across_every_configuration` also asserts
`!truncated`.

---

## 5. The estimator confound

Not a crash, not even a wrong number — a wrong *experiment*, which is worse,
because a wrong experiment ships as a finding.

**What it was.** Section 6 asks how accurate an output-length predictor needs to
be. The obvious design is to vary the predictor's error and watch SJF degrade.
I built that, and got a muddy result: SJF's advantage decayed a little, the
noise was comparable to the effect, and the honest summary would have been
"prediction accuracy matters somewhat".

**What found it.** FIFO's numbers moved too.

FIFO never consults the estimate for ordering. If FIFO is sensitive to
estimator error, the error is reaching the system through some channel other
than scheduling — and it was: the KV **reservation** is also computed from the
estimate. One knob was moving two mechanisms, and the measured effect was their
sum.

**The fix.** Split `estimator_spread` from `reservation_spread` in
`GatewayConfig` so each channel can be varied alone. This is a two-line change
that took an afternoon to justify.

**What it produced.** The report's most useful finding, and it is not subtle
once the channels are separated:

| channel | effect of 8× estimate error |
|---|---|
| scheduling only | SJF advantage +0.094 → +0.085 (**flat**) |
| reservation only | attainment 91.4% → 75.6%, evictions 0 → 137 |

Scheduling needs the *ranking* to be roughly right, and rank statistics are
robust to multiplicative noise: a chat turn and a batch job differ by more than
an order of magnitude, so 8× noise rarely swaps them. A memory reservation needs
the *magnitude*, and being wrong about it commits resources that preemption can
only redistribute, never recover.

The engineering rule is sharper than "improve the predictor": **use predictions
for ordering decisions, which are reversible and forgiving, and avoid using them
for resource commitments, which are neither.**

**Pinned by.** `tests/sched_test.rs::fifo_ignores_the_estimate_entirely`, which
feeds two identical FIFO schedulers wildly different estimates and requires
identical service order. If that test ever fails, section 6 is confounded again.
`sjf_ordering_survives_multiplicative_noise` pins the mechanism on the other
side.

---

## 6. `emit_token` reachable during prefill

The smallest bug here, found last, and included because of how it was found.

**What it was.** `Running::emit_token` increments `kv_held` and `emitted`. It
assumes the sequence has finished prefill and has tokens left to produce.
Nothing enforced either.

**What found it.** Writing `tests/engine_test.rs`. The method was private, so I
made it public to test the KV accounting — and the first test I wrote with it
looped `while r.prefilling() { r.emit_token(); }`, which never terminates,
because `emit_token` does not advance prefill. It ran until `kv_held`
overflowed `u32`: roughly four billion iterations, and a panic message
(`attempt to add with overflow`) pointing at a line that was not the mistake.

**The fix.** Two `debug_assert!`s: not during prefill, not after completion.
The event loop already respected both invariants; nothing stated them.

**Why it is in this list.** The bug was *created* by making the method public
for testing, and then immediately caught by the test that needed it. That is
the trade working as intended — widening an API for testability is a real cost,
and the mitigation is to state the invariants at the boundary rather than to
keep the method private and untested. `emit_token_is_rejected_during_prefill`
and `emit_token_is_rejected_after_completion` now pin both.

---

## What the six have in common

Five of the six produced believable output. Ranked by how long each would have
survived in a document nobody re-derives:

| bug | visible symptom | how long it survives |
|---|---|---|
| eviction livelock | `thr=0.00` in one column | until someone reads that column |
| lockstep clocks | slightly superlinear scaling | indefinitely |
| greedy fill | 1.5× from two replicas | indefinitely, with a plausible story attached |
| KV double-count | low batch, low attainment everywhere | until calibration refuses to converge |
| estimator confound | a muddy but publishable result | **permanently** — it ships as a finding |
| emit during prefill | integer overflow panic | zero seconds |

The one that crashes is the one that costs nothing. The one that would have
survived permanently is the one where the code was correct and the *experiment*
was wrong — and no amount of unit testing finds that, because every component
was doing exactly what it was asked.

What found each one:

- **Little's Law** — an identity that must hold for structural reasons, computed
  two independent ways. Found bug 1 and its second-order variant.
- **An interesting result being checked** — found bug 2. This is not a
  methodology, it is luck, and the lesson is that the mechanism behind a
  surprising number should be verified before the number is written down.
- **Calibration failing to converge** — found bug 3. When no parameter setting
  produces sensible physics, suspect the model, not the parameters.
- **A safety net firing** — found bug 4, but only barely, and the real fix was
  making the net loud instead of quiet.
- **A control that should not have moved** — found bug 5. FIFO's insensitivity
  to the scheduling estimate was a property I could have asserted from the
  start, and did not.
- **Writing the test** — found bug 6.

Only the first and the last are things a test suite does on its own. The other
four came from having an expectation precise enough to be violated, which is
the argument for registering predictions before running experiments rather
than describing results afterwards.
