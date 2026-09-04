# Interview Talking Points

## 1. What real problem does this solve?

It replaces spreadsheet-based access governance with executable lifecycle, approval, SoD, certification, reconciliation, and evidence workflows.

## 2. Why is the architecture non-trivial?

Effective access is a graph, not a row. A user can receive one entitlement through direct grants, a dynamic group, several nested roles, or JIT elevation. The system preserves every path while also applying contextual policies and exclusions.

## 3. What could fail?

- connector outage after internal revocation;
- stale manager/department data;
- toxic access hidden inside a role;
- missed certification deadline;
- expiry worker unavailable;
- broad deny or allow policy;
- target-side manual grant;
- attempted approval identity spoof;
- audit/database tampering.

## 4. How does it recover?

Bounded connector retries and quarantine, independent reconciliation, fail-closed end-time checks in the resolver, background plus manual deadline operations, audited workflow steps, and runbooks.

## 5. How is it secured?

JWT issuer/audience/signature/lifetime validation, scope policies, approver/JWT-subject binding, deny-wins policy, preventive/detective SoD, security headers, rate limiting, production default-key refusal, and append-only hash-linked audit.

## 6. How is it tested?

Pure domain tests pin graph/policy/state invariants. SQLite in-memory integration tests exercise full use cases with `FakeClock`, connector failure injection, and API 400/401/403 checks.

## 7. How is it observed?

Correlation IDs, structured logs, health probes, OpenTelemetry spans, authorization latency histogram, and pending request/active elevation/campaign completion gauges.

## 8. What trade-offs were made?

A modular monolith keeps governance changes and evidence transactionally coherent. SQLite and in-memory connectors make the project independently runnable. Synchronous simulation favors clarity over million-identity scale.

## 9. How would it scale?

Cache/version role graphs, batch subject derivations, queue simulation/campaign generation, partition by identity/application, use worker leases, dispatch provisioning through an outbox, and move to a managed relational database.

## 10. What changes in a real enterprise?

OIDC/JWKS and managed identities, vendor connectors, migrations/concurrency, immutable audit export, policy change governance, secrets management, asynchronous jobs, SIEM integration, and formal security/load testing.

## Deep-dive prompts

- Why explicit deny beats priority.
- Why campaign revocation uses an exclusion.
- Why role hierarchy stays a DAG rather than a flattened cache.
- How the resolver stays correct when the expiry worker is delayed.
- How reconciliation differs from retrying provisioning.
- Why an IGA product should not also become a home-grown OIDC provider.
