# Demonstration Script

## Setup

```powershell
$env:NORTHSTAR_MASTER_KEY = "demo-only-not-a-real-secret-runtime-demo-key"
dotnet run --project src\Northstar.Secrets.Api
```

Open `http://localhost:5020` and keep the rotation dashboard visible. In a second terminal:

```powershell
.\scripts\demo.ps1
```

## Narrative

1. **Safety first:** point out the README warning, runtime generation, environment-supplied wrapping
   key, Production guard, and absence of real credentials.
2. **Registry:** show hierarchical metadata, type, owner, criticality, schedule, tags, consumers,
   current version, and `@secret:` reference.
3. **Happy-path rotation:** request dual-write. Show the operation pause in
   `AwaitingAcknowledgement`, the consumer pull result, acknowledgement, verification, and
   `Completed` state with v1 retained as `Previous`.
4. **Failure path:** the script registers a synthetic test record tagged `verification-fail`.
   Rotation reaches verification, rejects the candidate, and reports `RolledBack`; the prior
   current remains active.
5. **Cryptography:** explain that SQLite stores only ciphertext, nonce, tag, wrapped DEK, and key
   version. Show the AAD-transplant and re-wrap tests rather than exposing a value.
6. **Operations:** show upcoming expiry, acknowledgement status, anomaly report, dead letters, and
   the runbooks.
7. **Consumer integration:** open the compile-checked sample and show reference parsing, bounded
   in-memory caching, pull, refresh, and acknowledgement without printing the value.

## Close

Emphasize the trade-offs: local adapters and SQLite optimize reproducibility; a real deployment
would use OIDC, managed HSM/KMS, durable messaging, immutable audit export, and multi-worker leases.
