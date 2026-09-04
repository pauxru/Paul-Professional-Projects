# Assembly Archaeologist -- what 24 compiled assemblies will admit under questioning

Every number in this document was produced by running the analyser over a directory of DLLs. No source was read. No reference assembly was resolved -- `System.Web`, `System.ServiceModel` and `System.EnterpriseServices` are not installed on the machine that produced this, and were never needed. Metadata does not require the thing it names to exist, which is the only reason legacy estates are analysable at all.

## 1. What the artefacts admit

| measure | value |
|---|---|
| assemblies | 24 |
| types | 42 |
| methods | 95 |
| call edges (method level) | 65 |
| call edges (type level) | 59 |
| call edges (assembly level) | 48 |
| external assemblies referenced | 8 |
| assemblies with no PDB | 3 |

The external references are the estate's era, stated in its own manifest:

```
System.Configuration
System.Data
System.Drawing
System.EnterpriseServices
System.Messaging
System.ServiceModel
System.Web
mscorlib
```

Three assemblies arrive without symbols -- Contoso.Claims.Integration.Mainframe, Contoso.Common.Logging, Contoso.Common.Utils. In the story these are the ones whose source went with the 2011 SAN failure. Nothing below treats them differently during analysis, and everything below treats them differently during planning.

## 2. What stops it moving

| severity | call sites | meaning |
|---|---|---|
| Rewrite | 5 | compiles after a package or config change; behaviour must be re-tested |
| PlatformNotSupported | 2 | compiles, throws at run time, and the compiler will not warn you |
| NoEquivalent | 10 | no modern equivalent; the design has to change |

| assembly | wave | sites | score | worst API |
|---|---|---|---|---|
| Contoso.Claims.Web | 5 | 3 | 7 | System.Web.HttpContext |
| Contoso.Common.Utils | 1 | 2 | 6 | System.Runtime.Serialization.Formatters.Binary.BinaryFormatter |
| Contoso.Claims.Batch | 5 | 2 | 5 | System.Runtime.Remoting.RemotingConfiguration |
| Contoso.Claims.Integration.Guidewire | 3 | 1 | 3 | System.EnterpriseServices.ServicedComponent |
| Contoso.Claims.Integration.Mainframe | 2 | 1 | 3 | System.Messaging.MessageQueue |
| Contoso.Claims.Notifications | 3 | 1 | 3 | System.Messaging.MessageQueue |
| Contoso.Claims.Security | 2 | 1 | 3 | System.Web.Security.FormsAuthentication |
| Contoso.Claims.WebServices | 5 | 1 | 3 | System.ServiceModel.ServiceHost |
| Contoso.Claims.Scheduler | 4 | 1 | 2 | System.Threading.Thread::Abort |
| Contoso.Claims.Data | 2 | 1 | 1 | System.Data.SqlClient.SqlConnection |
| Contoso.Claims.Documents | 3 | 1 | 1 | System.Drawing.Bitmap |
| Contoso.Claims.Reporting | 3 | 1 | 1 | System.Drawing.Bitmap |
| Contoso.Common.Config | 2 | 1 | 1 | System.Configuration.ConfigurationManager |

**P1 -- expected.** The unportable APIs are at the edge of the system. Presentation, hosting and integration code is where a Framework estate touches things that were never ported; the domain and utility layers underneath should be portable already.

**P1 -- CONTRADICTED.** 8 assemblies contain an API with no modern equivalent, and they span waves 1 to 5 of 6. The deepest is `Contoso.Common.Utils` at wave 1 -- a leaf that everything is built on -- and its blocker is `System.Runtime.Serialization.Formatters.Binary.BinaryFormatter`, which is not merely unported but removed. It is also one of the assemblies with no source. The hardest problem in this estate is at the bottom of it, in code nobody can rebuild.

**P2 -- expected.** Reading the manifest is enough. An assembly's reference list names every external assembly it touches, so counting Framework references -- which needs no IL at all -- identifies the assemblies in trouble.

