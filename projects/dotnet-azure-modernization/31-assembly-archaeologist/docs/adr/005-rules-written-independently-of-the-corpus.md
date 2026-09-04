# ADR 005: The rule table is written independently of the corpus

## Status

Accepted.

## Context

`BlockerRules.All` is the only place in this codebase that encodes knowledge from outside
it: which .NET Framework APIs survive the move to modern .NET, and how badly they do not.
Everything else is derived from bytes.

There is an obvious way to build that table, and it is a trap. Write the corpus first,
then write rules for the APIs the corpus uses. The tests pass immediately, the report has
a satisfying number of blockers, and the whole thing measures nothing at all -- it is a
lookup table checked against the list it was copied from.

## Decision

The rule table was written from the .NET porting documentation before the corpus body IL
was written, and it contains rules the corpus does not exercise. Two properties follow
from that and are pinned by tests:

**The table is broader than the corpus.** `BlockerRulesTest` asserts on the table's own
shape -- all three severities present, no rule marked `None`, every rule carrying a reason
distinct from its guidance, no duplicate keys, at least six distinct namespaces -- without
reference to any estate. `Bitmap.GetHbitmap` is in the table and the corpus never calls
it.

**The corpus is broader than the table.** `CorpusIntegrityTest` checks that the estate
calls Framework APIs that are *not* blockers, so that a rule table which flagged
everything would fail. `AppDomain.CurrentDomain` is in the corpus specifically because it
must not match.

## The severity model

Three levels, ordered by how much of the fix is a decision rather than a change:

| | Meaning | Example |
|---|---|---|
| `Rewrite` | Compiles and runs after a mechanical change | `ConfigurationManager` -> `IConfiguration` |
| `PlatformNotSupported` | Compiles, throws at runtime | `Thread.Abort`, `AppDomain.CreateDomain` |
| `NoEquivalent` | The design has to change | `BinaryFormatter`, `ServicedComponent` |

`PlatformNotSupported` sits in the middle deliberately, and it is the one that surprises
people. Those APIs are *present in the surface area of modern .NET*. The compiler is
silent. The build is green. The failure arrives at runtime, on the error path, in
production -- which is where `Thread.Abort` lives in most Framework code. A porting
assessment that only counts compile errors misses this category entirely, which is why it
is a category.

## Member precision

Rules are keyed on namespace + type + *optional* member, and `Match` gives member-specific
rules precedence over type-wide ones. This is the mechanism that makes the argument in
ADR 004 concrete:

```csharp
BlockerRules.Match("System.AppDomain", "CreateDomain")      // PlatformNotSupported
BlockerRules.Match("System.AppDomain", "get_CurrentDomain") // null
BlockerRules.Match("System.Drawing.Bitmap", "Save")         // Rewrite
BlockerRules.Match("System.Drawing.Bitmap", "GetHbitmap")   // NoEquivalent
```

The `Bitmap` pair was added because a test found the gap. `AMemberSpecificRuleBeatsATypeWideOneOnTheSameType`
originally searched the table for any type carrying both kinds of rule, and found none --
so the precedence logic existed but nothing exercised it against real data. Rather than
delete the test or fake a rule, I looked for a case where the distinction is genuinely
true. `Bitmap.GetHbitmap` returns a Win32 `HBITMAP`: a caller using it is not using an
imaging library, it is using Windows, and no cross-platform swap helps. Same type,
different answer, and only the member says which.

## Consequences

**Good.** The blocker counts in `results.md` are a measurement rather than a restatement.
17 call sites across 13 assemblies with a total severity of 39, distributed 5 `Rewrite` /
2 `PlatformNotSupported` / 10 `NoEquivalent`, is a fact about the estate.

**Good.** The table is portable. Point `IlReader` at a customer's `bin` directory and the
rules apply unchanged.

**Bad.** Fifteen rules is a demonstration, not coverage. A production version needs the
full .NET portability catalogue -- hundreds of entries -- and ideally the API Port
compatibility data as a source rather than hand-written entries. The table's *shape* is
the contribution here; its contents are a sample.

**Bad.** No signature matching, so overloads collapse. If `Serialize` is a blocker, every
`Serialize` on that type is a blocker. For porting rules this is nearly always what you
want -- portability is a property of the member, not the arity -- but it is a real
limitation and it is documented.
