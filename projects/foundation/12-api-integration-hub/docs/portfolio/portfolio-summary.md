# Portfolio Summary

API Integration Hub is a self-directed engineering case study showing how to connect unreliable, incompatible enterprise APIs without hiding failure modes. It combines a versioned connector registry, declarative JSON/YAML flows, a sandboxed mapping DSL, contract-drift alerts, cron scheduling, encrypted secret references, and durable run/DLQ history.

The strongest implementation signals are:

- Four real pagination strategies tested against in-repository ASP.NET Core simulators.
- OAuth2 token caching and automatic refresh after 401.
- Retry with jitter and `Retry-After`, per-connector circuit breaker and bulkhead.
- At-least-once processing with persisted idempotency responses and per-record checkpoints.
- HMAC webhook verification and replay prevention.
- Secret and PII redaction proven in tests.
- A functional operator UI and documented incident/replay procedures.

This is not client work and makes no production-scale claim.
