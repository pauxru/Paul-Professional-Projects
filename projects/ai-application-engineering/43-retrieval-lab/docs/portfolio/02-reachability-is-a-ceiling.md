# Reachability is a ceiling, not an objective

The standard way to argue about chunking is to argue about size. Smaller chunks
are more precise; larger chunks preserve context; pick a number in the middle,
maybe add overlap. Every parameter in that discussion is a length.

This lab was built to test that framing and it does not survive it. The
variable that matters is not how long a chunk is. It is whether a chunk keeps a
value together with the thing that says what the value *means*.

## The measurement that made the difference visible

Start with the obvious ceiling. Before retrieval, before ranking, ask: does any
chunk anywhere in the index contain a complete answer? Call it reachability.
Nothing downstream can exceed it.

Computed per *fact*, it is nearly useless here — five of seven chunkers score
exactly 1.000. Not because they are good, but because each fact appears in up
to three document layouts, and a chunker that destroys the answer in the plan
matrix still scores 1.000 because the FAQ rendering survived. That is a
property of the corpus's redundancy, not of the chunker.

The fix is to measure per *placement* — each individual rendering — and to split
failure into two kinds that call for opposite responses:

- **lost** — no chunk contains the sentence stating the value at all;
- **severed** — some chunk contains the sentence, but no chunk contains it
  together with the heading that scopes it.

The prediction written before looking: loss dominates at small chunk sizes,
severance at large ones, because they pull in opposite directions.

| chunker | chunks | intact | severed | lost |
|---|---|---|---|---|
| fixed_120 | 541 | 114/160 | 39 | 7 |
| fixed_240 | 287 | 141/160 | 18 | 1 |
| overlap_240_120 | 467 | 142/160 | 18 | 0 |
| recursive_240 | 318 | 142/160 | 18 | 0 |
| structural_240 | 601 | 115/160 | 45 | 0 |
| **structural_prefixed_240** | **601** | **160/160** | **0** | **0** |
| sentence_window_1 | 5245 | 103/160 | 57 | 0 |

Across the whole grid: **195 severed, 8 lost.** Severance is not one failure
mode among two, it is essentially the only one. And the direction is wrong:
`sentence_window_1`, the smallest unit in the grid, loses nothing and severs
the most.

## The controlled comparison

The last two structural rows are as close to a controlled experiment as this
kind of thing gets. `structural_240` and `structural_prefixed_240` produce the
**identical 601 chunk boundaries** — same splitting logic, same size limit,
same everything. The only difference is that the second repeats the heading
path ("Enterprise > Log retention") into the body of each chunk.

Severance: 45 → 0.

On the `plan_matrix` layout — the one where a table row's meaning lives
entirely in its column heading — intact placements go from **0/39 to 39/39**.

That is not a size effect. There is no size effect available; the sizes are
identical. It is a *co-location* effect, and co-location is a thing you can fix
with three lines of code that no amount of tuning a chunk-length parameter will
reach.

## Why the ceiling does not predict the outcome

The second half of the prediction — that reachability, being a bound, should
not predict the final score — held, and the way it held is the most
counter-intuitive number in the report.

`sentence_window_1` has **perfect** reachability, 1.000. Every answer has a
home. It also has the **worst** end-to-end score in the grid: nDCG@10 of 0.204,
serving 0.417 of targets against 0.908 for the leader.

It did not remove the loss. It **moved** it. Chunker loss 0.000; retriever loss
0.298. Splitting into 5,245 units gave every answer a home and gave the
retriever sixteen times more places to look, each fragment too short to carry
enough signal to be found.

This is the shape of the whole problem, and it is why the report decomposes
failure across all four stages rather than reporting one number:

> The stage you measure is the stage you fix, and fixing it in isolation
> relocates the failure to a stage you were not measuring, where it is
> invisible.

The decomposition is exclusive and exhaustive by construction — chunker,
retriever, ranker, served, tested in pipeline order, summing to one — so a
defect in it does not produce a plausible-looking split. It produces a row that
does not sum to one, and a test asserts that no row does.

The `binding` column names the stage costing the most for each configuration.
For **five of seven** chunkers it is the retriever, not the chunker. Which is
not where the chunking literature suggests looking.

## What this changes about the design of a real pipeline

**Compute reachability before you tune anything.** It needs the chunking and
the labels and no retrieval at all. If it is low, nothing downstream matters.

**Do not optimise it.** The chunker with perfect reachability is the worst
configuration in the grid. It is a bound to check, not a target to maximise —
the distinction that section 1a exists to make.

**Repeat the heading path into every chunk body.** It costs a few lines, it
costs nothing measurable in retrieval quality or index size, it takes severance
from 45 to 0, and it makes an entire class of confidently-wrong answers
*structurally impossible* rather than merely rare. Of everything in this
report, this is the finding with the best ratio of impact to implementation
cost, and it is invisible to nDCG (section 8).

**Stop arguing about chunk length.** The grid contains a 2x length sweep, an
overlap variant, and a recursive splitter, and the differences between them are
small compared to the difference made by whether the heading travels with the
text.

## A defect this section found in its own instrument

While writing the coverage test that pins "no content character falls outside
every chunk", the suite failed on the structural chunkers. A heading
immediately followed by a subheading, with no body text between them, produced
a section whose body was empty — and the section builder dropped it entirely.

The consequence was not a coverage cosmetic. Those headings' words never
entered the plain-structural index at all, so `structural_240` was losing
*vocabulary*, and the loss looked exactly like the co-location effect the
chunker exists to demonstrate. A real finding was being inflated by a bug that
pointed the same way — the most dangerous kind.

Fixing it took chunk counts from 522 to 601 and every number in the report
moved. Severance stayed at 45 → 0, which is what made it safe to keep the
conclusion: the effect survived the removal of the thing that was
contaminating it.
