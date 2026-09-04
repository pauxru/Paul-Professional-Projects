# ADR 001: Author the corpus with Cecil rather than collect one

## Status

Accepted.

## Context

This project needed a .NET Framework estate to analyse. There were three options.

**Find a real one.** Open-source Framework-era codebases exist, but they are either
small enough to be uninteresting or large enough that no ground truth can be established.
More importantly, a real estate cannot answer "did the analyser find the cycle?" -- only
"did the analyser find *a* cycle?". Without ground truth, every measurement in the report
would be an assertion.

**Write source and compile it.** Straightforward, but it defeats the premise. The whole
argument for IL-level analysis is that on real modernisation work you do not have source.
If the corpus is built from source, the corpus is not modelling the problem. It also
requires the Framework reference assemblies to be installed to compile against
`System.Web`, which they are not, and which is itself the situation being modelled.

**Emit the assemblies directly with Mono.Cecil.** Untested, and the make-or-break
question was whether Cecil can author an assembly that references `System.Web 4.0.0.0`
when `System.Web` is not installed anywhere on the machine.

## Decision

Emit the corpus with Cecil.

I ran a spike before committing to this, because the whole design collapses if the answer
is no. It is yes:

```csharp
var sysWeb = new AssemblyNameReference("System.Web", new Version(4, 0, 0, 0));
var httpContext = new TypeReference("System.Web", "HttpContext", module, sysWeb);
```

writes real IL with correct AssemblyRef, TypeRef and MemberRef scopes, and reads back
correctly. Metadata does not require the thing it names to exist. That is not a Cecil
quirk -- it is the property of ECMA-335 that makes legacy IL analysis possible at all.
An assembly is a set of *names*, resolved at load time by a runtime that may or may not
be there.

Two further facts made this cheap:

- `AssemblyDefinition.CreateAssembly` defaults its corlib reference to `mscorlib 4.0.0.0`,
  which is exactly the Framework-era manifest you want, with no configuration.
- Cycles are emittable in two passes: create every `AssemblyDefinition`, `TypeDefinition`
  and `MethodDefinition` first, then fill method bodies using `module.ImportReference`
  against the in-memory definitions. A single-pass emitter cannot express `A -> B -> A`.

## Consequences

**Good.** The corpus has exact ground truth. `CorpusSpec` declares what was planted --
which types are knotted, which assemblies are statically unreachable, which reflection
sites are undecidable -- and `CorpusIntegrityTest` re-derives all of it from the emitted
bytes using code that has never seen the spec. When a number in the report is wrong, a
test fails rather than a reader noticing.

**Good.** The estate can be shaped to contain the phenomena worth measuring. A packaging-
only cycle, a genuine type-level knot, an assembly whose only manifest reference is
mscorlib but which contains a `NoEquivalent` blocker -- these all exist in real estates
and none of them would reliably appear in a corpus I found by accident.

**Good, and unexpected.** Authoring the corpus surfaced a fact I would not have predicted
and which changed the reader's design: four of the seven Framework assemblies the estate
references *do* resolve on a modern machine, to empty facades. See ADR 002.

**Bad.** The IL is simplified. Call sites are keyed on declaring type plus member name,
with no signature, so overloads collapse. This is documented in `known-limitations.md`.
It does not affect any conclusion in the report -- every conclusion is about the shape of
the graph, and overload collapse does not change graph shape -- but it would matter for a
tool that reported call sites to a human for editing.

**Bad.** Cecil memory-maps the files it reads, so `ModuleDefinition.ReadModule` holds the
DLL open until disposed. The first version of `IlReader` could analyse a directory exactly
once per process, and then failed with an `IOException` on cleanup. The fix is a
`try/finally` that disposes every module; the symptom was obscure enough to be worth a
test of its own (`ReadingTheSameDirectoryTwiceInOneProcessWorks`).

## Alternatives reconsidered

If this were a product rather than a demonstration, the corpus would be a *test fixture*
and the analyser would run against customer binaries. Nothing about the analyser assumes
the corpus: `IlReader.Read` takes a directory path. The corpus exists so that the numbers
in `results.md` can be checked, not because the tool needs it.
