# ADR 004: The assembly is the wrong unit

## Status

Accepted. This is the thesis of the project, so it is worth writing down as a decision
rather than leaving it implicit in the code.

## Context

Every .NET dependency tool operates on assemblies. `dotnet list package`, NDepend's
default view, the Visual Studio dependency diagram, `Assembly.GetReferencedAssemblies()`,
and every home-grown PowerShell script anyone has ever written for a modernisation
assessment. The assembly is the unit because it is the unit the metadata makes cheapest
to read: the AssemblyRef table is right there in the manifest and you do not have to
decode a single method body to get it.

That convenience determines what the industry believes about legacy estates.

## Decision

Compute at the finest granularity the metadata supports -- the call site -- and lift
upward explicitly, measuring what each lift destroys.

`IlReader` builds a **method** call graph from IL bodies. `TypeEdges` lifts it to types.
`AssemblyEdges` lifts that to assemblies. Weight means the same thing at every level: the
number of call sites behind the edge. Nothing above the method level is read from the
manifest.

## What the lifts destroy, measured

Each of these is a section in `results.md`, with the number the report actually produced.

**Portability is a property of a member, not a type.** `AppDomain.CreateDomain` is a
blocker: it compiles on modern .NET and throws at runtime. `AppDomain.CurrentDomain` is
fine, and appears in every assembly that logs its own base directory. A rule table keyed
on types cannot express that. `BlockerRules` is keyed on type *plus optional member*, with
member-specific rules taking precedence -- so `Bitmap` is a `Rewrite` (swap in an imaging
library) while `Bitmap.GetHbitmap` is `NoEquivalent` (the caller is not using an imaging
library, it is using Windows).

**Portability is not visible in the manifest at all.** Kendall tau between manifest
reference count and actual blocker severity is 0.567. That number looks like agreement
until you find the two assemblies that reference *only mscorlib* and contain
`BinaryFormatter`. `BinaryFormatter` is in mscorlib. Every assembly references mscorlib.
The cheapest possible scan is structurally blind to the worst possible finding.

**Entanglement is a property of a type, not an assembly.** Three multi-assembly strongly
connected components cover 10 of 24 assemblies -- 42% of the estate "in a cycle". At type
level, 5 of the 19 types in those components are knotted: 26%. One component has no
type-level cycle at all. A five-assembly cycle in this estate is caused by two types.

**Difficulty does not compose.** An assembly in a cycle cannot be migrated alone, so its
real difficulty is its whole unit's. Kendall tau between per-assembly score and unit score
is 0.635, and the assembly with the worst own score is not on the critical path. Ranking
assemblies by their own blocker count -- which is what every assessment deck does -- ranks
the wrong things.

**"Delete it" is a property of a type qualified by a string.** Call-graph analysis says 6
types are dead. Two of them are constructed reflectively. Honouring literal reflection
gives 4, honouring a recovered string prefix gives 3, and a fully sound analysis gives 0.
The unit of the answer is not "type", it is "type, under an assumption about strings".

## Consequences

**Good.** Every claim in the report is falsifiable at the level it is made. A test can
assert that a specific type is in a specific knot, because knots are made of types.

**Good.** The advice is actionable. "These three assemblies are entangled" is not a task.
"Move `Contoso.Claims.Rules.RuleContext` into a new assembly, no code changes" is a task,
and it exists only because the analysis went below the assembly.

**Bad.** It is dramatically more expensive. Reading the AssemblyRef table of 400
assemblies is milliseconds. Decoding every method body of 400 assemblies is not. On a real
estate this is a batch job, not an IDE feature. That is the right trade for an assessment
that happens once and drives a year of work; it is the wrong trade for a linter.

**Bad.** It needs the IL to be present and readable. Obfuscated assemblies, mixed-mode
C++/CLI assemblies and NGen images all degrade this analysis in different ways. See
`known-limitations.md`.

## The honest counter-argument

The assembly-level graph is not useless. It is exactly right for one question -- "what is
my build order?" -- because assemblies are the unit the build system operates on. The
`MigrationPlanner` uses it for precisely that, and for nothing else. The mistake is not
computing the assembly graph; it is answering *portability*, *entanglement*, *difficulty*
and *deletability* with it, because it is the graph that happened to be cheap.
