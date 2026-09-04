# ADR-001: Data-Driven Rulesets vs Hard-Coded Matchers

## Status

Accepted, dated 2026-09.

## Context

ReconEngine must explain why a settlement line matched, and it must preserve the exact matching policy used for each run. The domain persists `MatchingRuleSet` rows with `Name`, `Version`, `IsActive`, `DefinitionJson`, and the computed `VersionTag` format `{Name}@v{Version}`. The payload is a serialized `MatchingRuleSetDefinition`, which enables or tunes concrete behaviours such as `ExactReferenceEnabled`, `CompositeDateWindowDays`, `AmountToleranceMinor`, `ManyToOneEnabled`, `SubsetSumMaxGroupSize`, `FeeAdjustedDateWindowDays`, and `RefundDateWindowDays`.

The matching pipeline emits `MatchCandidate` values. `ReconciliationOrchestrator.BuildMatches` persists each final `Match` with both `RuleId` and `RuleSetVersionTag`, so an audit reader can see that, for example, `many-to-one-subset-sum` under `default@v1` produced a match.

## Decision

Use persisted, immutable, versioned `MatchingRuleSet` entities whose `DefinitionJson` stores a `MatchingRuleSetDefinition`. Changes to matching behaviour are data changes that create a new ruleset version, not code edits to compiled matchers. Matching rules remain implemented in code, but enablement, tolerances, date windows, fee schedule, subset-sum caps, and canonicalisation settings are controlled by the ruleset definition.

Every persisted `Match` records the concrete `RuleId` and ruleset `VersionTag` used by the run.

## Options Considered

1. Data-driven, versioned ruleset definitions.
   - Pros: auditable `Name@vN` history; safe operational tuning; old runs remain explainable; defaults in `MatchingRuleSetDefinition` allow older serialized rulesets to keep loading.
   - Cons: less compile-time protection for configuration values; bad tuning can reduce match quality; schema evolution of JSON requires care.
2. Hard-code all matching thresholds in compiled rule classes.
   - Pros: strong type safety; simpler local reasoning; potentially easier micro-optimisation.
   - Cons: policy changes require deployment; historical runs are harder to reconstruct; multiple clients or profiles would create code branching.
3. External rule engine or DSL.
   - Pros: maximum business-user flexibility; complex conditional policies can be expressed without changing C#.
   - Cons: much larger runtime surface; harder testing and debugging; more difficult to keep performance predictable; unnecessary for the current six-rule pipeline.
4. Store only the active ruleset name, not an immutable version.
   - Pros: simpler database model.
   - Cons: audit ambiguity after edits; impossible to prove which tolerance produced a historical match.

## Consequences

Positive consequences:

- Rules can be toggled or tuned by changing `MatchingRuleSetDefinition` data while retaining deterministic code for the matching algorithms.
- `RuleId` plus `RuleSetVersionTag` makes match evidence durable in the `matches` table.
- Immutable ruleset versions align with immutable `ReconciliationRun` snapshots.
- Tests can exercise the same rule classes with different definitions.

Negative consequences:

- Validation must protect against unsafe values such as excessive subset-sum caps.
- JSON definitions are less discoverable than C# constants unless surfaced clearly through API contracts or documentation.
- Developers must handle backwards compatibility when new fields are added to `MatchingRuleSetDefinition`.

## Risks

- Risk: an operator activates a ruleset with too-wide tolerances and creates false positives. Mitigation: keep explicit confidence, explanation, `RuleId`, and `VersionTag` on each match; review run reports before adopting new active versions.
- Risk: JSON schema drift breaks old rulesets. Mitigation: maintain safe defaults in `MatchingRuleSetDefinition.Deserialize`.
- Risk: data-driven settings hide performance costs. Mitigation: retain hard-coded algorithmic guards such as subset-sum candidate and group-size caps.

## Alternatives

A reviewer might expect compiled strategies selected through dependency injection, one class per bank or feed. That was not chosen because the current engine already separates stable algorithms from variable policy: exact, composite, fuzzy amount/date, subset-sum, fee-adjusted, and refund rules stay in code, while dates, tolerances, enablement, and fee schedule live in versioned data. This keeps deployments for algorithm changes, not routine policy tuning.
