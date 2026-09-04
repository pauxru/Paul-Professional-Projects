# ADR-0002 — Declarative, versioned rules for reproducibility

**Status:** Accepted
**Date:** 2026-01-15

## Context

Analysts and risk officers must be able to explain **exactly** why a given transaction was
declined, six months after the fact, during a chargeback dispute or a regulator visit. This requires:

- A **stable, versioned representation** of the rules that were in force at decision time.
- A pure, deterministic evaluation function so that re-scoring reproduces the historical decision.
- The ability to **replay** a labelled dataset through any historical version to compute
  precision / recall for that version.

## Decision

Rules are **declarative records** (`RuleDefinition`) grouped into a versioned `RulesetDefinition`.
The definition is JSON-serialised to `Ruleset.DefinitionJson` and immutable once activated. A
`RulesetSerializer` gives strict round-tripping. The scoring service persists the exact
`RulesetVersion` string with every `ScoringDecision`. `ScoringService.EvaluateOnly` is the pure
re-scoring path used by the replay / tuning code.

## Consequences

**Positive:**
- Reproducibility is a **tested invariant**, not a hope: `ScoringServiceTests.Score_SameTxnSameRuleset_ProducesSameScore`.
- The tuning recommender in `DetectionEvaluator.RecommendAsync` can compute a **projected impact**
  by replaying labelled history under a candidate ruleset.
- Champion-challenger (`ADR-0003` / shadow mode) is a natural extension: one live, one shadow, both
  versioned, decisions persisted with a `Shadow` flag.

**Negative:**
- Rule authoring is by JSON / configuration, not by writing C#. This is a deliberate trade — the
  price of the reproducibility invariant is that C# is the language of the *engine*, not the
  *content*.
- Every new rule kind requires a code change to the engine. This is fine because the taxonomy of
  fraud signals is small and well-understood; explosion of one-off rules is a governance failure,
  not a technical one.

## Alternatives considered

- **Compile-and-load a rule assembly** at runtime (e.g. Roslyn scripting). Rejected because it
  breaks reproducibility (the compiled assembly is not the same artefact as the source, and .NET
  runtime differences would change behaviour subtly across versions).
- **A dedicated rules DSL** (e.g. CEL, Drools). Rejected as over-engineered for a portfolio-scope
  project; the 17 rule kinds implemented cover the well-known fraud taxonomy and are much easier
  for a reviewer to read than a DSL grammar.

## Related

- `src/FraudPipeline.Domain/Rules/RulesetDefinition.cs`
- `src/FraudPipeline.Domain/Rules/DefaultRulesets.cs`
- `src/FraudPipeline.Application/Rules/RuleEngine.cs`
- Tests: `RuleEngineTests`, `ScoringServiceTests.Score_SameTxnSameRuleset_ProducesSameScore`.
