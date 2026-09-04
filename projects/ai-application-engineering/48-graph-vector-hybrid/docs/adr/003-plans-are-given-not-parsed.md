# ADR 003: Query plans are handed to both systems, not parsed

**Status:** accepted
**Date:** during design

## Context

Turning *"which suppliers of Ashford Components are ultimately controlled by a
sanctioned person"* into a traversal is a language problem. In a production system it is
what the model does, and it is a substantial part of the engineering.

## Decision

The plan is written by hand, correctly, and given identically to both systems.
`Plan.java` carries a javadoc saying so, because a reader who discovers this in the
source rather than in the documentation will reasonably assume it was hidden.

## Rationale

If the report included a parser, every wrong answer would have two possible causes:

1. the substrate could not support the question, or
2. the parser mangled it.

The report could not tell you which, and the central claim -- that retrieval fails on
conjunctions for structural reasons -- would be unfalsifiable. With plans given, the
measurement is sharply about one thing: *given a correct plan, can this substrate
execute it?*

## Consequences

- The comparison is **generous to the retriever**, which is the direction generosity
  should run. A retriever handed a perfect plan and a perfect reader still cannot
  answer a four-hop question, and that result does not depend on any parsing claim.
- The report does not measure NL-to-plan translation. `docs/known-limitations.md` says
  so directly; it is the single largest thing this project does not show.
- Plans are data, so they can be checked. `CorpusTest.hopCountsMatchThePlans` asserts
  that every question's declared hop count equals its plan's structural hop count, and
  `premisesAreNecessary` asserts that dropping any premise changes the answer.

## What this caught

`premisesAreNecessary` failed on Q5 the first time it ran. The question asks *"which
firm has its corporate seat in Panama **and** is held by Anisimov?"* -- a conjunction of
two conditions -- but the plan checked only ownership. It happened to return the right
answer because only one company is owned by Anisimov, so the second premise document was
listed as a premise while being unreachable by the plan that was scored.

That is precisely the class of error this ADR exists to make visible: a plan that
silently under-specifies its question, giving a correct answer for the wrong reason and
inflating the premise-recall table in §3. The fix was to express the conjunction as a
guard. Without the necessity test it would have shipped, and it would have been
invisible in every aggregate number in the report.