**P2 -- CONTRADICTED.** Kendall tau between manifest reference count and blocker severity is 0.567, which sounds usable until you look at what it misses. 2 assemblies reference nothing but `mscorlib` and still contain an API with no modern equivalent: `Contoso.Claims.Batch`, `Contoso.Common.Utils`. `System.AppDomain::CreateDomain` lives in the core library, so the manifest of the assembly holding the estate's worst blocker is indistinguishable from that of a pure-domain assembly with nothing wrong with it. A manifest scanner cannot see the difference because the difference is not in the manifest.

## 3. Cycles: what the graph says, and what the code says

**P3 -- expected.** There is no migration order. Estates of this age always contain assembly cycles, so a topological sort of the dependency graph does not exist and the plan cannot simply be read off the graph.

**P3 -- HELD.** 3 strongly connected components contain more than one assembly, covering 10 of 24 assemblies. `TopologicalOrder` throws on the raw assembly graph, which is the correct behaviour and the reason the rest of this section exists.

**P4 -- expected.** The cycles are a local problem. Most assemblies in an estate this size sit in no cycle at all, so most of the plan can be read straight off the graph and only a minority needs the expensive treatment.

**P4 -- HELD.** 14 of 17 units are single assemblies, covering 14 of 24 assemblies (58.33%). The tangle is real but bounded: 3 units need the analysis in sections 4 and 5, and the other 14 need only an ordering.

| unit | assemblies | types | type-level knots | knotted types |
|---|---|---|---|---|
| Contoso.Claims.Audit | 5 | 8 | 1 | 2 |
| Contoso.Claims.Documents | 3 | 7 | 1 | 3 |
| Contoso.Claims.Rules | 2 | 4 | 0 | -- |

**P5 -- expected.** An assembly cycle means the code is entangled. If A and B depend on each other, the types inside them depend on each other, and separating them is a rewrite.

**P5 -- CONTRADICTED.** 10 assemblies are in cycles and they contain 19 types, but only 5 of those types actually participate in a cycle -- 26.32% of them. The largest unit, `Contoso.Claims.Audit`, ties 5 assemblies together on the strength of 2 types. 1 unit contains no type-level cycle at all: the cycle exists purely because of which types were put in which project file.

### unit `Contoso.Claims.Audit`

- `Contoso.Claims.Audit`
- `Contoso.Claims.Data`
- `Contoso.Claims.Security`
- `Contoso.Common.Config`
- `Contoso.Common.Logging`

Type-level knot: `Contoso.Claims.Security.PrincipalCache` -> `Contoso.Common.Config.ConfigStore` -> `Contoso.Claims.Security.PrincipalCache`

### unit `Contoso.Claims.Documents`

- `Contoso.Claims.Documents`
- `Contoso.Claims.Notifications`
- `Contoso.Claims.Reporting`

Type-level knot: `Contoso.Claims.Documents.Archive` -> `Contoso.Claims.Notifications.Templates` -> `Contoso.Claims.Reporting.ReportBuilder` -> `Contoso.Claims.Documents.Archive`

### unit `Contoso.Claims.Rules`

- `Contoso.Claims.Rules`
- `Contoso.Claims.Workflow`

The induced type graph is acyclic. Nothing in this unit is entangled; the cycle is an artefact of packaging.

## 4. Breaking the cycles: the heuristic and the truth

Minimum feedback arc set -- the cheapest set of dependencies to cut so an order exists -- is NP-hard. The usual response is a heuristic and silence about how good it is. This ships both a heuristic (Eades-Lin-Smyth) and an exact solver (dynamic programming over subsets, refusing rather than degrading past 20 nodes), and reports the gap.

**P6 -- expected.** The heuristic is suboptimal somewhere in this estate. That is why the exact solver exists.

| unit | n | edges | greedy cut/weight | exact cut/weight | gap |
|---|---|---|---|---|---|
| Contoso.Claims.Audit | 5 | 8 | 2 / 2 | 2 / 2 | equal |
| Contoso.Claims.Documents | 3 | 4 | 1 / 1 | 1 / 1 | equal |
| Contoso.Claims.Rules | 2 | 2 | 1 / 1 | 1 / 1 | equal |

