# Architecture

Northstar Reliability Control Room is a modular monolith designed for local reproducibility. The application port (`IReliabilityStore`) prevents SLO logic from depending on EF Core; the SQLite adapter persists service, SLI/SLO, and metric rows while storing lifecycle-rich aggregates as JSON payloads.

```mermaid
sequenceDiagram
  participant S as Simulator / Ingest client
  participant A as API
  participant R as ReliabilityFacade
  participant D as Domain calculators
  participant DB as SQLite
  participant G as Deployment automation

  S->>A: POST /metrics/ingest
  A->>R: validate and ingest samples
  R->>DB: append metric aggregates
  G->>A: GET /gates/checkout/deploy
  A->>R: evaluate service report
  R->>DB: load SLO, SLI, samples
  R->>D: SLI -> budget -> burn
  D-->>R: status and policy decision
  R-->>A: allow / warn / deny
  A-->>G: machine-readable gate response
```

## Module boundaries

| Module | Responsibility | May reference |
|---|---|---|
| Domain | invariants, arithmetic, state machines | BCL only |
| Application | ports, simulation, orchestration, reports | Domain |
| Infrastructure | EF Core / SQLite adapter and mappings | Application |
| API | HTTP, JWT, configuration, dashboard, composition | Application + Infrastructure |

## Failure behavior

The application emits RFC 7807 responses. Domain rule violations return 422, missing resources return 404, input validation returns 400, scope failure returns 401/403, and fixed-window rate limiting returns 429. Alert suppression is explicit state rather than implicit disappearance.
