# ADR-004: HdrHistogram-style buckets for high volume, exact percentiles for small runs

* **Status:** Accepted
* **Date:** 2026-09-02

## Context

Latency percentiles at p99 and p99.9 need lots of samples. A naive implementation retains
every sample in a list and quickselects — O(N) memory and O(N) per query. For a 10-minute
soak run at 500 rps that's 300 000 samples per endpoint per run, which is not enormous, but
scales badly and doesn't compose cleanly across streams.

HdrHistogram (Gil Tene's algorithm) uses a bounded-precision logarithmic bucket layout:
`bucket_i` covers a range that is roughly 2× as wide as `bucket_{i-1}`, and within each
bucket a linear array of `subBucketCount` slots gives fixed relative precision. Memory is
O(bucket count), record cost is a couple of shifts, and the relative precision bound
(default ~2 %) is guaranteed at every scale.

## Options considered

### Full-sample percentiles only
* Pros: exact. Deterministic, easy to test.
* Cons: doesn't scale, and inter-run merging requires re-sorting the union.

### HdrHistogram only
* Pros: bounded memory, bounded error, composes across streams by adding count arrays.
* Cons: results have a known relative error (up to ~4 % for our 3-decimal significant-figure
  configuration). For very small runs, that error is a real fraction of the reported number.

### Both (chosen)
* Pros: retain a full sample list for small runs (< a configurable threshold — currently
  1 000 samples per stream) and switch to the histogram beyond that. Reports show which was
  used so the reader can reason about precision.
* Cons: two code paths to maintain and test.

## Decision

Ship both. `LatencyHistogram` implements the HdrHistogram-style layout. `ExactPercentiles`
computes exact percentiles from a sorted sample list (Type 7 percentile). `MetricsCollector`
always feeds both, and the aggregate report uses `ExactPercentiles` when `count <= 1000`,
`LatencyHistogram` otherwise.

## Consequences

* The histogram's precision bound is unit-tested: `HistogramPrecisionBoundTests` fills the
  histogram with 100 000 random values and asserts that the reported p50 / p95 / p99 fall
  within the guaranteed relative error of the exact answer.
* Compare two histograms by summing count arrays — used by the stress runner to fold per-step
  metrics into the run-total metrics.
* Reports state the source ("exact 800 samples" vs "histogram 3 289 samples, bound ±4 %").

## Risks

* If a stream has very few samples but the run has many total, the reader could infer more
  precision than deserved. **Mitigation:** each endpoint's per-endpoint block in the report
  shows sample count and source.

## Alternatives

* T-digest: another well-known algorithm with excellent tail accuracy. Not chosen because
  the reference implementations are more complex and the correctness story is less obvious to
  a reviewer; HdrHistogram's simplicity is a portfolio-visible virtue.
* KLL / DDSketch: similar tradeoff; not worth the added complexity for this project's scale.
