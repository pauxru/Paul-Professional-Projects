# Bugs the experiment found

Six defects. Every one of them produced a plausible number rather than an
error, and four of them produced a number that pointed in the same direction as
the conclusion the report was reaching for. That is the interesting property:
none of these would have been caught by anything failing.

They are recorded here in the order they were found, with the tell that exposed
each one, because the tells are the transferable part.

---

## 1. A bare number was going to be an unlabelled correct answer

**What was wrong.** The fact table contained a governed value of `"4"` — a
concurrency limit. The corpus generator also writes plausible near-miss
sentences to serve as distractors, and one of them could legitimately read
"the default fan-out is 4".

**Why it matters.** Under ADR 0001 the labels are complete *by construction*:
relevance is a set operation on declared spans. A generated sentence containing
the correct value outside any declared placement is an unlabelled positive — a
bag-of-words retriever that returns it is correct and is scored wrong. That is
precisely the pooling-bias failure the whole design exists to avoid, recreated
inside the design.

**The fix.** `assert_values_are_distinctive` now rejects any value shorter than
four characters or composed only of digits and spaces.

**The subtlety worth keeping.** The first attempt at the rule was "a value must
contain a letter". That rejects `99.0%`, which is perfectly distinctive — no
distractor sentence is going to contain `99.0%` by accident. The correct rule
is about *collision probability in this vocabulary*, not about character class.
Digits-and-spaces-only, and a length floor.

---

## 2. Provenance spans were one character short

**What was wrong.** `_span_of` and `_split_body` computed chunk boundaries by
walking to the end of the last token inside the limit. A declared placement
span ran to the end of the *sentence*, including its terminal period. So a
chunk that visibly contained the whole sentence failed the containment check by
exactly one character.

**The symptom.** `structural_240` reported 46.2% reachability — a dramatic,
publishable-looking result about how badly heading-based chunking performs.

**The tell.** A diagnostic counter called `partial`, added for an unrelated
purpose, read **6**. If structural chunking were genuinely severing half the
answers, `partial` — placements where *some* required span was covered but not
all — should have been in the dozens. Six was impossible. A counter added for
one reason falsified a conclusion reached for another.

**The fix.** Chunk boundaries now fall at the *start of the next token* rather
than the end of the current one, so trailing punctuation belongs to the chunk
that contains the sentence.

**The lesson.** Off-by-one in a span comparison does not fail. It produces a
number, and the number is in the range where results live.

---

## 3. The corpus violated its own contract at generation time

**What was wrong.** `SINGLETON_FAMILIES` declares facts that exist in exactly
one qualified form. The `_plan_matrix` layout renders a table with one row per
plan, and it was rendering rows for singleton families too — creating
qualified placements for facts that are contractually unqualified.

**Where it was caught.** Not by a retrieval result. By an invariant assertion
that ran at corpus construction.

**The fix.** At the source: `_plan_matrix` skips singleton families, rather than
downstream code learning to tolerate them.

**The lesson.** The alternative fix — teach the label computation to ignore the
extra rows — would have worked, passed the tests, and left the corpus
internally inconsistent for the next person to discover. Fix the generator, not
the consumer.

---

## 4. Heading-only sections were dropped, and it inflated a real finding

**What was wrong.** `_sections` split documents at heading boundaries and
skipped any section whose body was empty after stripping. A heading immediately
followed by a subheading — a normal document shape — therefore produced no
chunk at all.

**Why it matters.** Those headings' words never entered the plain-structural
index. `structural_240` was not just severing values from their qualifiers; it
was losing *vocabulary* the retriever needed. The loss pointed in the same
direction as the co-location effect the chunker exists to demonstrate, so a
genuine finding was being amplified by a bug that agreed with it.

**How it was found.** By a test written to pin an unrelated property:
`test_no_content_character_falls_outside_every_chunk`.

**The fix.** Sections retain heading-only bodies; `structural` and
`structural_prefixed` emit a heading-only chunk when the body splits to
nothing. Chunk counts went 522 → 601 and every number in the report moved.

**Why the conclusion survived.** Severance for the controlled pair stayed at 45
→ 0. The effect was still there after removing the thing that was
contaminating it. Had it collapsed, the finding would have been the bug.

---

## 5. Two predicates were methods sitting among properties

**What was wrong.** `Fact.qualifier_label` and `Placement.required` were plain
methods in dataclasses whose other members were `@property`. Any caller writing
`if placement.required:` gets a bound method object, which is **always truthy**.

**Why it matters.** No error, no warning, no failing test — just a condition
that is unconditionally true, in code that decides which spans a chunk must
contain to count as an answer. The natural reading of the call site is the
wrong one.

**The fix.** Both are `@property` now, and their call sites were audited.

**The lesson.** A dataclass with a mix of properties and no-argument methods is
a trap with no diagnostic. Pick one convention per type.

---

## 6. A "no significant differences" table that was arithmetically forced

**What was wrong.** Section 3 originally used a paired **permutation test** at
B = 4,000 resamples, with **Holm** correction over 595 comparisons. Both
choices are individually correct and defensible.

A permutation test cannot report a p-value below `1/(B+1)` = **2.50e-04**.
Holm's strictest threshold is `alpha/m` = 0.05/595 = **8.40e-05**.

The floor sits above the threshold. **No comparison could clear it regardless
of effect size.** The corrected column was forced to zero by arithmetic.

**Why this is the worst one.** It is invisible. "Nothing was significant after
multiple-comparison correction" is *exactly* what an honest, adequately
powered, genuinely null experiment looks like. There is no error, no anomaly,
no residual. It reads as a finding, and a careful reader has no reason to doubt
it. The report shipped it as a finding in an earlier revision.

**The fix.** The primary test is now a paired Student's t (p from the
incomplete beta function, checked against published critical values in the
suite). The permutation test is kept beside it in section 3a *with its floor
stated*, because the comparison is the most transferable thing in the report:

| test | raw p < 0.05 | Holm-corrected | smallest raw p |
|---|---|---|---|
| paired permutation (B = 4,000) | 537 | **0** | 2.50e-04 |
| paired t | 536 | **491** | 3.56e-78 |

Identical differences. The two tests agree on raw significance to within one
comparison. One reports 491 real differences and the other reports none.

`stats.permutation_resolution(B)` and `stats.iterations_for_holm(m, alpha)` now
exist so the arithmetic is a callable function rather than something somebody
has to remember, and `test_the_reported_defect_is_reproducible` pins the exact
numbers so the bug cannot return silently.

**The general rule, which is the point:**

> Before nesting a randomisation test inside a family-wise correction, check
> that `1/(B+1) < alpha/m`. If it is not, the table will fill with zeros and
> read as a result.

---

## What the six have in common

None of them threw. Four of them produced numbers that agreed with the
hypothesis being tested, which is the direction in which a researcher is least
likely to look.

The ones that were caught were caught by three things, in descending order of
how much work they were:

1. **Invariants asserted at construction** (#1, #3) — cheap, and they fire
   before any analysis can be built on the broken data.
2. **A diagnostic counter added for a different purpose** (#2) — free, and it
   was the only thing standing between the report and a confident wrong claim
   about structural chunking.
3. **Tests that pin properties rather than outputs** (#4, #5, #6) — the
   heading-only bug was found by a coverage property, not by anyone suspecting
   the chunker.

The one that took longest to see (#6) was found by *reading the report as a
reader* — noticing that a column of zeros was too clean, and doing the
arithmetic on the instrument rather than the systems. There is no automated
substitute for that, which is an uncomfortable thing to conclude in a document
about testing.
