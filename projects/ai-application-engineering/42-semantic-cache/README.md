# Semantic cache and coalescing gateway

A measurement study of the two mechanisms that sit in front of every LLM
endpoint, built as a deterministic simulator so the numbers can be argued with.

```
go run ./cmd/semcache -out docs/results.md   # regenerates the report, byte for byte
./test.ps1                                    # 132 tests
```

Everything here is stdlib Go. There is no network call, no model provider, no
vector database and no `-race` flag (see [known limitations](docs/known-limitations.md)).

---

## The question

A semantic cache answers a new question with a previous answer when the two are
"similar enough". A request coalescer merges concurrent duplicate requests into
one backend call. Both are standard, both are usually deployed on the strength
of a hit-rate number, and both are trivially capable of returning the wrong
answer to a paying customer.

The interesting question is not "does it work" but **what does the hit rate not
tell you**, and the honest answer turned out to be: almost everything that
matters.

## What the report establishes

The generated [`docs/results.md`](docs/results.md) is the artefact. Five findings,
each stated as a falsifiable prediction *before* the number is printed.

**1. A cosine threshold cannot separate paraphrases from confusables, and this
is a theorem, not a tuning failure.** For L2-normalised pooled vectors, the
similarity between two texts is exactly the shared feature mass, so swapping one
decisive word costs only that word's share. The report verifies this identity on
35 designed pairs with a mean absolute error of 0.000. The consequence: a
threshold high enough to reject the top decile of confusable pairs also discards
**79.1% of genuine paraphrases**. The populations interleave. No cut of this
score works, because a single token cannot dominate a mean it is one term of.

**2. The headline false-hit rate measures your traffic, not your cache.** The
identical configuration reports **0.00%** on a benign stream and **3.31%** on a
trap-heavy one. The cache did not change. Reporting that number as a quality
metric means your dashboard improves when customers ask easier questions. The
replacement is a **fixed adversarial probe**: warm one entry per intent, replay
every other phrasing, count the confusions. It reads 2/100 on *both* streams and
moves monotonically with the threshold — a property of the cache, which is what
you wanted to measure.

**3. The IDF cut cannot work here, and the reason is worth more than the fix.**
56.5% of the vocabulary occurs once and 74.3% within two documents, so every IDF
percentile from the 10th to the 90th returns the same value. There is no
threshold to tune. Replacing it with a **top-K budget** — take the K
highest-IDF tokens however common they are, and veto a hit when they disagree —
takes confusable error from 3.7% to 0.0% and precision from 96.7% to 100.0%
while serving **82 more correct answers from cache**. The transferable idea:
when one feature must be able to overrule a whole document, it cannot live
inside a mean. It has to be a veto.

**4. The veto's advertised price is also a traffic artefact — the same mistake,
committed against the fix.** The sweep prices it at one point of hit rate, which
reads as free. Measured on the population it actually adjudicates — pairs of
different wordings of the same question — it rejects **81.4%** of them. Both
numbers are correct: 81.8% of cache hits in this workload are verbatim repeats,
whose decisive sets are identical by construction and which the veto passes for
free. The guard is nearly invisible in aggregate *precisely because it only
touches the minority of traffic that the word "semantic" refers to*. This
section exists because a unit test contradicted the report.

**5. Coalescing is a cache with no metrics.** Sweeping only the coalescing
threshold, the coalescer's precision falls from 100% to 94.9% while
**cache-reported precision stays flat at 95.5%** — every one of those requests
was a cache *miss*, so the cache's own counters are blind to them by
construction. Turning on `CacheCoalescedResults` converts transient mistakes
into permanent poisoned entries.

Plus: tenant scoping costs 2.8% hit rate and 104 extra entries, and eliminates
96 cross-tenant leaks that no encoder could have prevented — the corpus contains
three intents whose wording is byte-identical across tenants with three
different correct answers.

## Why a simulator

