# ADR 0004: Report counted operations, not wall-clock time

## Status

Accepted.

## Context

Section 9 draws a Pareto frontier of quality against cost, and section 10's
selection guide tells a reader which configuration to pick under a cost budget.
Both need a cost number.

The obvious one is elapsed time. It is also the one that would make this
repository's headline claims unreproducible.

## Decision

Every index and reranker increments an operation counter as it works, exposed
as `last_ops`. Cost in the report is that count. No timing appears anywhere in
`docs/results.md`.

The counters are:

| stage | counted |
|---|---|
| BM25 / TF-IDF | posting-list entries touched |
| LSA | dense dot products over the truncated basis |
| RRF / blend | candidate lists merged |
| cross-encoder-style reranker | (candidates scored) x (feature evaluations) |

## Rationale

**Reproducibility.** A wall-clock number is a property of one machine on one
afternoon: CPU model, thermal state, whatever else was running, Python's
allocator, and — decisively — whether the process was the first to touch a
given array. A reader who re-runs this repository would get different numbers
and no way to tell whether the difference is their laptop or a real change.
Operation counts are a function of the algorithm and the data, so
`test.ps1` can generate the report twice and compare hashes. That check would
be impossible with timings in the file.

**Honesty about what is being compared.** These are teaching implementations in
pure Python plus NumPy. Timing them would compare implementation quality — how
many attribute lookups are in a loop — not retrieval strategy. A production
BM25 in C over a compressed index is orders of magnitude faster than this one
and has *the same* posting-list-entry count. The counted quantity is the part
that survives being ported to a serious engine; the timing is the part that
does not.

**It makes the reranker's cost model legible.** The reranker's cost is
candidates x features, and the report can therefore state exactly what a
reader pays to widen the candidate pool from 20 to 50 without anyone having to
believe a benchmark. Section 7's conclusion — that a reranker cannot recover an
answer the first-stage pool never contained, so pool width is the variable that
matters and reranker quality is second-order — is a statement about that
budget.

## Consequences

- Costs across *different* stage types are not commensurable. A posting-list
  entry and a dense dot product are not the same amount of work. The report
  never adds them; the Pareto frontier is drawn within a stage type, and where
  it is not, section 9 says so explicitly.
- The frontier is a frontier in "algorithmic work", so a reader who cares about
  p99 latency has to do their own translation. The README states this as a
  limitation rather than pretending the frontier is a latency curve.
- Adding an index means adding its counter, and forgetting to means the new
  index appears free. `test_indexes.py` pins a non-zero, monotone-in-input-size
  count for every registered index so a missing counter fails the suite rather
  than silently flattering a strategy.

## Alternatives considered

**Wall-clock with many repetitions and a median.** Rejected: still
machine-specific, still breaks the hash check, and the repetition count needed
to stabilise a Python microbenchmark is larger than the whole experiment
budget.

**Both, side by side.** Tempting, and rejected on the grounds that a reader
shown a timing column will use it, and the timing column is the untrustworthy
one. `demo.ps1` prints elapsed time for the *run* so nobody is misled about how
long the lab takes; that number never enters a comparison.

**Model an idealised cost analytically instead of counting.** Rejected: an
analytic model can be wrong without failing, whereas a counter that is wrong
about what the code does tends to disagree with the tests that pin it.
