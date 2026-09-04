# Failure log

Every defect found during construction, what found it, and what it would have
cost. Kept because the list is more informative than the finished code.

---

## 1. The cache was filled when a request arrived, not when its answer existed

**Found by:** a prediction that said "coalescing should absorb a large share of
burst traffic", followed by a measured `Coalesced` count of **0 for every mode**.

**What was wrong:** `serve()` called `cache.Put` immediately on a backend miss.
So the second of two concurrent duplicates found the answer already cached —
before the backend had returned it.

**Why it matters:** this *defines away the phenomenon*. Coalescing exists
entirely to cover the interval between a request starting and its answer
existing. A cache filled on arrival has no such interval. The measurement was
not wrong about coalescing; it had removed the thing being measured.

**Why it nearly survived:** zero is a plausible number. "Coalescing didn't help
much on this workload" is a believable sentence, and had the prediction not been
written down first there would have been nothing to contradict.

**Fix:** a `pendingPut` queue drained by `retire(now)` in arrival-time order.
Pinned by `TestCacheIsFilledOnCompletionNotOnArrival`, which asserts a duplicate
arriving at `latency/2` is *not* a hit.

---

## 2. The guard's cost was measured on the wrong population

**Found by:** a unit test — `TestGuardPreservesGenuineParaphrases` — asserting
the comfortable thing the report already claimed. It failed: 8 of 117.

**What was wrong:** nothing, in the code. The report priced the top-K veto at
one point of aggregate hit rate. The test measured 81% of paraphrases rejected.
**Both numbers were correct.**

**The reconciliation:** 81.8% of cache hits in the workload are verbatim
repeats, whose decisive sets are identical by construction and which the veto
passes for free. The guard is nearly invisible in aggregate *precisely because
it only touches the minority of traffic that the word "semantic" refers to*.

**Why it matters:** this is the report's own thesis — that a rate averaged over
traffic you do not control measures the traffic — committed a second time,
against the fix instead of the problem. It became section 3b, the strongest
section in the report, and changed the recommendation from "turn the veto on" to
"turn it on if you are deduplicating repeats; price it carefully if you are
consolidating paraphrases".

**Resolution:** the test now asserts the *true* property
(`TestGuardIsSevereOnTheParaphrasePopulation`) with a comment saying that if a
future change makes the guard gentle here, the report is wrong and must be
rewritten.

---

## 3. The report was not reproducible, and claimed to be

**Found by:** the reproducibility check in `test.ps1` — generate the report
twice, compare hashes. One cell alternated between `0.000` and `-0.000`.

**What was wrong:** three float sums accumulated in Go map iteration order,
which is randomised per process. Float addition is not associative, so the last
bits varied between runs. `embed.sparse` was the serious one: feature hashing
allows two tokens to collide into one bucket, so **every embedding vector** was
process-dependent in its last bits.

**Why it matters:** the README claims "re-running reproduces this file byte for
byte". That claim is the basis for trusting every number in it. A signed zero is
harmless; the same mechanism at a decision boundary is not, and the report's
comparisons are between arms differing by a few percent.

**Fix:** iterate sorted keys in `embed.sparse`, `embed.TokenWeights` and
`massBound`. Pinned by `TestEmbeddingIsBitIdenticalAcrossIndependentFits`, which
builds 60 independent models and compares exact bit patterns — enough map
orderings to fail reliably in-process — plus the cross-process hash check in
`test.ps1`.

---

## 4. The lexicon collapsed two confusable families

**Found by:** `TestFamilySiblingsNeverCollapseToTheSameConcept`, on its first
run.

**What was wrong:** the synonym table contained `remove → disable`. Removing a
teammate and disabling 2FA are different intents in different confusable
families. The instrument was manufacturing the exact confusion the project
claims to measure.

**Why it matters:** the finding would have been partly an artefact of the
author's hand-written table — the single largest integrity risk in a study that
needs a hand-built lexicon.

**The harder part** was stating the property correctly. The first version banned
every decisive token from the lexicon outright, and failed on `activate →
enable`, `upload → import`, `bigger → upgrade` — all legitimate synonymy where
the decisive token is the *canonical form*. The property had to split in two:

- **Class A**, tokens with no legitimate synonym at all (entities, plan names,
  status codes, tenants): banned on both sides.
- **Class B**, tokens that may be canonical forms but must never collapse with a
  family sibling.

Forcing that precision is what surfaced the bug. The over-strict version would
have been deleted as "too pedantic" and the bug would have stayed.

