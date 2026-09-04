# Interview Talking Points

## What real problem does it solve?

It models a field-operations SaaS where several organizations share one application but must never share operational data. It also handles the commercial controls needed to sell and operate that application.

## Why is the architecture non-trivial?

Tenant selection can come from three sources, identities can belong to several organizations, permissions change while tokens remain valid, and platform admins legitimately need unfiltered views. Isolation therefore cannot rely on one endpoint predicate.

## What could fail?

- conflicting tenant sources;
- stale role cache;
- omitted repository filter;
- foreign detached entity write;
- quota race or rollover error;
- feature cache collision;
- forged/stale/replayed billing event;
- dunning that never recovers;
- attachment traversal.

## How does it recover?

It fails closed for tenant and signature errors, invalidates permission cache on role mutation, records unique webhook receipts, makes successful payment reset dunning and documents operational runbooks.

## How is it secured?

JWT/policies, membership verification, application tenant guards, EF filters, write interception, protected admin bypasses, tenant cache keys, HMAC webhooks, bounded inputs, security headers and append-only audit.

## How is it tested?

The strongest tests exercise the adversarial path: foreign job by ID is 404, foreign update is rejected, lists contain one tenant, and a repository deliberately bypassing filters still cannot save. The wider suite covers state machines, RBAC, quotas, flags, webhooks and API status/error contracts.

## How is it observed?

Correlation IDs, tenant logging scopes, OpenTelemetry spans/metrics, security-denial counters, health checks and searchable audit records.

## Main trade-off

A shared schema is cheaper and easier to operate but raises isolation risk. Multiple independent controls make that risk visible and testable. A real high-assurance deployment would add database row-level security or dedicated tenant placement.

## How would it scale?

Stateless API replicas, managed relational database, distributed cache/rate meter, cloud object storage and outbox-backed integration handling. Composite indexes already begin with tenant ID.

## What changes in an enterprise deployment?

OIDC/SCIM, managed secrets and key rotation, database migrations/RLS, SIEM/OTLP, WAF/private networking, malware scanning, approved platform elevation, billing reconciliation and formal security testing.

## Honesty statement

This is a self-directed case study, not client work. It has not served real users or money and makes no production scale, uptime or certification claim.
