# ADR-005 — DAG dependency support scope

- **Status:** Accepted
- **Date:** 2026-09
- **Context tags:** orchestration, scope, dependencies

## Context

Real pipelines have ordering: "run the export **after** the ETL succeeds", fan-out (one job triggers
several), and fan-in (a job waits for several predecessors). The brief asks for "dependency chains /
simple DAG support with cycle detection and fan-in/fan-out". The risk is scope creep into a
full workflow engine (dynamic branching, sub-DAGs, data passing, conditional edges) that would dwarf
the scheduler itself.

## Options considered

1. **No dependencies.** Simplest, but fails the requirement and real ETL use cases.
2. **Full workflow engine (Airflow/Argo-class).** Dynamic DAGs, XCom-style data passing, conditional
   branches, sub-workflows, backfills. Enormous surface; would eclipse the coordination headline and
   the zero-infra constraint.
3. **Static DAG by dependency edges, evaluated at completion (chosen).** Definitions declare
   `DependsOn` (job names). A job becomes eligible only when all its predecessors have **succeeded**
   within the same workflow (correlation) instance.

## Decision

Support **static DAGs declared as `DependsOn` name edges**, with:

- **Cycle detection** at definition time via `DagValidator.TopologicalOrder` (Kahn's algorithm);
  a cycle throws `DependencyCycleException` and the create/update is rejected (HTTP 400).
- **Fan-out / fan-in** naturally expressed: one predecessor with many dependents (fan-out); one
  dependent with many predecessors (fan-in). Fan-in readiness is a query over the workflow
  correlation id (`SucceededInWorkflowAsync`, backed by the `(CorrelationId, JobName, State)` index).
- **Completion-driven progression**: `DagOrchestrator.OnRunSucceededAsync` evaluates and enqueues
  now-eligible dependents when a run succeeds, tagged with the same correlation id so the workflow
  instance is coherent.

Explicitly **out of scope**: dynamic/conditional edges, data passing between jobs, sub-DAGs,
automatic backfill, and per-edge retry policies. These are documented as future work.

## Consequences

- **Positive:** Covers the common "B after A", fan-out and fan-in cases with a small, well-tested
  surface. Cycle detection prevents unschedulable definitions from ever being saved. Ordering is a
  data property (edges), not code.
- **Positive:** Tested by `DagValidatorTests` (topological order, cycle detection, fan-in/fan-out
  shapes) and dependency-resolver tests.
- **Negative:** No conditional branching or data flow — a genuine pipeline needing "run C only if B
  output > threshold" must encode that inside a handler, not the DAG.
- **Negative:** Dependencies are by **name within a correlation instance**; cross-instance or
  versioned-definition dependencies are not modelled.

## Risks & mitigations

- **Stuck workflow** (a predecessor never succeeds → dependents never run). Mitigation: dependents
  stay `Pending`/unmaterialised and are visible via the API; the predecessor's own retry/DLQ policy
  governs its fate; operators can trigger or cancel manually.
- **Cycle introduced by an update.** Mitigation: `DagValidator` runs on both create and update paths
  before persisting.
- **Fan-in correctness under retries.** Mitigation: readiness is "all predecessors **Succeeded** in
  this correlation", not "attempted"; a failed predecessor blocks progression until it succeeds or
  is resolved.

## Alternatives not chosen

A full workflow engine (out of proportion to the brief and the zero-infra constraint) or no
dependencies (fails the requirement). Static edges + cycle detection + completion-driven fan-in is
the deliberate "simple DAG" scope the brief calls for.
