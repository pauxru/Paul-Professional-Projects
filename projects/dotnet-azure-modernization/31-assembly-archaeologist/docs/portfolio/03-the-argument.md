# The argument

Twelve predictions, written before anything was measured. Three held.

This essay is about the nine that did not, and what they add up to.

---

## The predictions that held, first

It matters that some held, because a report where every prediction fails is a report full
of straw men, and I would not believe it either.

**P3 -- there is no migration order.** Three strongly connected components contain more
than one assembly, covering 10 of 24 assemblies. `Graphs.TopologicalOrder` throws on the
raw assembly graph. This is the boring, correct expectation and it was correct.

**P4 -- the tangle is bounded.** 14 of 17 migration units are single assemblies. Only 3
units need the expensive analysis; the other 14 need only an ordering. Estates feel
totally entangled and are not.

**P12 -- there is parallelism.** 24 assemblies in 6 waves, 5 of which contain more than
one independent unit; the widest holds 5. The critical path, not the assembly count, sets
the schedule.

---

## The nine that failed, and the pattern in them

### The blockers are not where you would look for them

**P1 predicted** that the worst blockers sit deep in the estate, in the old code at the
bottom that everything depends on.

They do not. No-equivalent blockers span waves 1 through 5 of 6. The deepest one is
`Contoso.Common.Utils` at **wave 1** -- the very first thing you would migrate -- holding
`BinaryFormatter`. It is also one of the three assemblies with no symbols.

**P2 predicted** that a cheap manifest scan would broadly agree with a real analysis.
Kendall tau between manifest reference count and blocker severity is **0.567**, which
looks like agreement until you notice that **two assemblies reference nothing but
mscorlib and contain no-equivalent blockers**. `BinaryFormatter` is in mscorlib. Every
assembly references mscorlib. A manifest scanner cannot see the estate's worst problem,
not because it is imprecise, but because the evidence is not in the table it reads.

### The cycles are smaller than they look, and made of different stuff

**P5 predicted** that an assembly cycle means the code inside is entangled.

10 assemblies are in cycles, containing 19 types. **Five of those types are actually in a
cycle** -- 26%. The largest unit ties **5 assemblies together on the strength of 2
types**. And one of the three units contains **no type-level cycle at all**: the cycle
exists purely because of which types were put in which project file.

**P7 predicted** that breaking a cycle means deleting dependency edges, and that the
output of cycle analysis is a list of edges to remove.

For the same three units, cutting 4 edges and moving 7 types both produce an acyclic
build. They are not the same work. Cutting an edge means finding every call site across
it and redesigning the interface. Moving a type means dragging a file between two project
folders. For one unit -- `Contoso.Claims.Rules` -- the answer is **move one type, no code
changes at all**.

A tool that reports edges is technically correct and practically useless, because the
cheapest fix is not an edge operation.

### Approximation is fine here, and would not be there

**P6 predicted** that the greedy feedback-arc-set heuristic would be measurably worse
than the exact answer, which is why the exact solver exists.

On all three real units, greedy equals exact. The largest unit is 5 nodes, and at that
size the heuristic is essentially always optimal.

That is a null result, and a null result you can only report if the exact solver is
genuinely exact -- which is why it throws above 20 nodes rather than silently falling back
to greedy (ADR 003). Having established it is real, the report goes on to measure *where*
greedy starts losing, on synthetic graphs:

| nodes | greedy loses | mean excess | worst |
|---|---|---|---|
| 6 | 9 / 40 | 0.38 | -- |
| 8 | 23 / 40 | 1.57 | -- |
| 10 | 33 / 40 | 2.10 | -- |
| 12 | 33 / 40 | 3.48 | 11 |

The useful answer is not "use greedy" or "use exact". It is: **greedy is fine for the
cycles you actually have, and stops being fine at about eight nodes.** You cannot say that
without both solvers, and you cannot trust it if one of them is secretly the other.

### "What can we delete?" is not a question with one answer

This is the section I would put in front of a client first.

**P8 predicted** that static reachability finds the dead code. It declares 6 types dead.
**Two of them are alive**, named as string literals or type tokens elsewhere in the same
directory. Deleting on the strength of the call graph would have removed working code, and
the evidence that it was working was sitting in the same folder the whole time.

**P9 predicted** that reflection makes sound dead-code analysis worthless, so everyone
ships the unsound answer.

The first half is exactly right. The fully sound analysis says **0 types are deletable**,
because one call site builds a type name at run time and nothing bounds where it lands.

The second half is wrong, and this is the finding. The name is not built from nothing:

```
ldstr "Contoso.Claims.Plugins."
...
call string System.String::Concat(string, string)
call class System.Type System.Type::GetType(string)
```

The prefix is *still in the IL*. Recovering it narrows "any type in the process" to "any
type under that namespace", and that takes the sound answer from **0 deletable types to 3,
and 0 deletable assemblies to 1** -- without giving up soundness. Two instructions of
dataflow buy back the entire result.

The four answers form a lattice, and the report prints all four with the assumption each
one requires:

| analysis | dead types | dead assemblies | sound? |
|---|---|---|---|
| CallsOnly | 6 | 3 | no |
| DecidableReflection | 4 | 2 | no |
| PrefixConstrained | 3 | 1 | yes, if the prefix is constant |
| FullySound | 0 | 0 | yes |

Nobody should be given one of those numbers. They should be given the ladder, and told
which rung their risk tolerance buys.

### Difficulty does not compose, and the constraint is not what you think

**P10 predicted** that ranking assemblies by their own blocker score gives you the
priority order. Kendall tau between own score and unit score is **0.635**, and the
assembly with the worst own score, `Contoso.Claims.Web` at 7, **is not on the critical
path**. Sequencing the project around it would be sequencing around the wrong thing.

**P11 predicted** that missing source is an analysis problem -- that the assemblies whose
source was lost are the ones you cannot say anything about.

Analysis is entirely unaffected. All three source-less assemblies read fine, their call
edges recovered, three blockers found inside them, including a `MessageQueue` that is the
most severe category there is. *Planning* is where it bites: **21 of 24 assemblies
(87.5%) transitively depend on something nobody can rebuild**, against 13 (54.2%) that
contain a blocker of their own.

The estate is constrained more by what cannot be recompiled than by what will not compile.
That is not a fact about code. It is a fact about the organisation, and it is invisible to
every tool that looks only at code.

---

## What the nine failures have in common

Every one of them is the same mistake in a different costume: **answering a question at the
granularity the data happened to arrive in.**

The manifest arrives per assembly, so portability gets answered per assembly -- and misses
`BinaryFormatter` in mscorlib. The SCC algorithm runs on whatever graph you hand it, so
entanglement gets answered per assembly -- and turns 2 knotted types into a 5-assembly
crisis. Reachability runs on the call graph, so deletability gets answered per call edge --
and deletes live code. Blocker counts aggregate per assembly, so difficulty gets ranked
per assembly -- and ranks something off the critical path first.

The correct granularities are: **member** for portability, **type** for entanglement,
**migration unit** for difficulty, and **type qualified by a string constant** for
deletability.

None of those is the assembly. The assembly is right for exactly one thing -- build order
-- because that is the unit the build system operates on. It is the unit everything else
gets answered in because it is the unit that was cheap to read.