**P6 -- CONTRADICTED.** The heuristic matches the exact solver on all 3 units in this estate. That is a fact about the estate, not about the heuristic: the largest unit has 5 nodes, and at that size almost anything is optimal. On random dense digraphs the heuristic starts losing at n=6 and the gap grows steadily after that, which is why the exact solver ships and why it refuses rather than degrades.

| n | trials where greedy lost | mean excess weight | worst excess |
|---|---|---|---|
| 6 | 9/40 | 0.38 | 4 |
| 8 | 23/40 | 1.57 | 8 |
| 10 | 33/40 | 2.10 | 6 |
| 12 | 33/40 | 3.48 | 11 |

## 5. Dissolving the cycles instead of cutting them

**P7 -- expected.** Breaking a cycle means removing a dependency. The output of cycle analysis is a list of edges to delete, and deleting each one is a piece of work someone has to do.

| unit | edges to cut | types to move | code change needed? |
|---|---|---|---|
| Contoso.Claims.Audit | 2 | 3 | yes -- quarantined, not fixed |
| Contoso.Claims.Documents | 1 | 3 | yes -- quarantined, not fixed |
| Contoso.Claims.Rules | 1 | 1 | no |

### `Contoso.Claims.Audit`

moving 3 type(s) into one new assembly linearises the build; the tangle survives inside that assembly and still needs a code change

- `Contoso.Claims.Data.ConnectionFactory` (currently in `Contoso.Claims.Data`)
- `Contoso.Claims.Security.PrincipalCache` (currently in `Contoso.Claims.Security`)
- `Contoso.Common.Config.ConfigStore` (currently in `Contoso.Common.Config`)

### `Contoso.Claims.Documents`

moving 3 type(s) into one new assembly linearises the build; the tangle survives inside that assembly and still needs a code change

- `Contoso.Claims.Documents.Archive` (currently in `Contoso.Claims.Documents`)
- `Contoso.Claims.Notifications.Templates` (currently in `Contoso.Claims.Notifications`)
- `Contoso.Claims.Reporting.ReportBuilder` (currently in `Contoso.Claims.Reporting`)

### `Contoso.Claims.Rules`

moving 1 type(s) into a new assembly removes the cycle; no code changes

- `Contoso.Claims.Workflow.ClaimWorkflow` (currently in `Contoso.Claims.Workflow`)

**P7 -- CONTRADICTED.** Cutting 4 dependency edges and moving 7 types produce the same acyclic result, but they are not the same work. Cutting an edge means finding every call site behind it and inverting a dependency -- the analyser reports edge weights precisely because that cost is not one unit. Moving a type between project files changes no code at all where the unit has no type-level knot, which is true of 1 of 3 units here. Where a knot does exist, extraction does not fix it; it quarantines it into a single assembly so the rest of the build orders, and the tangle becomes one scheduled piece of work instead of a constraint on everything.

## 6. Dead code: four answers, one of them true

| analysis | live types | dead types | dead assemblies | sound? |
|---|---|---|---|---|
| CallsOnly | 36 | 6 | 3 | no |
| DecidableReflection | 38 | 4 | 2 | no |
| PrefixConstrained | 39 | 3 | 1 | yes, if the prefix is constant |
| FullySound | 42 | 0 | 0 | yes |

**P8 -- expected.** Static reachability finds the dead code. Walk the call graph from the entry point; what you do not reach, you can delete.

**P8 -- CONTRADICTED.** Call-graph reachability declares 6 types dead. 2 of them are named as string literals or type tokens elsewhere in the IL and are alive: `Contoso.Claims.Core.LegacyCurrencyTable`, `Contoso.Claims.Plugins.Fraud.FraudScorer`. Deleting on the strength of the call graph would have removed working code, and the evidence that it was working was sitting in the same directory the whole time.

**P9 -- expected.** Reflection makes dead-code analysis worthless. One `Type.GetType` on a configured name and nothing in the process can be proven dead, so the sound answer is useless and everybody ships the unsound one instead.

