# How it is built

## The spike that decided the architecture

The design hinged on one question I could not answer from documentation: **can Mono.Cecil
author an assembly that references `System.Web 4.0.0.0` when `System.Web` is not installed
anywhere on the machine?**

If no, the corpus has to be compiled from source, which means the Framework reference
assemblies have to be installed, which means the project is no longer demonstrating
IL-level analysis of an estate you cannot build. The whole thing collapses into a
different, less interesting project.

So before writing anything else I wrote a forty-line spike:

```csharp
var sysWeb = new AssemblyNameReference("System.Web", new Version(4, 0, 0, 0));
module.AssemblyReferences.Add(sysWeb);
var httpContext = new TypeReference("System.Web", "HttpContext", module, sysWeb);
var current = new MethodReference("get_Current", httpContext, httpContext) { HasThis = false };
il.Emit(OpCodes.Call, current);
```

It writes. It reads back. The AssemblyRef, TypeRef and MemberRef scopes are all correct.

That is not a Cecil trick -- it is the property of ECMA-335 that makes legacy analysis
possible at all. An assembly is a set of *names*, resolved at load time by a runtime that
may or may not be there. Nothing about writing metadata requires the referenced thing to
exist. Once that was confirmed, everything else followed.

Two more facts made the emitter cheap:

- `AssemblyDefinition.CreateAssembly` defaults its corlib reference to `mscorlib 4.0.0.0`
  with no configuration. That is exactly the Framework-era manifest you want.
- **Cycles need two passes.** Create every `AssemblyDefinition`, `TypeDefinition` and
  `MethodDefinition` first; then fill the method bodies using `module.ImportReference`
  against the in-memory definitions. A single-pass emitter physically cannot express
  `A -> B -> A`, and cycles are the entire subject of sections 3 through 5.

## The layers

```
CorpusSpec       the estate, declared: 24 assemblies, 42 types, 95 methods,
                 plus ground truth about what is knotted / dead / undecidable
      |
      v  Mono.Cecil, two passes
CorpusBuilder    emits real .NET Framework DLLs, 21 with PDBs and 3 without
      |
      v  a directory of bytes -- the seam
IlReader         method call graph, blockers, reflection sites.
                 no source, no reference assemblies, no resolver.
      |
      +-> Graphs           iterative Tarjan, topological order, waves
      +-> FeedbackArcSet   greedy Eades-Lin-Smyth, exact subset DP
      +-> Reachability     four modes, forming a lattice
      +-> CycleDissolver   exact minimum type extraction
      +-> MigrationPlanner units, condensation, waves, critical path
      |
      v
Experiments      the report as a program: 12 predictions, measured
```

The seam is deliberate and it is the most important line in the diagram. Everything above
it knows the ground truth. Everything below it sees only bytes. `CorpusIntegrityTest`
stands on the seam and checks that the bytes really do contain what the spec claimed --
using machinery that has no access to the spec.

## Decisions worth explaining

### The reader never resolves anything

`IlReader` reads every module with a `NullResolver` that throws on every request. This
started as an obvious consequence of the premise -- the Framework is not installed -- and
turned into the most interesting finding in the build.

A test asserted that all seven referenced Framework assemblies fail to resolve. It failed:
only three did. `System.Web`, `System.Data`, `System.Drawing` and `System.Configuration`
all resolve on .NET 10, to type-forwarding facades versioned 4.0.0.0 that contain one type
each and none of the types the estate uses.

So a resolving analyser would not fail loudly. It would resolve `System.Web`, look for
`HttpContext`, not find it, and take whatever branch its author wrote for "unresolved
type" -- most likely skipping silently. The worst blocker in the estate would be reported
as clean, and the result would vary by machine. ADR 002 has the full detail. The test that
replaced the failing one asserts something better: `TheReaderNeverAsksToResolveAnything`
passes a recording resolver and asserts zero requests.

### Weight means the same thing at every level

`MethodEdges` carries weight = number of call sites. `TypeEdges` lifts to types.
`AssemblyEdges` lifts to assemblies. Originally the lift *counted edges* rather than
summing weights, so a type edge's weight was "how many method pairs" and the call-site
count collected by the reader was silently discarded above the method level.

A test caught it, and the fix is a one-token change (`+ 1` becomes `+ e.Weight`). It
matters because the feedback-arc-set solver minimises *weight*, and weight is supposed to
answer "what would this cut cost me". A call site is a place a human has to go and change
something. That is a unit. "Number of method pairs" is not.

### Tarjan is iterative

The recursive formulation of Tarjan's algorithm is shorter and clearer, and it blows the
CLR stack on a deep dependency chain. Not on a 24-assembly corpus -- on a real 400-
assembly estate, in production, during the assessment. `GraphsTest` runs it over a
200,000-node chain and a 100,000-node cycle.

The iterative version keeps an explicit work stack of `(vertex, childIndex)` pairs. It is
uglier. It is also the difference between a tool that works and a tool that works on the
demo.

### Exact solvers refuse rather than degrade

`FeedbackArcSet.Exact` throws above 20 nodes rather than falling back to the heuristic.
`CycleDissolver` returns `Possible = false` with a reason above 22 types. ADR 003 is the
full argument, but the short version: section 4 of the report is a comparison between the
heuristic and the exact answer, and a silent fallback would make that comparison say
"greedy matched exact everywhere" -- tautologically true, and indistinguishable in the
output from a real result.

### The cycle dissolver allows the extracted assembly to be internally cyclic

This is the subtlety that makes the section useful. If you extract a set of types into a
new assembly and *those types still reference each other*, you have not fixed the design
-- but you have fixed the build, because a cycle inside a single assembly is not a
build-order problem.

So a genuinely entangled unit does not come back "impossible". It comes back "move these
three types into one new assembly; the build linearises; the tangle survives inside that
assembly and still needs a code change". That distinction -- `QuarantinesAKnot` -- is the
difference between advice and a diagnosis.

### The report is a program that refuses to lie

`Report.Render()` throws if any prediction is unsettled, if any character is non-ASCII
(the console is code page 1252 and a report full of mojibake is a report nobody reads),
or if a table row has the wrong number of cells. `Prediction.Settle` throws if called
twice, or with blank evidence.

Two of those checks were added because tests asked for them and the code did not have
them. That is the correct direction of travel.

## What the numbers cost

The whole pipeline -- emit 24 assemblies, read them back, run Tarjan, run an exact
feedback-arc-set on three units, run an exact subset search for type extraction, run four
reachability analyses, run 40 synthetic feedback-arc-set comparisons at each of four
graph sizes, and render 19KB of Markdown -- takes about four seconds.

The full six-stage `test.ps1`, which does all of that eight times over (once clean, seven
times with a mutation applied), takes 147 seconds.
