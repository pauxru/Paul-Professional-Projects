# INC-008 — Cascading retry storm

## Symptoms
A downstream error causes its request rate to increase rather than decrease. Traces show retries at several layers, error logs repeat for one logical request, and a struggling dependency receives amplified load.

## Business impact
Retry amplification can turn a brief dependency blip into a broader outage. Carrier status or shipment updates may overwhelm the exact service needed to recover.

## Reproduction
```powershell
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-008 --mode broken --requests 20
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-008 --mode fixed --requests 20
```
`INC-008` intentionally caps the requested workload at 12 logical operations.

## Telemetry & evidence
See [`evidence\inc-008-broken.json`](evidence/inc-008-broken.json) and [`evidence\inc-008-fixed.json`](evidence/inc-008-fixed.json). `OutboundCallCounter` increments at the failing downstream boundary.

| Run (12 logical requests) | Actual downstream calls | Calls/logical request | Circuit rejections |
|---|---:|---:|---:|
| Broken | 324 | 27.00 | 0 |
| Fixed | 2 | 0.17 | 11 |

Counter/log excerpt:
```text
INC-008 Broken: 8.70 ms; ... actualDownstreamCalls=324
INC-008 Fixed: 29.84 ms; ... actualDownstreamCalls=2
```
Broken mode produces the expected `3 × 3 × 3 = 27` attempted downstream calls per logical request.

## Hypotheses considered and eliminated
- **A high real request rate:** the harness drives exactly 12 logical operations after the safety cap.
- **A client connection leak:** the downstream is an in-process failure function; the counter records calls directly.
- **A single retry owner:** the 27 calls/request prove three nested retry owners are active.

## Root cause
Gateway, service, and repository each retry three times. When the always-failing downstream exhausts at the innermost layer, it propagates and is retried twice more at each outer layer.

## The fix
```diff
- gateway Retry(3) -> service Retry(3) -> repository Retry(3)
+ one global two-call retry budget
+ circuit breaker opens after two failures
+ deterministic jittered 1-3 ms backoff before allowed retry
```

## Verification
Real calls changed from **324 to 2**, a **162×** whole-run reduction; broken mode’s measured amplification is **27×** over one attempt per logical request. The test requires at least 8× more broken calls than fixed and checks circuit rejections.

## Prevention
Assign retry ownership to one layer, retry only classified transient errors, cap total attempts by a request budget, add jitter, propagate deadlines, record attempts separately from logical requests, and test the breaker-open state.

## Related failure modes
Hedged requests, recursive fallback chains, retries after cancellation, retrying non-idempotent writes, and queue redelivery loops all amplify a failing dependency.
