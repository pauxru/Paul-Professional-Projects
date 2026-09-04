# 04 — Measurement, including the results that did not go my way

Every number in this repository is produced by `demo.ps1`. Nothing is hand-typed
and nothing is rounded in my favour. This document is about the ones that were
uncomfortable.

## The optimisation that lost

Trie-pruned mask computation is the obvious optimisation: share work across
common token prefixes, prune the subtree when the automaton dies. The expected
result was a uniform speedup over brute force.

Measured, on a 30,242-token vocabulary with 99,141 trie nodes:

| schema | allowed tokens | speedup vs brute force |
|---|---|---|
| enum-3 | few | **17.8×** |
| flat-object | few | **10.8×** |
| invoice-nested | few | **8.5×** |
| person | ~1,700 | **0.7×** |
| classification | ~1,855 | **0.7×** |

On two of five schemas the "optimisation" is a 30% slowdown.

The explanation is straightforward once you see it. Pruning only helps when
subtrees die. When ~1,800 tokens are allowed, almost nothing prunes, and what
remains is a pointer-chasing traversal of a 99,141-node structure competing
against a tight linear scan over contiguous token bytes. The brute-force loop
wins on locality.

This could have been left out. The three wins are large and the schemas are all
"real". Reporting it is the difference between a benchmark and an advertisement,
and the honest version is more useful: it tells a reader that the right choice
depends on mask density, and it is why ADR 0004 records adaptive selection as
considered-and-rejected rather than pretending the question does not exist.

## The optimisation that won, which I did not plan

The mask deduplication measurement was added late, mostly out of curiosity about
cache memory.

| schema | DFA states | distinct masks | ratio | naive | deduped | precompute |
|---|---|---|---|---|---|---|
| enum-3 | 14 | 13 | 1.08× | 51.7 KB | 48.1 KB | 0.0 ms |
| flat-object | 49 | 30 | 1.63× | 181.1 KB | 111.1 KB | 0.2 ms |
| person | 1,481 | 138 | **10.73×** | 5,472.8 KB | 515.7 KB | 146.8 ms |
| invoice-nested | 795 | 65 | **12.23×** | 2,937.8 KB | 243.3 KB | 5.2 ms |
| classification | 2,201 | 152 | **14.48×** | 8,133.4 KB | 570.3 KB | 226.7 ms |

Many distinct DFA states permit exactly the same set of tokens — unsurprising in
hindsight, since states differing only in "which key comes next" often allow the
same token prefixes.

This changes a design conclusion. Eager precomputation of every mask looks
obviously wasteful at 5.5 MB and stops looking wasteful at 516 KB. If you need a
hard per-step latency bound rather than an amortised one, you can now buy it for
147 ms of startup.

The lazy cache is still good — 94.98% to 98.85% hit rates during decoding — but
"good enough amortised" and "bounded worst case" are different guarantees, and
the measurement is what makes the second one affordable.

## The cost comparison that refused to be a slam dunk

The obvious framing is "constrained decoding is cheaper than retrying". Trying to
measure it honestly made the claim conditional.

| single-shot validity | mean attempts | retry tokens | constrained tokens | ratio |
|---|---|---|---|---|
| 0.0% | 50.00 | 337.6 | 5.8 | retry never terminates |
| 0.5% | 38.05 | 430.0 | 5.4 | 111/200 gave up |
| 5.0% | 18.14 | 254.7 | 5.4 | 11/200 gave up |
| 9.5% | 9.88 | 160.7 | 5.4 | 3/200 gave up |
| 13.5% | 5.82 | 101.5 | 5.5 | **18.5×** |

Two things are worth saying about this table.

First, a single ratio would have been meaningless. The answer depends entirely on
where you sit on the validity curve, which is a property of the model and the
prompt — not of the schema and not of this library. So the deliverable is a
curve and a decision rule, not a headline number.

Second, at low validity rates the ratio is not large, it is *undefined*: retry
does not terminate within any budget. The rows where 111 of 200 runs exhausted 50
attempts are more informative than the rows with a clean multiplier, because
that is the failure mode teams actually hit in production — not "slow", but
"sometimes never returns".

The upper end of the curve is limited by how well a bigram model can be aligned
to a schema. 13.5% single-shot validity is the best the aligned model achieved,
and a real prompted LLM would sit far higher, where the ratio is smaller. That
limitation is stated rather than hidden by extrapolating the curve.

## What compilation actually costs

| schema | NFA | DFA | minimised | reduction | time |
|---|---|---|---|---|---|
| person | 28,746 | 6,914 | 1,481 | 78.6% | 1,411 ms |
| classification | 32,462 | 10,374 | 2,201 | 78.8% | 1,908 ms |

Minimisation removes about 78% of the states, which is the difference between a
mask cache that is a nuisance and one that is a non-issue.

The 1.4–1.9 s compile time is the real operational finding, and it is a
limitation, not a feature: a server that compiles a schema per request would
spend more time on the automaton than on generation. `docs/known-limitations.md`
records that compiled automata cannot be serialised, which is the obvious fix and
is not implemented.

## Method

- Every measurement is a mean over repeated runs from a fixed seed.
- Both test suites are run twice and compared; results are identical.
- The randomised cross-checks use fixed seeds, so a failure is reproducible.
- The distribution results are not measurements at all — they are closed-form
  computations over an exhaustively enumerated valid set, which is why they carry
  no error bars.
