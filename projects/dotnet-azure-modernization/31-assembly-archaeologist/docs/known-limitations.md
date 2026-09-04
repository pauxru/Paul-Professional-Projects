# Known limitations

Written so that nobody has to discover these by being wrong in front of a client.

## The IL is simplified

The corpus emits call sites keyed on **declaring type plus member name, with no
signature**. Overloads collapse: `Serialize(Stream, object)` and `Serialize(object)` are
the same edge and the same blocker.

For the questions this project asks -- graph shape, blocker presence, reachability -- this
changes nothing, because none of them are sensitive to arity. It would matter for a tool
that hands a human a list of call sites to edit, and for any rule that is genuinely
overload-specific (there are a few in the real porting catalogue; there are none in this
table).

## Prefix recovery is a pattern match, not a string solver

`IlReader.RecoverPrefix` recognises exactly one shape:

```
ldstr "Contoso.Claims.Plugins."
ldarg / ldloc / call ...
call string System.String::Concat(string, string)
call class System.Type System.Type::GetType(string)
```

It reads the literal operand of an `ldstr` that feeds a `Concat` that feeds a
`GetType`. That is a two-instruction dataflow, not an analysis.

It is **unsound if the prefix is itself computed**. `Type.GetType(cfg.Namespace + "." + name)`
has no `ldstr` to recover, so the site correctly falls through to "unconstrained" -- but
`Type.GetType(BuildPrefix() + name)`, where `BuildPrefix` returns a constant, would also
fall through, and the analysis would be needlessly pessimistic rather than wrong. The
dangerous direction -- claiming a prefix that is not actually constant -- requires the
`ldstr` operand to be reassigned between the load and the concat, which the CLI
verification rules make difficult but not impossible in unverifiable IL.

A production version wants a real constant-propagation pass over the method body, or a
string solver. What is here is the cheapest thing that demonstrates the point: recovering
one prefix takes the sound answer from 0 deletable types to 3.

## Reflective instantiation is over-approximated

When a reflection site resolves to a type, **every method on that type** is marked
reachable. In reality `Activator.CreateInstance(t)` calls a constructor, and whatever the
caller does with the resulting object is a separate question the analyser is not tracking.

This is deliberately the safe direction -- it can only make the live set larger, never
smaller, so it cannot cause a deletion that should not happen. It does mean the "live
methods" count is an over-estimate, and no conclusion in the report depends on that count
being tight.

## No inheritance, no interfaces, no generics

Because the analyser never resolves references (ADR 002), it cannot see base types across
assembly boundaries, cannot resolve interface dispatch to implementations, and does not
decode generic instantiations.

Practical consequences on a real estate:

- A Web Forms page found by `: Page` inheritance rather than by calling a `Page` member
  would be missed. In practice code-behind always calls *something*, so this is less
  severe than it sounds, but it is a real gap.
- `IFoo.Bar()` produces an edge to the interface member, not to the implementations. On
  a heavily interface-mediated codebase the call graph will be sparser than reality and
  the dead-code analysis correspondingly more optimistic -- the *unsafe* direction.
- Virtual dispatch has the same problem. `callvirt` on a base member does not produce
  edges to overrides.

A tool intended to drive actual deletions needs a class-hierarchy analysis pass and must
accept the reproducibility cost of resolving. This one is intended to drive a plan.

## What the analyser cannot read at all

- **Obfuscated assemblies.** Renamed types defeat name-based rule matching entirely. The
  graph still comes out, but the blockers do not.
- **Mixed-mode C++/CLI assemblies.** Native code is invisible; the managed surface reads
  fine and the native half does not exist as far as this is concerned. Framework estates
  frequently contain one of these and it is frequently the hard part.
- **NGen / crossgen images.** Cecil reads the IL if it is still present, and a fully
  AOT-compiled image may not have it.
- **Anything referenced only from configuration.** A type named in `web.config` and never
  mentioned in IL is invisible. This is the single most common source of "we deleted it
  and production broke" in real modernisation work, and no IL analysis can fix it. The
  `FullySound` mode exists to make the shape of that risk explicit rather than to solve it.

## The corpus is a model, not a sample

24 assemblies, 42 types, 95 methods. A real Framework estate is 200-800 assemblies with
tens of thousands of types. The *phenomena* modelled here are the ones I have repeatedly
seen -- packaging-only cycles, a mscorlib-only manifest hiding a `BinaryFormatter`, an
assembly nobody can rebuild sitting at the bottom of the dependency graph -- but their
*proportions* are chosen to make each one visible in a report a human will read, not
sampled from anything.

Specifically: 3 of 24 assemblies without symbols (12.5%) is optimistic for a 2005-era
estate, and the 6-wave critical path is short because the estate is small.

## Ordinal comparison is assumed, not enforced end to end

`DiGraph` uses `StringComparer.Ordinal` throughout, and a mutation that replaces it with
the default comparer **survives the test suite**. It survives for a real reason: every
identifier in this corpus is ASCII, and invariant ordering agrees with ordinal on ASCII.

This is a latent portability bug rather than a hole in the suite. It would surface on a
corpus containing a Turkish dotless 'i' or a Unicode identifier in a type name, which
.NET permits and which no test here generates. Recording it rather than adding a test
that pretends to catch it: writing a corpus with a Turkish 'I' purely to kill a mutant
would be testing the test, and the honest statement is "this is ordinal by construction
and unenforced by measurement".

## Scale

`Graphs.StronglyConnectedComponents` is iterative and tested to 200,000 nodes, so the
graph layer will not fall over on a real estate. The two exact algorithms will:

- `FeedbackArcSet.Exact` refuses above 20 nodes (`O(2^n * n)`).
- `CycleDissolver` refuses above 22 types (subset search).

Both refuse rather than degrade (ADR 003). On a real estate with a 40-assembly strongly
connected component -- which happens -- you get the greedy cut and an honest "no exact
answer available", not a wrong number.
