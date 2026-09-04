# ADR-003 — Bind applications to immutable product and ruleset versions

## Context
Product terms and policy evolve. Re-evaluating a 2026 application with a 2028 rate or ruleset would corrupt the historical explanation.

## Options
1. Resolve the active product and ruleset every time an application is read.
2. Copy only current terms into the application without identifiers.
3. Persist immutable product/ruleset version references and a decision record snapshot.

## Decision
Use option 3. Application creation validates the selected product version and stores product/ruleset identifiers and versions. Screening persists a `DecisionRecord` containing facts, scorecard version, trace, and product/ruleset provenance.

## Consequences
Historical applications remain reproducible even after later versions are published. This adds payload duplication deliberately in exchange for auditability.

## Risks
Storage grows as schedules and traces are retained. A real lender would add retention, legal-hold, archival, and export policies.

## Alternatives
Event sourcing could preserve equivalent history, but adds infrastructure and operational complexity beyond this SQLite case study.
