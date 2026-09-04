# Nobody knows the right batch size, and that is the point

Every backfill has the same paragraph in its runbook. *Update in batches of N,
with a pause between batches, and watch replica lag.* The runbook never says
how to pick N, because nobody knows how to pick N.

This is not sloppiness. N depends on row width, index count, the replica's disk,
what else is running, and the time of day. It is a property of the system at the
moment the backfill runs, and the only person who could know it is not in the
room when the runbook is written.

## Both guesses are wrong

The tool models a 2,000,000-row backfill against a replica that applies about
5,000 rows per second, with a 5-second lag budget. Two fixed batch sizes:

| controller | rows/sec | lag breaches | peak lag |
| --- | --- | --- | --- |
| fixed 500 | 500 | 0 | 0.5 s |
| fixed 20000 | 20,000 | 99 | 300 s |

The cautious guess never breaches and takes **ten times longer than necessary**
— an hour and a half of an engineer's evening for something the system could
absorb in nine minutes. The aggressive guess breaches 99 times and drives lag to
five minutes, which on most fleets means the read replicas are serving stale
data and somebody's dashboard is paging.

The interesting thing is not that one is too slow and one too fast. It is that
**the operator has no way to find the middle** without running the backfill,
which is the thing they were trying to avoid doing blind.

## Give the loop a feedback signal

AIMD — additive increase, multiplicative decrease — is the congestion-control
algorithm TCP uses. Increase the batch size by a constant while lag is under
budget; multiply it by a factor below one the moment lag exceeds budget.

| controller | rows/sec | lag breaches | peak lag | batch size cv |
| --- | --- | --- | --- | --- |
| fixed 500 | 500 | 0 | 0.50 s | 0 |
| fixed 20000 | 20,000 | 99 | 300 s | 0 |
| **aimd** | **4,950** | **27** | **5.69 s** | **0.207** |

Nobody told it the replica applies 5,000 rows per second. It found 4,950 — 99%
of the true capacity — by pushing until it hurt and backing off. Peak lag 5.69
seconds against a 5-second budget: it overshoots, briefly, by design. That is
what the feedback costs, and it is the correct trade against a fixed guess that
is either 10× slow or 60× over budget.

## Which half does the work?

Here the prediction was wrong, and the failure produced the better finding.

The prediction was that a proportional controller — scale batch size by how much
lag headroom remains — would be *smoother* than AIMD's sawtooth, and riskier for
it. AIMD's coefficient of variation on batch size was expected to be the high
one.

Measured:

| controller | rows/sec | breaches | peak lag | cv |
| --- | --- | --- | --- | --- |
| aimd (additive up, multiplicative down) | 4,950 | 27 | 5.69 s | **0.207** |
| aiad (additive up, additive down) | 4,988 | 189 | 8.95 s | 0.384 |
| proportional (scale by headroom) | 5,013 | 187 | 9.13 s | **0.731** |

The proportional controller is the *noisiest* — three and a half times AIMD's
variation — and it breaches seven times as often.

The mechanism, once you look for it: multiplying batch size by a lag-error term
means the error drives the *derivative* of the size rather than the size itself.
That is integral action on a plant with transport delay, and integral action on
a delayed plant is a limit cycle. The controller pushes, the lag responds
several seconds later, the controller has already overcorrected, and it
oscillates around the operating point instead of settling on it.

The AIAD row is the ablation that isolates the cause. Additive increase with
*additive* decrease breaches 189 times against AIMD's 27, at effectively
identical throughput. So it is not the additive increase that makes AIMD work —
both have that. **It is the multiplicative decrease.** Backing off by a factor
rather than a constant means the controller's retreat is proportional to how far
it has climbed, which is what damps the oscillation.

## The number that decides the argument

Look at the throughput column again:

- aimd: 4,950 rows/sec
- aiad: 4,988 rows/sec
- proportional: 5,013 rows/sec

The spread across all three adaptive controllers is **under 1.3%**. The spread
in lag breaches is **7×**.

This is the whole case for the asymmetric response in one comparison. The three
controllers cost the same in wall-clock time. They differ enormously in how
often they hurt the replica. **The asymmetry is nearly free** — you are not
trading throughput for safety, you are getting safety for a rounding error.

That is an unusual shape for an engineering trade-off, and it is worth
recognising when it appears, because the instinct built by every other
performance decision is to assume a cost exists and go looking for it.

## When the ground moves

A backfill does not run in a static environment. §12 introduces a concurrent
`VACUUM` partway through, which cuts the replica's effective apply rate for
several minutes and then restores it.

| controller | breaches during | breaches in the 400 s after | peak lag | rows/sec |
| --- | --- | --- | --- | --- |
| aimd | 34 | 8 | 6.48 s | 3,810 |
| fixed 5000 | 197 | 0 | 300.5 s | 5,000 |

The fixed controller was tuned to exactly the right value for the undisturbed
system — 5,000 rows/sec, the true capacity — and it is the one that fails. It
cannot know the capacity changed. It breaches 197 times and drives lag to five
minutes, because it keeps issuing the batch size that was correct ten minutes
ago.

AIMD gives up 24% of its throughput and keeps peak lag at 6.5 seconds.

The 8 breaches in the recovery window are AIMD's genuine weakness, stated
plainly: after backing off it climbs additively, so it re-discovers the restored
capacity slowly and overshoots a little on the way. A controller with a memory
of the pre-disturbance operating point would do better. That is a real
improvement and it is not implemented.

## What this is really an argument about

The runbook says "batches of N". Every discussion about backfills is a
discussion about what N should be, and it is unanswerable, because N is not a
constant — it is a *function of conditions at runtime*, and it changed while you
were arguing.

The measurements say: stop trying to answer it. Pick a control law instead. The
tuning that matters is not the batch size, it is the shape of the response —
and the shape that works is asymmetric, because the cost of being too fast and
the cost of being too slow are not symmetric either.

Being too slow costs an engineer's evening. Being too fast costs the read
replicas. A controller that treats those the same is answering a question
nobody asked.
