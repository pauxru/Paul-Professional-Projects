# ADR 0002 — The oracle answers deterministically, keyed on (seed, query, model)

**Status:** accepted
**Date:** 2024-06

## Context

The experiment compares routing policies whose accuracy differs by a few points.
Section 3 reports differences as small as +0.3. Those differences have to be
real, not sampling noise.

The naive simulator draws a fresh random number every time a model is called. It
has two problems, and the second one is subtle enough that I built it the naive
way first.

**Problem 1 — noise swamps the signal.** With 4,000 eval queries, the standard
error on an accuracy estimate is about 0.8 points. A 2.5-point difference between
two policies is three standard errors *if the samples are independent*, which is
exactly the regime where a run-to-run reshuffle can reverse a ranking.

**Problem 2 — policies that make identical decisions get different answers.**
This is the fatal one. A cascade at threshold 0.90 and a cascade at threshold
0.91 might route 3,900 of 4,000 queries identically. Under fresh sampling, all
4,000 of those calls return independently drawn outcomes, so the two policies
differ on the 3,900 queries where they *agree*. The measured difference between
them is then almost entirely noise from queries that were routed the same way.

Everything downstream inherits this. The Pareto frontier becomes a ranking of
which threshold got lucky. The comparison in section 4 between the raw and
scaled cascade — the one that produces this project's sharpest result — would
show a nonzero difference purely from resampling, and I would have concluded
that recalibration *does* move the frontier.

## Decision

**`models.Oracle.Ask(q, name)` is a pure function of `(seed, query.ID, model
name)` and the query's latent difficulty.** It seeds a SplitMix64 generator from
a hash of those three values and draws correctness, confidence and latency from
it.

Consequences that follow directly:

- Two policies that make the same call on the same query get the *same answer*.
  All measured differences between policies come from queries they routed
  differently. Noise from shared decisions is exactly zero, not merely small.
- The whole report is reproducible byte for byte from a seed, which is what lets
  `docs/results.md` be a generated artefact rather than a transcript.
- `WouldBeCorrect` is free to call repeatedly — the classifier's training loop
  and the oracle baseline both do — with no risk of consuming randomness that
  changes a later result.

## The cheating question

An oracle that knows ground truth is available to *anything* that holds a
`*models.Oracle`, and routing policies hold one so they can call models. So the
codebase has to draw a line and enforce it.

- `Ask` returns an `Answer` containing `Correct`. A policy that reads
  `Answer.Correct` and acts on it *before* deciding is cheating, and the only
  type that does so is `router.Oracle`, which is documented as an unimplementable
  ceiling.
- `WouldBeCorrect` is documented as ground truth, and its callers are the oracle
  baseline, the classifier's *training* labels (legitimate — training labels are
  historical ground truth, which a production system gets from an eval set), and
  the calibration measurement.
- `Query.Difficulty` is the latent truth and no policy may read it.
  `TestClassifierScoreDependsOnlyOnFeatures` constructs two queries differing
  only in `Difficulty` and asserts the classifier scores them identically. The
  observable feature vector is a separate method, `Query.Features()`, so the
  boundary is a type-level thing rather than a convention.

## Consequences

- The correctness draw is `rng.Float() < model.Accuracy(difficulty)`, so a model
  that is 60% likely to be right is deterministically right on a fixed 60% subset
  of queries *for that seed*. Across seeds this averages out; within a run it is
  a fixed partition. Any conclusion that depends on a single seed is suspect, so
  the report's structural claims are all checked over sweeps (48 threshold pairs,
  16 λ values) rather than at single points.
- Latency jitter is also deterministic, which means the p95 figures are stable
  but do not capture tail risk from real infrastructure. Noted in known
  limitations.

## Alternatives considered

**Common random numbers via a pre-drawn matrix** (query × model). Equivalent, and
what this effectively is, but a hash is stateless and does not need the matrix to
be threaded through every call site or sized in advance.

**Averaging many independent runs.** Correct but expensive, and it does not fix
problem 2 — it only shrinks it. With enough runs the resampling noise on shared
decisions still costs precision that determinism gives for free.