---

## 5. `Put` appended unconditionally

**Found by:** a prediction about cache size, contradicted by an entry count that
exactly equalled the request count (4000).

**What was wrong:** `Put` appended a new entry per call. A cache whose size is a
request counter is a log: it never dedupes, and its lookups get slower forever.

**Why it matters:** every "entries" column in the report was meaningless, and
the linear-scan lookup was O(requests) rather than O(distinct queries).

**Fix:** a `byKey` map on (scope, normalised text); `Put` replaces. Pinned by
`TestPutReplacesRatherThanAppends` and `TestEntriesNeverExceedDistinctTexts`.

---

## 6. `Outcome.GuardVetoed` disagreed with `Stats.GuardVetoes`

**Found by:** `TestGuardVetoesAreCountedWhenTheyChangeAnOutcome`, which asserted
the two agree.

**What was wrong:** the per-outcome flag was set only on the cache-*hit* path,
where `GuardRejected` is almost never true. The case where a veto sends a
request to the backend — the only veto that changes an outcome — left the flag
false. `Stats` counted them; outcomes did not.

**Why it matters:** any per-outcome analysis of vetoes would have found none,
while the aggregate said 38.

**Secondary finding:** writing the test revealed that `GuardVetoes` only counts
*outcome-changing* vetoes at all. If a correct entry is also above threshold it
wins and no veto is recorded. That is the operationally meaningful quantity, but
it is not what the name suggests, so it is documented in known-limitations.

---

## 7. `report.Percentile` sorted its caller's slice

**Found by:** `TestPercentileDoesNotMutateItsInput`.

**What was wrong:** in-place `sort.Float64s`. Section 1 hands the same slice to
`Summarise`, then `Overlap`, then plots it.

**Why it matters:** benign here only by luck — sorting is idempotent and none of
the consumers depended on order. It is the kind of aliasing bug that is harmless
until someone adds a consumer that does.

**Also fixed:** `Percentile(nil)` and `Overlap(nil, nil)` returned `NaN`, which
renders as `NaN` in a markdown table beside real numbers. Both now return 0.

---

## 8. The mass-share bound was wrong on 32 of 35 pairs

**Found by:** the prediction printed above the measurement.

**What was wrong:** the first formulation said the similarity drop equals the
*single swapped token's* mass share. Swapping one **word** changes several
**features**, because the bigrams containing it change too.

**Correct formulation:** `cos = Σ_shared sqrt(w_a · w_b)`, so the drop is the
mass of the *differing feature set*. Now exact on 35/35 pairs, MAE 0.000.

**Why it matters:** the corrected version is a stronger result — an identity
rather than an approximation — and it only exists because the wrong version was
written down where it could be contradicted.

---

## 9. The benign/trap traffic lever was too weak to show a signal

**Found by:** section 2 reporting 3.88% benign vs 3.31% trap-heavy — no
separation, and in the wrong direction.

**What was wrong:** `AdversarialBoost` reweighted sampling, but the benign
stream was still 46.6% adversarial. Sampling less often from a set that still
contains every confusable pair does not make a stream benign.

**Fix:** `OneIntentPerFamily` — benign users ask about Germany but never France.
The distinction is structural rather than statistical, and it produces the clean
0.00% vs 3.31% contrast the section needs.

**Why it matters:** without it, section 2's argument had no evidence. The
temptation to keep tuning the boost until the numbers separated was real, and
would have been fitting the workload to the conclusion.

---

## 10. Two design bugs in the streaming single-flight

**Found by:** the `flight` unit tests, before the package was wired into
anything.

- **`Do()` could recurse indefinitely** when a flight completed between the
  lookup and the join. Rewritten as a retry loop.
- **A late joiner's prefix replay could deadlock.** The prefix is replayed under
  the group lock; if the subscriber channel filled, the replay blocked while
  holding it. The channel is now sized `len(prefix) + 64`.

The design decision behind both — the *group* owns the subscriber list, not the
leader — is in ADR 0005. A leader-owned list has a race that no amount of
careful leader code fixes.

---

## What the list says

Nine of these ten were found by a mechanism, not by inspection: a written
prediction contradicted by a number (1, 5, 8, 9), a test asserting a property (2,
3, 4, 6, 7), or a test written before the code was used (10).

Three of them — 1, 2 and 3 — would have produced a plausible-looking report with
a wrong conclusion. None would have been caught by code review, because the code
was not obviously wrong; it was measuring the wrong thing, and that is only
visible if you write down what you expect first.
