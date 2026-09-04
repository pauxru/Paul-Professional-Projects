# Known limitations

Written to be read by someone deciding whether to trust the numbers.

## The encoder is TF-IDF, not a transformer

There is no embedding API available in this environment, so `internal/embed`
fits TF-IDF over unigrams and bigrams, hashes into a fixed dimension and
L2-normalises. A sentence transformer would score paraphrases higher and
confusables lower, and every absolute number here would move.

**What survives the change and what does not:**

| result | survives a better encoder? |
|--------|----------------------------|
| §2's mass identity: cosine drop = differing feature mass | **Yes.** It is algebra about L2-normalised pooled vectors. It holds for mean-pooled transformer embeddings too — that is what pooling *is*. |
| §2's conclusion that the populations interleave | **Weakened, not removed.** A better encoder narrows the overlap. It cannot remove it, because the mechanism is dilution: one token's contribution to a mean shrinks as the document grows. |
| §3: the headline false-hit rate measures traffic mix | **Yes.** It is a statement about the metric, not the model. |
| §4: an IDF cut has no tunable range on a small corpus | **Yes**, and it is about the corpus, not the encoder. |
| §4b: the veto's cost is hidden by verbatim repeats | **Yes.** It follows from the hit mix, which is a property of traffic. |
| §5: coalescing errors are invisible to cache precision | **Yes.** Structural: those requests were cache misses. |
| **every absolute rate** (90.6% hit rate, 3.31% false-hit, 81.4% rejected) | **No.** Treat these as illustrating a shape, never as a benchmark. |

## The corpus is 170 queries

Hand-written and hand-labelled, which is the only way to get trustworthy labels
for confusable pairs, and far too small for the IDF distribution to be
realistic. §4's finding that "every IDF percentile returns the same cut" is
partly a corpus-size artefact — and is presented as one. It is also exactly what
happens to teams who compute IDF over their own document set instead of a
background corpus, which is why it is worth reporting rather than engineering
around.

Adversarial queries are heavily over-represented relative to real support
traffic. This is deliberate — the whole point is to measure the hard case — but
it means absolute error rates here are far above what a production system would
see, and §3 is the section that explains why that comparison is meaningless
anyway.

## The lexicon is hand-authored

`internal/lexicon` maps synonyms to canonical forms. It is a hand-built
component that directly affects the similarity function, which makes it the
single largest threat to the study's integrity: an author who quietly encodes
which tokens are decisive would produce a circular result.

Two tests constrain it. `TestNoLexiconEntryTouchesAnIrreducibleToken` forbids any
entry, on either side, mentioning an entity, plan name, HTTP status code or
tenant. `TestFamilySiblingsNeverCollapseToTheSameConcept` forbids collapsing two
members of a confusable family. Neither test can prove the absence of subtler
bias. The table is small and readable; read it.

## `go test -race` cannot be run here

The race detector requires cgo and a C toolchain. This machine has no gcc,
clang, mingw or msys, so `-race` is unavailable — not skipped by choice.

`internal/flight` is the only genuinely concurrent code in the repository. It is
compensated for, not covered:

- `test.ps1` runs `./internal/flight` at `-count=50` with `GOMAXPROCS=8`, which
  exercises the interleavings repeatedly but proves nothing about happens-before;
- the tests assert observable invariants that a data race would eventually
  violate — exactly one leader, every follower receiving the identical byte
  sequence, a late joiner receiving the full prefix, the channel closed exactly
  once;
- everything else in the repository is single-threaded by construction, because
  the gateway is a discrete-event simulation.

**Treat `internal/flight` as unverified for data races.** Run `go test -race
./internal/flight/` on a machine with a C toolchain before believing it.

## The gateway is a simulation, not a server

There is no HTTP, no TLS, no connection pool, no retry budget, no backpressure
and no memory limit. Backend latency is a deterministic function of the query
text between fixed bounds; a real endpoint has a heavy tail that would change
coalescing's value substantially — longer calls mean wider windows and more
joiners.

Cache lookup is a brute-force linear scan over entries. At the scale here
(≤ 300 entries) that is the right choice and keeps the admission logic legible.
A production cache needs an ANN index, and **an approximate index changes the
results**: it returns an approximate nearest neighbour, so the guard would be
adjudicating a candidate that is not necessarily the best one. Nothing here
measures that interaction.

There is no eviction policy. Entries live forever. Real caches evict, and
eviction interacts with the poisoning result in §5 in a way this does not model.

## Specific measurement caveats

**`Precision()` over zero hits returns 1.** Vacuously correct and operationally
a trap: a configuration that never hits displays as flawless. It is pinned by a
test and left alone rather than silently changed, because the alternative
(returning 0) misreports a genuinely error-free run. Read the hit count
alongside it. This is the report's own thesis applied to its own code.

**`Stats.GuardVetoes` counts only outcome-changing vetoes.** If a correct entry
is also above threshold it simply wins and no veto is recorded. The number is
"vetoes that sent a request to the backend", not "candidates rejected". That is
the operationally meaningful quantity, but it is not what the name suggests.

**§4's sweep is not monotone in K at fixed Jaccard.** Larger K produces larger
decisive sets and therefore larger unions, so a fixed Jaccard ratio means
something different at each K. At K=1 the parameter is entirely inert. The
parameter with a stable meaning is *how many of the K tokens must agree*. The
sweep is reported as measured, with the interaction called out, rather than
reparameterised to look tidy.

**Coalescing changes which entries get cached**, so `CacheFalse` moves between
rows in §5 for second-order reasons — a coalesced request is a cache miss that
never writes an entry, so downstream cache behaviour differs. The comparison
across coalescing modes is therefore not a clean single-variable sweep, and the
narrative says so where it matters.

**The dense projection path exists and is unused.** `embed.Dense` applies a
random projection to the sparse vector. It was probed early, made no qualitative
difference to any finding, and the final report uses the lexical path only. It
is retained because the tests cover both and its existence is what justifies the
claim that the findings are not an artefact of dimensionality.

## What this is not

It is not a benchmark of any vendor's semantic cache. It is not a
recommendation to use TF-IDF. It is not evidence that semantic caching is a bad
idea — §3's top-K veto reaches 100% precision while still serving 89.6% of
requests from cache, which is a good outcome.

It is an argument that the metrics conventionally used to justify these systems
are dominated by benign traffic, and that the fix is to measure against fixed,
named populations you control.
