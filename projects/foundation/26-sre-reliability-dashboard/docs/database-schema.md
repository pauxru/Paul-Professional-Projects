# Database Schema

SQLite is the default adapter. EF Core creates the schema with `EnsureCreated` in local Development; migration generation is a future improvement because schema evolution is not the instructional focus of this self-directed reference implementation.

```mermaid
erDiagram
  services ||--o{ slis : "serviceSlug (logical)"
  slis ||--o{ slos : "sliId"
  services ||--o{ metrics : "serviceSlug (logical)"
  slos ||--o{ alerts : "sloId"
  incidents ||--o{ postmortems : "incidentId"
  services {
    uuid id PK
    text slug UK
    text dependenciesJson
    int version
  }
  slis {
    uuid id PK
    text serviceSlug
    text filterJson
  }
  slos {
    uuid id PK
    uuid sliId
    decimal target
  }
  metrics {
    uuid id PK
    text serviceSlug
    datetime timestamp
    bigint requests
    bigint errors
    text latencyHistogramJson
  }
  alerts {
    uuid id PK
    uuid sloId
    text ruleName
    text payloadJson
  }
  maintenance_windows {
    uuid id PK
    text serviceSlug
    datetime startsAt
    datetime endsAt
  }
  incidents {
    uuid id PK
    datetime startedAt
    text payloadJson
  }
  postmortems {
    uuid id PK
    uuid incidentId
    text payloadJson
  }
```

## Tables and constraints

| Table | Primary key | Important columns | Indexes / constraints |
|---|---|---|---|
| `services` | `id` | catalogue metadata, `dependenciesJson`, `version` | unique `slug`; version concurrency token |
| `slis` | `id` | service, aggregation mode, kind, `filterJson`, latency threshold | unique `(serviceSlug, name)` |
| `slos` | `id` | `sliId`, service, target, window configuration | index `sliId`; unique `(serviceSlug, name)` |
| `metrics` | `id` | scope dimensions, aggregate counters, percentiles, histogram JSON | `(serviceSlug, timestamp)` |
| `alerts` | `id` | `sloId`, rule name, lifecycle payload | unique `(sloId, ruleName)` |
| `maintenance_windows` | `id` | service, start/end, payload | `(serviceSlug, startsAt, endsAt)` |
| `incidents` | `id` | start time, timeline/status/impacts payload | `startedAt` |
| `postmortems` | `id` | incident ID, review/actions/factors payload | `incidentId` |

Lifecycle aggregates (`alerts`, `incidents`, `postmortems`) are serialized deliberately because their nested timelines and action items are read and mutated atomically by this local prototype. Their top-level lookup keys remain indexed. A production reporting scale-out would normalize query-heavy child data or project it into a warehouse/read model.
