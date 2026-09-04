# ADR 004: Entity resolution is inside the measurement, not assumed away

**Status:** accepted
**Date:** during design; the specifics revised after §5 was measured

## Context

Comparisons of graph and vector retrieval usually evaluate a graph that already exists,
with clean nodes. That skips the step where the graph is *built*, and the build step is
where the interesting failure lives.

A graph over corporate filings cannot exist until somebody decides that "Halcyon Trading
Co" and "Halcyon Trading Company" are one node, and that "Meridian Shipping Ltd" and
"Meridian Freight Services" are two. That is a similarity judgement over surface form.
It is what embeddings are for, and it is what traversal cannot do.

## Decision

Include resolution in the system under test. `Systems.resolvedGraph(resolver)` builds
the graph from surface forms as they appear in the text, clustered by the same embedding
the retriever uses. §5 sweeps the threshold and measures answer quality downstream.

The **gold graph** -- built with perfect resolution -- is retained as a separate ceiling,
so the report can attribute a failure to resolution rather than to traversal.

## Consequences

### The architecture the measurement supports is neither pure option

**Vector for identity, graph for traversal.** The embedding decides what a node is; the
graph decides what follows from it. Neither technique can do the other's job, so the
"graph or vector" framing was never a real choice.

### The query entity must go through the same resolver

Not obvious, and it was a bug before it was a decision. A plan names entities
canonically; a resolved graph names them by whichever surface form claimed the cluster.
Without `Plan.remapEntities`, the plan's start node is simply absent from the graph and
every answer is empty -- a harness bug that looks exactly like catastrophic resolution
failure. It presented as "perfect resolution scores 10/16 while the gold graph scores
16/16", which is impossible, and that impossibility is what exposed it.

The corrected behaviour is also the more honest model: in production, the user's entity
name is resolved by the same embedding at the same threshold before traversal begins, so
a threshold that is wrong for the corpus is wrong for the query in the same way, and the
errors compound rather than cancel.

### Aggregate resolution metrics hide the errors that matter

At two decimal places, pair recall and pair specificity read 1.00 across the entire
sweep. Four bad merges out of ~550 cross-entity pairs is a specificity of 0.993 -- a
number that rounds to perfect and reads as success on a dashboard, while fabricating a
sanctions exposure. The report prints three decimals and says why.

The damage is not proportional to the pair count. It is proportional to how *central*
the merged node is.

### Resolution errors are only visible when someone traverses through them

At threshold 0.40 the resolver makes two merge errors and one split error, and every
answer is still exactly right. The error rate you can measure and the error rate that
matters are different quantities, and the second depends on the query workload.

## The trap that did not spring, and the one that did

The corpus was seeded with "Meridian Shipping Ltd" and "Meridian Freight Services" on
the expectation that a low threshold would fuse them. **No threshold ever does** --
their cosine similarity is 0.269, and by the time the threshold drops that far, greedy
clustering has already assigned "Meridian Freight Services" to a different cluster.

What actually happens is worse. It gets fused with **"Baltic Freight"** -- two firms
sharing a single generic industry word -- and Baltic Freight sits inside the sanctioned
ownership chain. Ravenna Textiles buys from the merged node, inherits the sanctioned
parent's edges, and the graph asserts an exposure no document supports.

The report states the merge it measured, not the one it was designed to bait:
`Experiments.describeMerges` computes the fused pairs at the offending threshold and
prints them into the prose. `ResolverTest.theBaitedTrapDoesNotSpring` asserts the
designed trap never fires, so the claim cannot rot.

## What was rejected

**A better clustering algorithm.** Single-pass greedy clustering is deliberately naive.
A better clusterer moves the threshold at which each error appears; it does not remove
the trade-off, because the trade-off is a property of a similarity function being asked
to make a discrete decision. Using the simple version keeps the measured effect
attributable to the threshold rather than to clustering heuristics.