**P9 -- CONTRADICTED.** The sound-with-no-constraint answer is exactly as useless as predicted: 0 deletable types, because 1 call site builds a type name at run time. But the name is not built from nothing. 1 of them concatenates a constant prefix that is still in the IL -- here, `Contoso.Claims.Plugins.` -- and recovering it narrows "any type in the process" to "any type under that namespace". That takes the sound answer from 0 deletable types to 3, and from 0 deletable assemblies to 1, without giving up soundness. Two instructions of dataflow buy back the entire result.

What each analysis would have you delete:

| analysis | assemblies it says you can delete |
|---|---|
| CallsOnly | Contoso.Claims.Migration.Tools, Contoso.Claims.Plugins.Fraud, Contoso.Claims.Plugins.Subrogation |
| DecidableReflection | Contoso.Claims.Migration.Tools, Contoso.Claims.Plugins.Subrogation |
| PrefixConstrained | Contoso.Claims.Migration.Tools |
| FullySound | (none) |

Reflection sites, as read from IL:

| site | kind | resolves to | recovered prefix |
|---|---|---|---|
| `Contoso.Claims.Reporting.ChartWriter::Draw+IL_000f` | LiteralTypeToken | `Contoso.Claims.Core.LegacyCurrencyTable` | -- |
| `Contoso.Claims.Web.PluginLoader::LoadConfigured+IL_000f` | ComputedTypeName | -- | `Contoso.Claims.Plugins.` |
| `Contoso.Claims.Web.PluginLoader::LoadFraud+IL_0005` | LiteralTypeName | `Contoso.Claims.Plugins.Fraud.FraudScorer` | -- |

## 7. Difficulty does not compose the way it is reported

**P10 -- expected.** Ranking assemblies by their own blocker count gives the order to work in. The worst assembly is the one with the most Framework APIs in it.

| assembly | own score | unit score | unit size | source? | depends on unbuildable? |
|---|---|---|---|---|---|
| Contoso.Claims.Web | 7 | 7 | 1 | yes | yes |
| Contoso.Common.Utils | 6 | 6 | 1 | NO | yes |
| Contoso.Claims.Batch | 5 | 5 | 1 | yes | yes |
| Contoso.Claims.Notifications | 3 | 5 | 3 | yes | yes |
| Contoso.Claims.Security | 3 | 5 | 5 | yes | yes |
| Contoso.Claims.Data | 1 | 5 | 5 | yes | yes |
| Contoso.Claims.Documents | 1 | 5 | 3 | yes | yes |
| Contoso.Claims.Reporting | 1 | 5 | 3 | yes | yes |
| Contoso.Common.Config | 1 | 5 | 5 | yes | yes |
| Contoso.Claims.Audit | 0 | 5 | 5 | yes | yes |
| Contoso.Common.Logging | 0 | 5 | 5 | NO | yes |
| Contoso.Claims.Integration.Guidewire | 3 | 3 | 1 | yes | yes |

**P10 -- CONTRADICTED.** Kendall tau between an assembly's own blocker score and the score of everything it is forced to move with is 0.635. An assembly in a 5-assembly cycle cannot be migrated alone whatever its own score says, so the two rankings disagree on a substantial fraction of pairs. The worst assembly by its own score, `Contoso.Claims.Web` (score 7), is not on the critical path, so improving it does not shorten the schedule at all.

## 8. The constraint nobody costs: assemblies nobody can rebuild

**P11 -- expected.** Missing source is an analysis problem. The assemblies whose source was lost are the ones you cannot say anything about; the rest of the estate can be planned normally.

**P11 -- CONTRADICTED.** Analysis is entirely unaffected: all 3 source-less assemblies were read, their call edges recovered, and 3 blockers found inside them -- including `System.Messaging.MessageQueue`, which is the most severe class of blocker there is. Planning is where it bites: 21 of 24 assemblies (87.50%) transitively depend on something nobody can rebuild, against 13 (54.17%) that contain a blocker of their own. The estate is constrained more by what cannot be recompiled than by what will not compile.

