# ADR 001 — Simulate the traffic instead of using a public corpus

**Status:** accepted

## Context

The project needs ninety days of production AI traffic in which quality degrades in a
known way at a known time, and in which every request nonetheless succeeds. No public
dataset has that shape. Real support-assistant logs are not published, and the ones that
are do not come with per-request ground-truth quality or a labelled degradation onset.

Three options were available:

1. A public QA dataset (e.g. a retrieval benchmark), degraded by post-processing.
2. A small hand-written corpus, replayed with noise.
3. A template-driven generator with explicit scenario definitions.

## Decision

Option 3. `src/corpus.py` defines question and answer templates per topic; `src/stream.py`
defines six scenarios, each specifying an onset day, a fourteen-day linear ramp, and a
transformation applied to the *text* of a share of responses.

## Consequences

The cost is stated plainly and repeated in `docs/known-limitations.md`: **the absolute
detection delays in this project are a function of effect sizes I chose.** If the model
swap had degraded 90% of traffic instead of 60%, every detector would be faster. No number
in `docs/results.md` should be read as "PSI detects a model swap in fourteen days."

What survives is the *relative* comparison, and it survives because every detector sees the
identical stream. When sliced PSI catches a regression that aggregate PSI misses, that is
not a property of my chosen effect size — it is a property of dilution, and it would hold
at any effect size small enough to matter. The findings in the README are all of that form:
which detector, relative to which other, and why.

The benefit is that ground truth exists at all. `Turn.quality` and `Turn.is_degraded` are
known per request, which makes it possible to define "the day the regression became
material" precisely, to measure delay from it, and — critically — to have a scenario in
which nothing is wrong and to know that with certainty. With a real corpus and human
labels, `input-shift` could not exist, and `input-shift` is where three of the eight
detectors fail.

A test (`test_detectors_never_read_ground_truth`) reads the source of `detectors.py` and
asserts that no detector references `is_degraded` or `.quality`. The convention that
ground truth is scoring-only is enforced rather than documented, because it is an easy
thing to break while debugging and a fatal thing to have broken.
