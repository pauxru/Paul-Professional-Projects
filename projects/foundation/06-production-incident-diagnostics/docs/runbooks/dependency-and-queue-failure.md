# Runbook: Downstream, Retry, Queue, and Cache Failure

## Scope
Use for rising outbound duration, timeouts, retry load, queue age, repeated delivery, dead-letter growth, cache misses, or origin overload. Relevant lab incidents: `INC-007` through `INC-010`.

## Immediate safeguards
1. Stop amplification before optimizing: reduce retry attempts, enforce a deadline, open a breaker, pause noncritical consumers, or serve stale cache data if safe.
2. Measure logical requests separately from downstream attempts.
3. Keep failure payloads redacted and preserve a small correlation-ID sample.

## Diagnose
- **Timeouts:** compare request p95 with configured timeout, in-flight depth, and cancellation propagation.
- **Retries:** calculate downstream calls/logical request; identify every layer that owns retries.
- **Poison messages:** inspect delivery attempts, partition head-of-line blocking, DLQ depth, and replay owner.
- **Cache:** compare concurrent misses with origin loads for a key; inspect expiration synchronization.

## Mitigate
- Give every outbound call a finite timeout and propagate cancellation.
- Put retries in one owner, budget them, classify only transient errors, add jitter, and use a circuit breaker.
- Dead-letter after a bounded count, quarantine the payload, and require a conscious replay action.
- Coalesce cache refreshes per key, jitter TTLs, and consider stale-while-revalidate.

## Verification
Expect lower in-flight pile-up, a bounded call amplification factor, healthy queue throughput, and origin loads close to one per concurrent cache miss group. Observe the downstream while recovery happens—do not only inspect caller errors.

## Lab command
```powershell
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-008 --mode broken --requests 12
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-010 --mode fixed --requests 20
```
