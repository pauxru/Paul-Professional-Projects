# ADR 002: The analyser never resolves an assembly reference

## Status

Accepted.

## Context

Mono.Cecil resolves assembly references by default. `ModuleDefinition.ReadModule` with
default `ReaderParameters` attaches a `DefaultAssemblyResolver`, which searches the
application base directory and, on .NET Framework, the GAC. When a `TypeReference` is
dereferenced, Cecil follows it.

The obvious design is to let it. Resolution gives you real `TypeDefinition` objects,
proper inheritance chains, and the ability to answer questions like "does this type
implement `ISerializable`?" that name-matching cannot.

My initial assumption was that resolution would simply fail on a Framework estate --
`System.Web` is not installed, so `AssemblyResolutionException`, so fall back to names.
A test written to pin that assumption failed:

```
Expected: 7
Actual:   3
```

Only three of the seven Framework assemblies the estate references failed to resolve.

## What is actually happening

The .NET 10 shared framework ships type-forwarding facades named `System.Web.dll`,
`System.Data.dll`, `System.Drawing.dll` and `System.Configuration.dll`, all versioned
4.0.0.0. They exist so that old assemblies can load. They contain almost nothing:

```
RESOLVED System.Web           -> 4.0.0.0  types=1  C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.11\System.Web.dll
RESOLVED System.Configuration -> 4.0.0.0  types=1  ...\System.Configuration.dll
RESOLVED System.Data          -> 4.0.0.0  types=1  ...\System.Data.dll
RESOLVED System.Drawing       -> 4.0.0.0  types=1  ...\System.Drawing.dll
ABSENT   System.EnterpriseServices
ABSENT   System.Messaging
ABSENT   System.ServiceModel
```

`System.Web` resolves. It does not contain `HttpContext`. `System.Configuration` resolves
and does not contain `ConfigurationManager` -- that type lives in the
`System.Configuration.ConfigurationManager` NuGet package, not in the facade.

So a resolving analyser, run on this estate on this machine, would not fail loudly. It
would resolve `System.Web`, look for `HttpContext`, not find it, and take whichever branch
its author wrote for "unresolved type" -- most likely skipping the call site silently.
The most severe blocker in an ASP.NET Web Forms estate would be reported as clean.

And the behaviour is machine-dependent. Install the
`Microsoft.Windows.Compatibility` package and the resolver finds different assemblies.
Run on a machine with a Framework GAC and it finds different assemblies again. The
analysis result would depend on the analyst's laptop.

## Decision

`IlReader` reads every module with a `NullResolver` that throws on every request, and the
analysis never dereferences a `TypeReference`. Blocker matching is done on the fully
qualified name in the TypeRef table plus the member name in the MemberRef table. Nothing
else.

`IlReader.Read` takes an optional `IAssemblyResolver` purely so that a test can pass a
recording resolver and assert that **zero** resolution requests are made
(`TheReaderNeverAsksToResolveAnything`). Tolerating a hostile resolver is not enough;
never calling one is the invariant.

## Consequences

**Good.** The analysis is a pure function of the bytes in the directory. Same DLLs, same
answer, on any machine, forever. This is the property that makes `results.md`
byte-reproducible across three separate processes in `test.ps1`.

**Good.** It works on estates whose dependencies genuinely cannot be obtained -- a
third-party assembly from a vendor that no longer exists, a strong-named internal library
whose build machine was decommissioned. This is the normal case in modernisation work,
not the exotic one.

**Bad.** No inheritance analysis. The analyser cannot answer "does this type derive from
`ServicedComponent`?", only "does this type call something on `ServicedComponent`?". For
the questions this project asks -- what blocks, what is entangled, what is dead -- calls
are sufficient, and the rule table is written accordingly. A tool that needed to find
every Web Forms page by base type rather than by call would need a different design, and
would have to accept the reproducibility cost.

**Bad.** Overload resolution is impossible without signatures, so `Match` is keyed on
declaring type plus member name. `BinaryFormatter::Serialize` matches regardless of
arity. In practice the porting rules are about *members*, not overloads -- if
`Serialize` is a blocker, every `Serialize` is a blocker -- but it is a real limitation
and it is in `known-limitations.md`.

## The general lesson

The failure mode that nearly slipped through here was not "the tool errors out". It was
"the tool succeeds and is wrong, in a way that varies by machine". Those are the
expensive ones, and the only reason this one was caught is that a test asserted the
*number* of unresolvable assemblies rather than merely that the analysis completed.
