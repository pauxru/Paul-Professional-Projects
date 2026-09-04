# Portfolio Summary

## One-line description

A .NET 10 secrets lifecycle control plane that makes multi-consumer credential rotation a durable,
verifiable, rollback-safe workflow.

## Why this is technically interesting

The platform goes beyond encrypted CRUD. It models rotation as persisted choreography, binds
ciphertext to its identity with AES-GCM AAD, re-wraps per-version DEKs during master-key changes,
and prevents promotion until consumer and downstream evidence is sufficient. Failure paths are
first-class: missed acknowledgements and verification failures restore the prior version.

## Evidence in the repository

- Domain lifecycle and transition invariants.
- SQLite persistence and HTTP integration tests.
- Dual-write and maintenance-window strategies.
- Runtime generators for passwords, API keys, RSA/ECDSA pairs, certificates, connection strings,
  HMAC secrets, and encryption keys.
- JWT scopes, path RBAC, rate limits, audit/anomaly reports, and four-eyes break glass.
- Signed notification adapter with bounded retry and dead letter.
- Dashboard, consumer sample, operational runbooks, threat model, ADRs, and demo automation.

## Honest positioning

This is a self-directed engineering case study. It has not handled real credentials or production
traffic. Azure and Docker artifacts are design/compile artifacts and were not provisioned or run.
