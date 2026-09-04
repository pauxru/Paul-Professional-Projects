# Transferable results

The parts worth carrying to a system that has nothing to do with caching.

---

## 1. A feature that must overrule a document cannot live inside a mean

For any L2-normalised pooled representation, the similarity between two items is
the shared feature mass:

```
cos(a, b) = Σ_{f ∈ shared} sqrt(w_a(f) · w_b(f))
```

so changing a set of features costs exactly the mass they carried. In a
twelve-token sentence, one decisive word carries about a twelfth. **A threshold
on a pooled score cannot express "identical except for the one thing that
matters",** because the score has already averaged that thing away.

The fix is not a better encoder or a better threshold. It is a **second signal
applied as a veto, outside the score**: extract the K most informative tokens
and reject when they disagree, regardless of similarity. That took confusable
error from 3.7% to 0.0%.

**Where this generalises:** any ranking or matching system where a small,
high-stakes part of the input must be able to overrule the rest. Address
matching where the house number decides. Product search where the model number
decides. Log clustering where the error code decides. Deduplication where the
account ID decides. If the deciding field is one term in a weighted sum, it will
be outvoted by everything else, and the failure will look like a tuning problem.

---

## 2. Any rate averaged over traffic you do not control is a measurement of that traffic

The same cache, unchanged, reports a **0.00%** false-hit rate on one stream and
**3.31%** on another. The metric moved across its entire range because the
questions changed.

Such a metric improves when customers ask easier questions, cannot be compared
across two weeks or two tenants, gives no signal before launch, and degrades
silently when a new customer arrives with a harder distribution — which is the
failure it was supposed to catch.

**The replacement is a fixed probe.** Hold the inputs constant, vary only the
system. Here: warm one entry per intent, replay every other phrasing, count the
confusions. It reads identically on both streams (the defining property) and
moves monotonically with the threshold (0/0/2/6/11 as the cut falls), which
makes it something you can actually tune against.

**Where this generalises:** every quality metric computed over production
traffic. Fraud model precision. Search relevance. Spam catch rate. Autocomplete
acceptance. Each is a weighted average whose weights are set by users. Keep them
for *detecting change*; do not use them for *deciding whether a change is safe*.

---

## 3. The same mistake will be made against the fix

The top-K veto was priced at one point of aggregate hit rate — nearly free.
Measured on the population it actually adjudicates, it rejects **81.4%** of
genuine paraphrase hits. Both numbers are right: 81.8% of cache hits are
verbatim repeats, whose decisive sets are identical and which the veto passes
for free.

So the fix's advertised cost was hidden by exactly the mechanism the project
exists to expose. Having identified that a metric is dominated by benign
traffic, I then evaluated my own mitigation with a metric dominated by benign
traffic.

**The general form:** when you introduce a defence, measure it on the population
it defends against and the population it might harm — separately, by name. An
aggregate that mixes them will report the size of the majority, not the quality
of the defence.

---

## 4. Some correctness problems are not similarity problems

Three intents in the corpus are worded **byte-identically** across tenants with
three different correct answers. Cosine is 1.000. No encoder separates them; no
threshold rejects them; the veto passes them trivially.

Only **partitioning** works — making the tenant part of the key rather than part
of the score. Cost: 2.8 points of hit rate. Benefit: 96 cross-tenant leaks
eliminated, each served at the highest confidence the system can express.

**Where this generalises:** locale, environment, API version, entitlement tier,
data-residency region. Any axis where identical input must produce different
output is a partition key. Attempting to solve it with a better model is how
cross-tenant leaks ship, and the confidence score will be no help — it will be
maximal.

---

## 5. A metric can be blind by construction, not by omission

Coalescing precision falls from 100% to 94.9% while **cache-reported precision
stays flat at 95.5%**. No counter is missing. Every coalesced request was a
cache *miss* — the cache was asked, correctly said "I don't have that", and was
bypassed. Its counters are perfect; they are counting a different population.

**The general form:** when a request can be satisfied by a path that bypasses
component X, X's metrics cannot describe those requests, and adding counters to
X will not help. The instrumentation has to live at the point where the decision
is made, not inside the component that was skipped.

Concretely: `Stats` here exposes both `Precision()` and
`CacheReportedPrecision()`, and the gap between them is the finding.

---

## 6. Write the prediction before the number

Every measurement in the report is preceded by a written, falsifiable prediction.
This found **seven** genuine defects, three of which would have produced a
plausible report with a wrong conclusion:

- the cache was filled at request *arrival* instead of backend *completion*,
  which deleted the entire window coalescing exists to cover. Coalesced counts
  were 0 for every mode — a believable number that would have been believed;
- the guard's true cost, above;
- the report was not byte-reproducible despite claiming to be, because three
  float sums accumulated in Go's randomised map order.

The mechanism is not discipline for its own sake. **A prediction you cannot
justify is a measurement you have not designed.** The first version of finding 1
predicted a drop equal to the swapped *token's* mass share and was wrong on 32
of 35 pairs; understanding why (swapping one word changes several features,
because the bigrams containing it change too) produced an exact identity instead
of an approximation.

---

## 7. Make the integrity property a test, then make it precise

The study is worthless if the cache can see the answer key. Three tests enforce
it: scramble every request label and require the decision sequence to be
byte-identical; scramble every stored label and require every hit/miss decision
unchanged; forbid the hand-written lexicon from mentioning any token that
decides an answer.

The third is the interesting one. The first version banned decisive tokens
outright and **failed on legitimate synonymy** (`activate → enable`). Forcing the
property into two precise classes —

- tokens with no legitimate synonym at all (proper nouns, plan names, status
  codes): banned entirely;
- tokens that may be canonical forms but must never collapse with a family
  sibling —

surfaced a real bug: the table contained `remove → disable`, merging two
confusable families and manufacturing the exact error being measured.

**The lesson:** an over-strict invariant that fails on a legitimate case is not
noise to be deleted. It is a signal that the property is not yet stated
correctly, and the corrected statement is usually worth more than the original
test.
