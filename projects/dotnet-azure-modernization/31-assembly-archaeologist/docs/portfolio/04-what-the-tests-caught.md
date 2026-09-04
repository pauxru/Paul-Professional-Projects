# What the tests caught

206 tests. Seven of them found real defects in code I had already convinced myself was
correct, and one of them found a defect in my own understanding of the corpus before a
single line of the analyser existed.

This is that list, in the order it happened.

---

## 1. My hand-drawn ground truth was wrong

**Before any test existed.** I designed the corpus with three small two-assembly cycles,
planted deliberately so the analyser would find exactly three tidy components.

Then I hand-traced the edge graph and found they interlock:
`Config -> Security -> Data -> Audit -> Logging -> Config`. Three "small independent
cycles" are in fact **one five-assembly strongly connected component**.

I could have fixed the corpus. I restructured the ground truth around the better finding
instead, because the accidental version is the one that actually happens: nobody plants a
five-assembly cycle, they plant three small ones and the small ones merge. `PlantedIrreducibleCycles`
and `PlantedDissolvingCycles` became `PlantedTypeLevelKnots` and `PlantedPackagingOnlyCycles`,
which is a better vocabulary anyway -- it describes the *property that matters* rather than
the shape I happened to draw.

This is why `CorpusIntegrityTest` exists at all. The lesson is not "check your work"; it
is that **a corpus that declares its own answers will encode your misconceptions along
with your intentions**, and only re-deriving them from the artefact catches it.

---

## 2. The analyser could only run once per process

`IlReader.Read` worked. Called twice, it worked. Called twice with a directory delete in
between, it threw `IOException: The process cannot access the file`.

Cecil memory-maps the files it reads. `ModuleDefinition.ReadModule` holds the DLL open
until disposed, and I was never disposing. The fix is a `try/finally` around a `Read`/`Build`
split.

What makes this worth recording is the *shape* of the failure. In the CLI it never
surfaced -- one process, one read, exit. It only appears when something calls the analyser
twice, which is exactly what a test fixture does and exactly what a service would do. A
tool that works perfectly in the demo and fails on the second request is a specific,
familiar, expensive category of bug.

`ReadingTheSameDirectoryTwiceInOneProcessWorks` and `TheReaderDoesNotHoldTheFilesOpenAfterItReturns`
pin it.

---

## 3. Four of the "missing" Framework assemblies were not missing

I wrote `TheFrameworkAssembliesTheEstateNeedsAreNotInstalled` to assert the obvious: seven
Framework assemblies referenced, seven unresolvable.

```
Assert.Equal() Failure: Values differ
Expected: 7
Actual:   3
```

`System.Web`, `System.Data`, `System.Drawing` and `System.Configuration` all resolve on
.NET 10 -- to empty type-forwarding facades, version 4.0.0.0, containing one type each and
none of the types the estate calls.

This is the most valuable thing any test found in this project, because it inverts the
risk. I had assumed resolution would fail *loudly* and that avoiding it was a matter of
convenience. In fact resolution **succeeds quietly and wrongly**, and the wrongness varies
by machine: install `Microsoft.Windows.Compatibility` and you get different answers.

The replacement test, `ResolvingTheFrameworkReferencesWouldMisleadRatherThanFail`, reads
the TypeRef table out of the emitted DLLs, resolves each external assembly, and asserts
that **none of the types the estate names are present in what resolved**. Its partner,
`TheReaderNeverAsksToResolveAnything`, passes a recording resolver and asserts zero
requests -- because tolerating a hostile resolver is not the invariant; never calling one
is.

ADR 002 exists because of this test.

---

## 4. The wave arithmetic in my head was wrong, not the code

`AWaveIsOneMoreThanTheDeepestThingItDependsOnNotOneMoreThanTheShallowest` failed on first
run. I had written `deep = 4, top = 5`; the answer is `deep = 3, top = 4`.

The code was right. I include this because a test suite where every failure is a code
defect is a suite whose assertions were written by reading the implementation. Getting
the arithmetic wrong *in the test* is evidence the expectation was derived independently,
which is the only way an assertion is worth anything.

---

## 5. Edge weight was being discarded above the method level

`EdgeWeightIsTheNumberOfCallSitesNotTheNumberOfDistinctCallees` failed because no method
edge in the corpus had weight greater than 1 -- the corpus never repeated a call site. I
added a repeated call (`Repository::Load` opens a connection twice, once on the retry
path, which is what real code looks like).

That exposed the actual bug. `LiftFrom` counted **edges**:

```csharp
counts[k] = counts.GetValueOrDefault(k) + 1;      // wrong
counts[k] = counts.GetValueOrDefault(k) + e.Weight; // right
```

