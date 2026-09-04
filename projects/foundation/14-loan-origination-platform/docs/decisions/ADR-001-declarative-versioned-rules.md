# ADR-001 — Use declarative, versioned credit rules

## Context
Credit policy changes more frequently than application code releases, and a later reviewer must reproduce the exact result an applicant received. A hard-coded `if` tree hides policy meaning in a deployment artifact.

## Options
1. Put every eligibility rule in C#.
2. Use an opaque external decision engine.
3. Store a constrained declarative ruleset with typed conditions and versioned outcomes.

## Decision
Use option 3. Rulesets are persisted as immutable JSON payloads keyed by ruleset ID and version. The engine supports all/any/not and typed comparisons over an explicit `ApplicantFacts` model, then records inputs, match result, rule version, outcome, and business reason.

## Consequences
Credit operations can test candidate rulesets with the what-if endpoint and explain historical results without loading current policy. The constrained grammar intentionally limits arbitrary expressions.

## Risks
Complex policies may outgrow the small grammar, and JSON authoring needs validation/governance. A malformed candidate is rejected by deterministic validation before publishing.

## Alternatives
A full rules DSL, DMN engine, or code-only policy could be introduced later behind the same evaluation port; neither is needed to demonstrate reproducibility here.
