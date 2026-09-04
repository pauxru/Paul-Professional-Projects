# ADR-005: Adapter-driven provider ingestion with local synthetic default

## Context
The project must demonstrate Azure and AWS ingestion design without provisioning or calling either platform.

## Options
1. Couple all ingestion to a cloud SDK.
2. Use provider-neutral rows only and hide mapping.
3. Own an `ICostDataSource` port with synthetic and provider-shaped file adapters.

## Decision
Application owns `ICostDataSource`. Infrastructure implements a deterministic synthetic source, Azure Cost Management export CSV mapping, and AWS CUR-like CSV mapping. The import service receives the neutral `CostImportLine`.

## Consequences
Tests exercise real column mappings and re-import semantics with no external infrastructure. A real API/export adapter can be substituted at composition time.

## Risks
The lightweight CSV parser is intentionally limited compared with production-grade provider schema evolution handling.

## Alternatives
Dockerised local cloud emulators were rejected because Docker is unavailable on the build host.
