# ADR 0001: Simulate the lock queue instead of requiring a database

## Status

Accepted.

## Context

The central claim of this project is quantitative: a short DDL statement behind
a long-running read costs orders of magnitude more than its own duration. To
support that claim, something has to produce numbers.

The obvious approach is to run against a real PostgreSQL: start a container,
create a table, load rows, open a long transaction, fire the DDL, measure. This
is what most blog posts on the subject do, and it has one enormous advantage —
the numbers come from the actual system rather than from a model of it.

It also has four problems, and together they are disqualifying for this
project.

**It is not reproducible.** Two runs of the same experiment on the same machine
produce different numbers, because the numbers depend on page cache state, on
autovacuum timing, on whatever else the machine is doing. A report whose
figures change every run cannot be committed, cannot be diffed, and cannot be
verified by a reader.

**It is not portable.** Requiring Docker and a working PostgreSQL to read the
results means most readers will not run it and will have to take the figures on
trust. That is precisely the posture this project argues against.

**It is slow in the wrong way.** Measuring a 45-second read takes 45 seconds.
Sweeping seven timeout values across five workloads takes half an hour of
mostly waiting. This is not a cost worth paying for numbers that are, in the
end, illustrative.

**It measures the wrong thing.** The interesting quantity is *blocked
query-seconds* — the total time queries spent waiting that they would not have
spent otherwise. On a real server that requires either instrumenting every
client or reconstructing it from `pg_stat_activity` samples, and both give you
an estimate with error bars wider than several of the effects being measured.

## Decision

Build a deterministic discrete-event simulator of PostgreSQL's table lock
queue, and derive every published figure from it.

The simulator models exactly three things:

1. **The conflict matrix**, transcribed from the PostgreSQL documentation, not
   derived from any ordering of lock strength.
2. **Queue ordering**, which is the load-bearing part: a request that cannot be
   granted blocks every later request, whether or not the later request
   conflicts with anything currently held.
3. **Arrival and service times**, drawn from a seeded PRNG.

It does not model buffer pools, WAL, planner behaviour, index build cost, or
anything else about what a statement *does*. Those affect how long a statement
holds its lock, and this project takes hold time as an input rather than
predicting it.

## Consequences

**The numbers are reproducible to the byte.** `docs/results.md` has a sha256
recorded in CI, and `TestResultsAreReproducible` regenerates the whole document
and compares it. If a figure changes, either the model changed or somebody
edited the document by hand, and both are worth failing a build over.

**The numbers are relative, not absolute.** The simulator cannot tell you that
your `ALTER TABLE` will block for 32,896 query-seconds. It tells you that the
same DDL costs 83× more when a long read is present than when it is not, and
that ratio is robust to the arrival-process assumption because the assumption
appears in both halves. Every headline figure in the report is a ratio between
paired runs for exactly this reason, and `docs/known-limitations.md` says so
explicitly.

**The arrival process is an assumption.** Poisson arrivals with exponential
service times is the standard queueing assumption and is wrong in detail for
real traffic, which is bursty and correlated. It cancels in ratios. It does not
cancel in absolute figures, which is why absolute figures are always presented
alongside the paired comparison rather than alone.

**Nobody can check the model against reality from inside this repository.**
That is a real limitation and the honest mitigation is that the model is small
enough to read: `sim/queue.go` is 180 lines, and the conflict matrix it depends
on is a direct transcription with a symmetry test over all 64 pairs.

## Alternatives considered

**Testcontainers with a real PostgreSQL, results committed from one blessed
run.** Rejected: a committed artefact that cannot be regenerated is worse than
one that can, because the failure mode is silent staleness.

**A closed-form queueing model (M/M/1 with priority).** Rejected: the effect
being demonstrated is specifically that non-conflicting requests block behind a
conflicting one, which is a property of the queue discipline rather than of the
service distribution. Getting that into a closed form obscures the one thing
worth showing.

**Both — simulate for the report, integration-test against a container.**
Deferred rather than rejected. It is the right answer for a tool that shipped;
it is disproportionate for one that is demonstrating a technique. Recorded in
`docs/known-limitations.md` as the first thing to add.
