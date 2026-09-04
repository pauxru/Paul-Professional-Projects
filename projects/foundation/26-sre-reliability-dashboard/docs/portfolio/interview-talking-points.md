# Interview Talking Points

## Why this architecture?
A modular monolith was the appropriate boundary: the hard problem is correctness of reliability decisions, not distributed deployment. Ports isolate the EF Core SQLite adapter and let the domain tests remain pure.

## What can fail at 3am?
Telemetry can be stale, a dependency can cascade, an alert can flap, a page can remain high after recovery, or delivery pressure can bypass budget policy. The design records data lag, uses two alert windows, suppresses duplicates under known conditions, tracks flapping, and gives delivery automation a gate response.

## What proves the SRE claim?
The SLO mathematics document derives each formula and the test suite uses hand-computed 99.9% fixtures. A 14.4× one-hour rate and 6× five-minute rate are independently asserted before a fast page fires.

## What would change in production?
Replace development tokens with OIDC/workload identity, move high-cardinality telemetry to Prometheus or a managed equivalent, add notification/paging adapters, durable audit history, migrations, reporting projections, and production incident exercises.
