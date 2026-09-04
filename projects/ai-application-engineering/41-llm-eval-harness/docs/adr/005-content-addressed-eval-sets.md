# ADR 005: Content-address the eval set and fail closed on a mismatch

**Status:** Accepted

## Context

Eval sets are edited constantly and the edits are rarely recorded. Someone adds
fifteen cases from last week's incident. Someone drops an item everyone agreed
was ambiguous. Someone rewrites a reference answer that was subtly wrong. Each
edit is individually correct and improves the eval set.

The comparison across such an edit is meaningless, and nothing in a normal
pipeline notices. Both runs produce a mean. The means differ. The difference
is attributed to the change under test.

Section 11 measures the size of this: the *same unchanged model* scores
-0.0397 differently on v2 of an eval set than on v1, which is 0.99x the size
of the +0.04 improvement the report spends its other sections trying to
detect. The model did not change. The ruler did.

## Decision

Every `Item` hashes its own content. Every `Dataset` has a fingerprint derived
from its items' hashes, computed order-independently so that reordering is not
treated as a change. Every `RunResult` carries the fingerprint of the dataset
it was produced from. `compare()` checks it before computing any statistic and
returns `INCOMPARABLE` on a mismatch.

`INCOMPARABLE` blocks. It is the only non-regression verdict that does.

## Consequences

**The gate refuses rather than reporting a number.** This is the correct
failure mode and it is unpopular: it turns a silent wrong answer into a loud
inconvenience, and the loud inconvenience arrives at the moment someone is
trying to ship.

**An escape hatch exists and is explicit.** `allow_dataset_mismatch=True` is
sometimes genuinely right -- comparing across a deliberate eval set upgrade,
for example. Making it a named argument at the call site means the decision is
visible in review, which a config default would not be.

**Fingerprints are content-based, not id-based, and that distinction is
load-bearing.** Section 11 tries the tempting repair: the two eval set
versions share 50 item ids, so restrict the comparison to those. It does not
work. Five of the shared ids have modified content, because reweighting the
tier mix changed which topic each slot draws. **An id that survives an edit is
not the same item.** `comparable` is False on modification as well as on
removal for exactly this reason, and a test pins it.

**Order-independence was a deliberate choice with a cost.** Sorting item
hashes before combining them means a reordered eval set is recognised as the
same eval set, which is right -- order is not content. It also means the
fingerprint cannot detect a reordering, and a reordering *does* matter for the
paired arrays. That case is caught separately by the `item_ids` equality check,
which runs first. Two mechanisms, because they answer different questions:
"is this the same eval set?" and "can these two runs be paired index by index?"

## Consequences for the simulation

The same reasoning applied inside `SimulatedModel`. Responses are seeded from
a hash of the item id, not drawn from a stream, so adding an item to the eval
set does not perturb the responses generated for every item after it. Without
that, growing an eval set silently re-rolls every score, and every historical
comparison becomes meaningless with nothing visibly changing -- a
reproducibility bug that would be extremely hard to attribute after the fact.
`test_growing_the_dataset_does_not_change_existing_scores` pins it.

## Alternatives considered

**Version the eval set in git and compare commit SHAs.** Correct in principle
and defeated by the working copy. The file on disk at run time is what was
evaluated, not what was committed, and uncommitted edits are the normal case
during exactly the experimentation this gate is meant to protect.

**Warn instead of blocking.** A warning that appears on a green build is not
read. The verdict has to be terminal to function.

**Hash only the prompts.** Cheaper and wrong: a corrected reference answer
changes every score for that item without touching the prompt, and correcting
reference answers is one of the most common eval set edits there is.
