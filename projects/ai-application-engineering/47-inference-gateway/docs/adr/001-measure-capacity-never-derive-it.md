# ADR 001: Measure saturation throughput, never derive it

**Status.** Accepted.

## Context

Every experiment in this report is expressed as a utilisation ρ = λ/μ. That
requires a value for μ, the rate at which the gateway can complete requests when
it is never starved for work.

The obvious way to get μ is arithmetic: take peak decode throughput at maximum
batch, divide by the mean output length, and you have requests per second. This
is how most capacity plans are written, and `capacity::naive_rps` implements
exactly that calculation so the report can quantify the gap.

## Decision

`capacity::saturation_rps` measures μ empirically. It hands the gateway a trace
with all arrivals at time zero — an infinite backlog — runs it, and measures the
completion rate over the steady-state middle of the run, trimming 20% at each
end to exclude the fill and drain transients.

## Consequences

**The naive estimate overstates capacity by 27%.** It assumes the accelerator
spends every microsecond decoding at full batch. It does not: prefill is compute
bound and blocks decode, and the batch is limited by KV memory rather than by
configuration.

A team sizing a fleet from the naive figure would provision for 5.77 requests
per second per replica and discover the real number is 4.54 — which means
running at a true utilisation of 1.00 while believing they were at 0.79.
Section 4 shows what the difference between those two numbers does to latency:
attainment at 0.79 is roughly 98.7%; at 1.00 it is below 50%.

**Every experiment inherits the denominator.** If μ were 27% too high, ρ=0.85
would actually be ρ=1.08, and the section that studies "a system under moderate
load" would be studying an oversubscribed one. The trimming exists for the same
reason: including the fill transient depresses μ, and depressing μ inflates
every ρ.

**Cost.** Measuring μ requires a full simulation run per configuration, and the
report measures it separately for 1, 2 and 4 replicas and for every engine
config it varies. That is the dominant term in report runtime. It is worth it:
the alternative is a number that is wrong by a quarter with no visible symptom.

**Pinned by.** `stats_test.rs::measured_capacity_is_below_the_naive_estimate`
requires the gap to exist and to be under 2× — a larger gap would mean the naive
estimate is not the estimate anyone would actually make, and the comparison
would be a straw man.
`saturation_throughput_is_stable_across_sample_sizes` requires μ measured at
n=300 and n=900 to agree within 15%, because a drifting denominator would move
every experiment.
