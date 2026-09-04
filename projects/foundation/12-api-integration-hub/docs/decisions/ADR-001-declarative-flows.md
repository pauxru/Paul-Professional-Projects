# ADR-001 — Declarative flows over code-first integrations

## Context

Integration logic changes more often than the host. Operators need to inspect, version, activate, and roll back a flow without rebuilding the service.

## Options

1. Compile a class for every integration.
2. Embed a general workflow engine.
3. Persist a constrained declarative flow language interpreted by the hub.

## Decision

Use versioned JSON or constrained YAML definitions with a fixed step vocabulary: trigger, fetch, transform, filter, enrich, route, load, and respond. Definitions are validated before persistence and only an explicitly activated version can run.

## Consequences

Most mapping and routing changes become data changes with an audit-friendly version history. Execution remains observable at a stable step boundary. New step kinds require code.

## Risks

The format may evolve incompatibly or become an accidental general-purpose language. Versioning the schema and keeping the vocabulary narrow contain that risk.

## Alternatives

Code-first integrations remain appropriate for highly specialized algorithms. A commercial workflow engine would add visual tooling but also operational and licensing weight beyond this case study.