- `Contoso.Claims.Integration.Mainframe` -- wave 2, 1 blocker(s), 7 assemblies depend on it
- `Contoso.Common.Logging` -- wave 2, 0 blocker(s), 18 assemblies depend on it
- `Contoso.Common.Utils` -- wave 1, 2 blocker(s), 8 assemblies depend on it

## 9. The plan

Waves run bottom-up: wave 1 has no unmigrated dependencies and can start immediately. Units inside a wave are independent of each other and can be run in parallel by different people.

| wave | units | assemblies | score | notes |
|---|---|---|---|---|
| 1 | 3 | 3 | 6 | Contoso.Claims.Core; Contoso.Claims.Plugins.Abstractions; Contoso.Common.Utils |
| 2 | 3 | 7 | 8 | Contoso.Claims.Audit (+4 more); Contoso.Claims.Integration.Mainframe; Contoso.Claims.Plugins.Subrogation |
| 3 | 5 | 7 | 8 | Contoso.Claims.Documents (+2 more); Contoso.Claims.Integration.Guidewire; Contoso.Claims.Migration.Tools; Contoso.Claims.Plugins.Fraud; Contoso.Claims.Pricing |
| 4 | 2 | 3 | 2 | Contoso.Claims.Rules (+1 more); Contoso.Claims.Scheduler |
| 5 | 3 | 3 | 15 | Contoso.Claims.Batch; Contoso.Claims.Web; Contoso.Claims.WebServices |
| 6 | 1 | 1 | 0 | Contoso.Claims.Host |

Critical path (6 waves): `Contoso.Claims.Host` <- `Contoso.Claims.Batch` <- `Contoso.Claims.Rules` <- `Contoso.Claims.Documents` <- `Contoso.Claims.Audit` <- `Contoso.Claims.Core`. Every other unit has slack. Work that does not shorten this chain does not shorten the migration.

**P12 -- expected.** The plan parallelises. Migration schedules are set by the longest chain of dependencies, not by the number of assemblies, so the wave count should be far smaller than the assembly count and most waves should hold several independent units.

**P12 -- HELD.** 24 assemblies migrate in 6 waves. 5 of 6 waves contain more than one independent unit and the widest holds 5, so the schedule is bounded below by a chain of 6 and not by the size of the estate. Adding people shortens this plan; adding people to the critical path does not.

## 10. Summary

| id | prediction | verdict |
|---|---|---|
| P1 | The unportable APIs are at the edge of the system. | CONTRADICTED |
| P2 | Reading the manifest is enough. | CONTRADICTED |
| P3 | There is no migration order. | HELD |
| P4 | The cycles are a local problem. | HELD |
| P5 | An assembly cycle means the code is entangled. | CONTRADICTED |
| P6 | The heuristic is suboptimal somewhere in this estate. | CONTRADICTED |
| P7 | Breaking a cycle means removing a dependency. | CONTRADICTED |
| P8 | Static reachability finds the dead code. | CONTRADICTED |
| P9 | Reflection makes dead-code analysis worthless. | CONTRADICTED |
| P10 | Ranking assemblies by their own blocker count gives the order to work in. | CONTRADICTED |
| P11 | Missing source is an analysis problem. | CONTRADICTED |
| P12 | The plan parallelises. | HELD |

3 of 12 predictions held. The three that did are structural facts about the shape of the estate. Every one that did not makes the same mistake, and it is worth naming: **the assembly is the wrong unit.** Portability is a property of a member. Entanglement is a property of a type. Difficulty is a property of a migration unit. Deletability is a property of a type, qualified by a string constant. The assembly is what the build system happens to emit, and reports organised around it are wrong in the specific ways measured above.

The three findings that change what you would actually do:

- 10 assemblies are locked together by cycles, and 5 types decide it. Moving 7 types between project files linearises the entire build, and 1 unit needs no code change at all.
- Call-graph dead-code analysis would have deleted 2 live types; the sound analysis deletes 0; recovering one string constant from the IL makes the sound analysis delete 3.
- 21 of 24 assemblies depend on code nobody can rebuild, against 13 that contain a blocker. The binding constraint is provenance, not API surface.

Total blocker severity across the estate: 39 points over 17 call sites in 13 assemblies.