Because the alternative is unfalsifiable. A study of this against a real
provider would be a study of that provider's embedding on that week's traffic,
reproducible by nobody. Here the corpus is hand-written and hand-labelled, the
embedding is fitted with a fixed seed, the arrival stream comes from a
splitmix64 generator, and the "backend" is an oracle that always answers the
question asked — so **every wrong answer in the report is attributable to the
cache or the coalescer and nothing else**. Re-running reproduces the file byte
for byte.

The cost is external validity, and it is stated plainly in
[known limitations](docs/known-limitations.md). The structural results
(section 1's mass identity, section 2's measurement critique, section 5's
blind spot) do not depend on the encoder. The magnitudes do.

## The integrity problem, and how it is enforced

Every number here would be worthless if the cache could see the answer key. The
corpus carries ground-truth intents; the gateway carries them on requests. They
exist **only to score outcomes**. Three tests enforce this, and they are the
most important tests in the repository:

| test | property |
|------|----------|
| `gateway.TestDecisionsIgnoreGroundTruth` | scramble every request's intent across a 1500-request workload — the decision sequence must be byte-identical |
| `cache.TestLookupDecisionsIgnoreStoredIntents` | scramble every stored entry's intent — every hit/miss decision must be unchanged |
| `lexicon.TestNoLexiconEntryTouchesAnIrreducibleToken` | the hand-written synonym table may supply synonymy, but may not mention an entity, plan name, status code or tenant on either side |

The third is subtler than it looks and is discussed in
[ADR 0001](docs/adr/0001-hand-written-corpus.md).

## Layout

| package | role |
|---------|------|
| `internal/corpus` | 170 hand-written, hand-labelled queries; 53 intents; 19 confusable families; 3 tenants. Ground truth. |
| `internal/lexicon` | hand-authored synonymy. Deliberately contains no information about which tokens decide an answer. |
| `internal/embed` | TF-IDF over unigrams and bigrams, hashed, L2-normalised, with an optional dense projection. |
| `internal/cache` | the subject: exact and semantic admission, tenant scoping, the top-K veto. |
| `internal/flight` | a real concurrent streaming single-flight group — the only genuinely concurrent code here. |
| `internal/gateway` | deterministic discrete-event simulation of cache + coalescer + backend. |
| `internal/workload` | splitmix64 arrival generator: Zipf skew, bursts, paraphrase rate, adversarial mix. |
| `internal/report` | the markdown writer that renders `Expect` / `Found` pairs. |
| `cmd/semcache` | the experiment driver. |

## The method

Every measurement in `cmd/semcache` is preceded by a `w.Expect(...)` call — a
written prediction — before the number is computed. When the number contradicts
the prediction, the report says so and the discrepancy becomes the finding.

This is not decoration. It caught seven genuine defects during construction,
including two that would have invalidated headline results:

- the gateway filled the cache when a request **arrived** rather than when its
  backend call **completed**, which defines away the exact window coalescing
  exists to cover (coalesced count was 0 for every mode, and it looked plausible);
- `Put` appended unconditionally, so the cache's "entry count" was a request
  counter;
- `report.Percentile` sorted its caller's slice in place;
- the lexicon mapped `remove → disable`, collapsing the `team` family into the
  `2fa` family — exactly the confusion the project measures;
- `Outcome.GuardVetoed` was set only on the cache-hit path, where it is almost
  never true, so it disagreed with `Stats.GuardVetoes` in precisely the case
  that mattered;
- the first formulation of section 1's bound used a single token's mass share
  and was wrong on 32 of 35 pairs — swapping one *word* changes several
  *features*, because the bigrams containing it change too;
- and finding 4 above, which exists entirely because a unit test asserted the
  comfortable thing and failed.

Each is documented at the site of the fix.

## Documents

- [`docs/results.md`](docs/results.md) — the generated report
- [`docs/known-limitations.md`](docs/known-limitations.md) — what this does not show
- [`docs/adr/`](docs/adr/) — five decision records
- [`docs/portfolio/`](docs/portfolio/) — design walkthrough, review notes, and the failure log