So a type edge's weight was "how many method pairs are behind this", not "how many call
sites". The reader carefully counted call sites and the first lift threw the count away.

This matters because `FeedbackArcSet` minimises weight, and weight is supposed to answer
"what would this cut cost me". A call site is a place a human goes and changes something.
That is a unit you can defend in a planning meeting. "Number of distinct method pairs" is
not.

---

## 6. My cycle-dissolution check was weaker than the dissolver

`NoSmallerSetOfTypesWouldHaveWorkedWhichIsWhatMakesItAdvice` reported that the dissolver
was returning 3 types when 2 would do.

The dissolver was right. My verification was wrong. I was checking "do two of the unit's
original assemblies still sit in one component?" -- which ignores a cycle between the
**new extracted assembly** and an old one. Extracting two types left `Extracted <-> Audit`,
still a build cycle, and my check called it dissolved.

The fix is to induce the graph on `unit.Assemblies + Extracted` and require acyclicity
there. Worth recording because the failure mode of a *verification* being weaker than the
thing it verifies is the one that makes a green suite meaningless, and it presented here
as an accusation against correct code.

---

## 7. The rule table's headline feature was never exercised

`AMemberSpecificRuleBeatsATypeWideOneOnTheSameType` searched the table for any type
carrying both a type-wide and a member-specific rule, and found none.

The precedence logic in `BlockerRules.Match` existed, was correct, and was tested against
nothing. Member precision is the entire argument for why reference counting is inadequate
(ADR 004) and no data in the table demonstrated it.

The wrong fixes were available and tempting: delete the test, or invent a fake pair. What
I did instead was look for a case where the distinction is *genuinely true*.
`System.Drawing.Bitmap` is a `Rewrite` -- swap in ImageSharp and move on. `Bitmap.GetHbitmap`
returns a Win32 `HBITMAP`, and a caller using it is not using an imaging library, it is
using Windows. No swap helps. Same type, opposite verdicts, and only the member name
distinguishes them.

---

## 8. The CLI had been writing the report to whatever directory it was invoked from

`ReportFreshnessTest` failed with `Assert.NotNull() Failure` in its repo-root helper. The
marker file I searched for was `AssemblyArchaeologist.sln`. The actual file is
`AssemblyArchaeologist.slnx`.

The test's helper returned null and failed. The CLI's identical helper had this fallback:

```csharp
return d?.FullName ?? Directory.GetCurrentDirectory();
```

So the CLI had *never* found the repo root. It worked entirely by accident, because I
always ran it from the project directory. Run it from anywhere else and it writes
`docs/results.md` into a random folder and reports success.

Both were fixed, and the CLI now throws:

```csharp
return d?.FullName ?? throw new InvalidOperationException(
    $"could not find AssemblyArchaeologist.slnx above {AppContext.BaseDirectory}");
```

This is ADR 003's principle -- refuse rather than degrade -- arriving as a bug rather than
a decision. A fallback that silently substitutes a plausible wrong answer is exactly the
failure mode this project is *about*, and I had written one into the tool that argues
against it.

---

## The mutation stage

`test.ps1` stage 5 applies seven single-token edits to the source, each removing a
guarantee some number in `results.md` depends on, and requires the suite to go red for
every one:

| mutation | what it destroys |
|---|---|
| `+ e.Weight` -> `+ 1` | cut cost stops counting call sites |
| member lookup -> `false` | member precision collapses to type precision |
| `onStack.Contains` -> `index.ContainsKey` | Tarjan invents entanglement across cross-edges |
| `n > maxNodes` -> `n > int.MaxValue` | the exact solver stops refusing |
| `mode ==` -> `mode !=` in Reachability | the soundness lattice inverts |
| extraction edge filter | "move one type" becomes a guess |
| `unsettled.Count > 0` -> `> 99` | failed predictions can be dropped silently |

7 of 7 killed.

## The mutation that survives, and why it is staying in the documentation

Replacing `StringComparer.Ordinal` in `DiGraph` with the default comparer **survives the
suite**. Every identifier in this corpus is ASCII, and invariant ordering agrees with
ordinal on ASCII, so the mutant is behaviourally identical *on this input*.

This is a latent portability bug, not a hole in the suite. It would surface on a corpus
containing a Turkish dotless 'i' in a type name -- which .NET permits and which this
corpus has no reason to contain.

I could kill it by adding such a type. I did not, because that test would exist purely to
kill a mutant rather than to check a property anyone cares about, and I would then be
testing the test. It is written up in `known-limitations.md` instead, phrased honestly:
ordinal comparison here is **correct by construction and unenforced by measurement**.

"The mutation survived" and "the mutation could not possibly have changed anything" are
different findings, and only one of them is a problem. Recording which is which is more
useful than a perfect score.
