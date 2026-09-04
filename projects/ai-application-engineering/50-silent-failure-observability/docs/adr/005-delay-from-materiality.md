# ADR 005 — Measure delay from materiality, with a persistence rule

**Status:** accepted

## Context

"Detection delay" needs a zero point. The obvious choice is the day the degradation was
injected. Every scenario ramps: on the onset day the model swap affects a handful of turns
and quality has moved by a fraction of a percent.

## Decision

Delay is measured from `first_materially_degraded_day` — the first day mean answer quality
sits below `MATERIALITY` **and stays there for `PERSISTENCE` consecutive days** — not from
the onset day. Negative delays are recorded as negative and are treated as good.

## Consequences

**Measuring from onset would score honesty as failure.** On the onset day the effect is
indistinguishable from noise; a detector that fired then would be firing on noise, and would
have fired on the healthy scenario too. Onset-based delay awards points for exactly the
behaviour the false-alarm column punishes. Materiality-based delay asks the question the
operator actually has: *how long was the product measurably worse before anyone knew?*

**Negative delays are legitimate.** Three detector/scenario pairs alert *before* the
material day — self-similarity is one day early on `model-swap` and one day early on
`refusal-creep`, and the sliced PSI and self-similarity both hit `template-regression`
exactly on it. A detector watching the output distribution can see a mixture shifting while
the mean of a bounded quality score is still inside its noise band. Clipping those to zero
would erase the single most useful property a drift detector can have.

**The persistence rule is not cosmetic; it changed a ground truth.** `template-regression`
sat almost exactly on the materiality line, and its "first material day" was therefore
decided by which side of 0.95 the daily noise happened to fall on — day 52 without
persistence, day 57 with it. That is a five-day swing in a *ground truth*, driven by noise,
against which detectors were being scored. Two fixes were applied together: the truncated
answers were made clearly bad (quality 0.4 → 0.1) so the effect is not marginal, and
`first_materially_degraded_day` was given the same 3-day persistence requirement that every
detector must satisfy to alert.

The second half matters more than the first. Before it, detectors paid a three-day tax that
the ground truth did not, so every delay in the table was inflated by up to three days
against a benchmark held to a laxer standard. Comparing a persistent statistic to a
non-persistent one is not a comparison. Both numbers are now pinned in
`test_material_day_requires_persistence` — 52 under `persistence=1`, 57 under
`persistence=3` — and the `material-day-without-persistence` mutant reverts it.

That test used to assert `loose <= strict`, which is a tautology: relaxing a conjunction can
only move the first satisfying day earlier. It survived mutation. Pinning the values killed
it. The general lesson is in `docs/portfolio/04-two-mutants-that-survived.md`.

**`-1` means never.** `input-shift` and `healthy` have no material day, so the delay column
shows `--` and any alert on them is counted as a false positive rather than as a very fast
detection. See ADR 006.
