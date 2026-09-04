# ADR 003: The exact solver refuses rather than degrades

## Status

Accepted.

## Context

Breaking a dependency cycle means choosing a set of edges to remove -- a feedback arc set.
Choosing the cheapest one is NP-hard. The standard engineering answer is a heuristic:
Eades, Lin and Smyth's greedy linear arrangement is `O(V + E)`, well understood, and good
enough in practice.

"Good enough in practice" is exactly the kind of claim this project exists to test. So
the codebase has both:

- `FeedbackArcSet.Greedy` -- Eades-Lin-Smyth.
- `FeedbackArcSet.Exact` -- a subset dynamic program over vertex orderings,
  `f[S + {v}] = f[S] + sum of w(u -> v) for u not in S + {v}`, which is `O(2^n * n)` time
  and `O(2^n)` space.

At n = 20 that is a million states; at n = 25 it is 33 million and several gigabytes. The
solver has to do something when the graph is too big.

## The tempting design

```csharp
public static FasResult Exact(DiGraph g, int maxNodes = 20)
{
    if (g.Nodes.Count > maxNodes) return Greedy(g);   // fall back
    ...
}
```

This is what most libraries do, and it is what I would have written without thinking about
it. It never fails. It always returns an answer. It is completely reasonable.

It is also the single most dangerous line I could put in this codebase.

## Decision

`Exact` throws:

```csharp
if (n > maxNodes)
    throw new InvalidOperationException(
        $"exact feedback arc set refused for {n} nodes (limit {maxNodes}); " +
        "the answer would take longer than the migration");
```

## Why

The entire argument of section 4 of `results.md` is a comparison between the heuristic and
the exact answer. If `Exact` silently returns `Greedy` above some threshold, then that
comparison reports "greedy matched exact on every unit" -- which is true, tautologically,
and means nothing. The report would contain a measured-looking number that measures the
fallback.

The failure would be invisible. There is no output that distinguishes "the exact solver
agreed with the heuristic" from "the exact solver was never run". Both print the same
thing. The only difference is that one of them is evidence.

More generally: **an exact solver that silently becomes a heuristic is worse than having
no exact solver at all**, because its output is still labelled "exact" and downstream code
still treats it as ground truth. A caller who wants a fallback can write
`try { Exact(g); } catch { Greedy(g); }` in one line, and that line is visible in the
call site where the decision belongs.

The same principle appears twice more in this codebase:

- `Report.Render()` throws while any prediction is unsettled, rather than omitting it.
- `Graphs.TopologicalOrder` throws on a cyclic graph rather than returning a plausible
  order.
- `Program.FindRepoRoot` throws rather than falling back to the working directory. That
  one was not a principle, it was a bug: the marker file it searched for was named
  `.sln` and the actual file is `.slnx`, so it *had* been falling back to the current
  directory. It only worked because I happened to run it from the project root. A test
  that asserted the report was current caught it.

## Consequences

**Good.** Section 4 of the report is real. On the three actual migration units in this
estate the greedy answer equals the exact answer -- the largest unit is 5 nodes, and on
graphs that small the heuristic is essentially always optimal. That is a genuine finding
and it contradicts the prediction that opened the section.

**Good.** Because the exact answer had to be real, the report goes on to measure *where*
the heuristic starts losing, on synthetic graphs: n=6 loses 9 times in 40 by an average of
0.38 edges; n=12 loses 33 times in 40 by an average of 3.48 with a worst case of 11. That
is the useful answer -- not "use greedy" or "use exact", but "greedy is fine for the
cycles you actually have, and stops being fine at about eight nodes".

**Good.** `Exact` also self-checks: it verifies that the backward-edge weight of the
ordering it reconstructs equals the value the DP computed. A DP that is right and a
reconstruction that is wrong is a plausible bug, and this catches it.

**Bad.** Callers must handle the exception. In practice there is one caller and it knows
the unit sizes, so this is theoretical. `CycleDissolver` has the same structure and makes
the same choice -- it returns `Possible = false` with a reason string above 22 types
rather than guessing -- but returns rather than throws, because "this unit is too tangled
to advise on" is a legitimate finding to put in a report, whereas "the exact answer is
actually the approximate answer" never is.
