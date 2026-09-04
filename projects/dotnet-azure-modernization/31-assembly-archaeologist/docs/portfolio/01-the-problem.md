# The problem

## The slide

Every .NET modernisation engagement I have seen starts with the same slide. Somebody has
run a dependency scanner over the estate and produced a graph: 400 boxes, 3,000 arrows,
laid out by a force-directed algorithm so it looks like a hairball with a few dense knots.
The caption is usually some variant of "current state".

Everyone in the room agrees it looks bad. That is the only thing the slide achieves.

The next question is always the same, and the slide cannot answer it: *what do we do on
Monday?* Which assembly moves first. What breaks when it does. Whether the thing that
looks worst on the diagram is actually the thing that will cost the most. Whether we can
delete any of it. How long the whole thing takes, and which specific chain of work
determines that.

The graph is not wrong. It is answering a question nobody asked. "Which assemblies
reference which" is a fact about how somebody organised project files in 2007.

## Why that graph is the one you get

Because it is the cheapest one to compute. Every .NET assembly has an AssemblyRef table
in its manifest listing the assemblies it names. Reading it takes microseconds and
requires decoding no method bodies at all:

```csharp
Assembly.LoadFrom(path).GetReferencedAssemblies()
```

One line. That is why every tool does it, from `dotnet list package` to NDepend's default
view to the PowerShell script somebody wrote in an afternoon for the assessment.

The cost of that convenience is that the entire industry's mental model of legacy estates
is built on the coarsest measurement available. And it is not merely imprecise. It is
systematically wrong in a specific, measurable direction, which is what this project set
out to demonstrate.

## The four questions and why the assembly is wrong for all of them

**"What blocks the port?"** The manifest tells you an assembly references `System.Web`.
It does not tell you whether that reference is `HttpContext.Current`, which is a design
problem, or `HttpUtility.UrlEncode`, which is a package reference. In the estate I built
for this project, the correlation between manifest reference count and actual blocker
severity is Kendall tau 0.567 -- close enough to look like agreement. Then you find the
two assemblies that reference *nothing but mscorlib* and contain `BinaryFormatter`, which
is the single most severe category of blocker there is. `BinaryFormatter` lives in
mscorlib. Every assembly on earth references mscorlib. The cheap scan is structurally
blind to the worst finding.

**"What is entangled?"** A cycle in the assembly graph means those assemblies must ship
together. It says nothing about whether the *code* is entangled. In this estate, three
multi-assembly cycles cover 10 of 24 assemblies -- 42% "in a cycle" on the slide. At type
level, 5 of the 19 types involved are actually in a cycle: 26%. One of the three cycles
contains no type-level cycle whatsoever. It is three assemblies that were split along the
wrong seam, and moving one type dissolves it with no code change at all.

**"What is hard?"** Assessments rank assemblies by their own blocker count. But an
assembly inside a cycle cannot be migrated alone, so its real difficulty is its whole
unit's difficulty. Kendall tau between the two rankings is 0.635, and the assembly with
the worst own score is not on the critical path. You would sequence the project around
the wrong thing.

**"What can we delete?"** This one is the most interesting, because the honest answer is
not a number, it is a question about what you are willing to assume. Call-graph
reachability says 6 types are dead. Two of those are constructed by reflection from
string literals sitting in the same directory. Honouring literal reflection gives 4.
Honouring a string prefix recovered from IL gives 3. A fully sound analysis -- one that
admits it cannot predict where `Type.GetType(prefix + name)` lands -- gives **zero**, and
it is correct. One unconstrained reflection site in reachable code and nothing in the
process can be proven dead.

## Why this is worth building rather than writing about

All of the above is arguable. Someone could reasonably say "yes, but in practice the
assembly graph is good enough". The only way to settle it is to measure -- and to measure,
you need an estate where you know the answer in advance.

So the project builds one. `CorpusBuilder` uses Mono.Cecil to *emit* 24 real .NET
Framework assemblies: a 2005-era claims platform with a Web Forms front end, a WCF service
host, a COM+ component and an MSMQ integration. They reference `System.Web` 4.0.0.0 with
the correct public key token. None of those Framework assemblies are installed on the
machine that builds them, which is the whole point -- metadata does not require the thing
it names to exist.

Then `IlReader` opens that directory with a resolver that refuses every request and
rebuilds everything from IL. It has never seen the specification. Three of the assemblies
are emitted without symbols, because "we lost the source for this one" is not an exotic
case in Framework estates, it is Tuesday.

`CorpusSpec` declares what was planted. `CorpusIntegrityTest` re-derives every planted
fact from the emitted bytes. The corpus does not mark its own homework.

## The format

`docs/results.md` is generated by the code. Before it measures anything it writes down
twelve predictions -- the things a competent engineer would expect walking into this
estate. Then it measures, and settles each one HELD or CONTRADICTED.

Nine were contradicted.

The report will not render while a prediction is unsettled. `Report.Render()` throws.
That is not decoration; it is the mechanism that makes the format honest. There is no code
path that quietly drops a prediction that came out badly, because I wrote the class before
I knew which ones would.
