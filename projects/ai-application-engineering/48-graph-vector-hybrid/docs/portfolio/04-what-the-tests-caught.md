# 04 — What the tests caught

Four defects the suite found, and one non-defect it is worth being precise about. Each
would have produced a plausible-looking report.

## 1. A question that asked a conjunction, scored against a plan that checked one half

**Found by:** `CorpusTest.premisesAreNecessary`

Q5 asks: *"Which firm has its corporate seat in Panama **and** is held by Anisimov?"*
Two conditions. Its plan checked only ownership:

```java
p.put("Q5", Plan.of("Viktor Anisimov", Plan.back("OWNED_BY")));
```

It returned the correct answer, because exactly one company in the corpus is owned by
Anisimov. Everything looked fine.

The test that dropped each premise in turn and required the answer to change caught it:
removing the Panama document changed nothing, because the plan never used it. So Q5 was
listed with two premises in §3's premise-recall table while only needing one — inflating
the retrieval burden the report attributes to it — and the graph was being credited with
answering a conjunctive question it had never evaluated.

```java
p.put("Q5", Plan.of("Viktor Anisimov", Plan.back("OWNED_BY"))
        .withGuard(new Plan.Guard(Set.of(), 0, "REGISTERED_IN", "Panama")));
```

**The general shape:** a right answer for the wrong reason is invisible to every
aggregate metric. The only way to find it is to attack the *inputs* — assert that each
thing you claimed was necessary actually is.

## 2. The query entity never went through the resolver

**Found by:** a number that was impossible

§5 builds the graph by clustering surface forms, so its nodes are named by whichever
surface form claimed each cluster ("MERIDIAN SHIPPING LTD"), not canonically. The plans
name entities canonically. So the plan's start node did not exist in the graph and the
traversal began nowhere.

It surfaced as: **perfect resolution (0 merge errors, 0 split errors) scored 10/16, while
the gold graph scored 16/16.** Those two things cannot both be true — a perfect
resolution reproduces the gold graph by definition. It was that impossibility, not a
failing assertion, that exposed it.

The tempting reading was "resolution is harder than expected", and the table would have
been published as a finding.

The fix (`Plan.remapEntities`) is also the more honest model of production: the user's
entity name is resolved by the same embedding at the same threshold before traversal
begins, so a threshold that is wrong for the corpus is wrong for the query in the same
way — the errors compound rather than cancel. `Resolver.resolveQuery` now handles names
that were never in the corpus at all.

**The general shape:** an internal consistency check — *this configuration must equal
that one* — catches things no single assertion does. `goldGraphIsTheCeiling` and the §5
sweep were each individually plausible; only together were they impossible.

## 3. `resolve()` looked pure and was not

**Found by:** `ResolverTest.invisibleErrorsAreStillErrors`, then pinned by
`resolveIsIdempotent`

`Resolver.resolve()` accumulated into instance fields. Calling it twice on the same
instance clustered the second input against the *first input's* clusters and returned a
different answer for the same argument.

The test that found it called `resolve()` twice by accident — once to build the graph,
once to score — and got a resolution that was perfect on the second call and imperfect on
the first. Same object, same input, two answers.

```java
canonicalOrder.clear();
canonicalVectors.clear();
assigned.clear();
```

Two regression tests now pin it: one asserts the same input gives the same output twice,
the other asserts a *different* input does not inherit the previous input's clusters.

**The general shape:** a method that returns a value and also mutates hidden state reads
as a pure function at every call site. This one would have produced a §5 table that was
subtly wrong in a direction nobody could have reverse-engineered from the output.

## 4. A deliberately planted trap that never sprang

**Found by:** a test asserting the trap works

The corpus contains "Meridian Shipping Ltd" and "Meridian Freight Services" as distinct
entities with confusable names, planted so that a low resolution threshold would fuse
them and fabricate a sanctions path. §5's prose described exactly that scenario.

`ResolverTest.theTrapCanBeSprung` asserted the fusion happens. It failed. The two names
have a cosine similarity of 0.269, and by the time the threshold drops that low, greedy
clustering has already assigned "Meridian Freight Services" elsewhere. **No threshold
ever fuses them.**

The prose was describing a mechanism that does not occur — while the report's own
false-alarm count said a merge *did* fabricate a path. Both were true; they were about
different merges. The real culprit is "Meridian Freight Services" fused with **"Baltic
Freight"** — two firms sharing one generic industry word, one of which sits inside the
sanctioned chain. Worse than the designed trap, and unplanned.

Two changes followed:

- `Experiments.describeMerges` computes the fused pairs at the offending threshold and
  prints them into the prose, so the report states the merge it *measured* rather than
  the one it predicted.
- `theBaitedTrapDoesNotSpring` asserts the designed trap never fires at any threshold,
  so the comfortable version cannot creep back.

**The general shape:** writing a test for the thing you are *sure* about is where the
surprises are. The trap was the part of the corpus design most confidently believed and
least checked.

## The non-defect: an equivalent mutant

`test.ps1` mutates production code and requires every mutation to be killed. One
candidate survived:

```java
return c != 0 ? c : Integer.compare(a[0], b[0]);   // →  return c;
```

The tie-break in `Retriever.topK`. Removing it changes nothing, and the honest response
was to find out *why* rather than to write a test that would pass either way. A short
probe confirmed both halves of the explanation: `List.sort` is a stable TimSort, and the
candidate list is built in ascending id order — so equal scores already emerge in id
order. The tie-break is provably unobservable. It is an **equivalent mutant**, not a gap
in the suite.

Three things were done with that:

1. The comment in `Retriever.java` was corrected. It previously claimed the tie-break
   prevented run-to-run variation; it does not, today.
2. The code was kept anyway, with the reason written down — both facts that make it
   unobservable are incidental, and a change to a parallel sort or to building the list
   from a map iteration would silently make retrieval order depend on something other
   than the score.
3. The failed candidate stayed in `test.ps1` **as a comment**, and was replaced with a
   killable mutation on the same class.

"The mutation survived" and "the mutation could not possibly have changed anything" are
different findings, and only one of them is a problem. Deleting the candidate quietly
would have destroyed that distinction.

## What the six surviving mutations verify

| mutation | what would break |
|---|---|
| negate the cosine score | the ranking inverts; least relevant retrieved first |
| make forward traversal follow inbound edges | "who owns X" answers "what X owns" |
| disable the guard | every supplier looks sanctioned |
| drop entity remapping from plans | §5 measures a naming accident instead of resolution |
| remove the reset in `resolve()` | the same input gives two answers |
| make `falseAlarm()` return false | §5's entire merge/split asymmetry disappears |

Each removes a guarantee some number in `docs/results.md` depends on. 6/6 killed.
