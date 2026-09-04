# INC-007 — Downstream timeout and request pile-up

## Symptoms
Outgoing calls remain in-flight, p95/p99 rises, and upstream requests accumulate while a dependency is slow. Without a client deadline, failures may only arrive after an outer gateway or user timeout.

## Business impact
A slow carrier, pricing, or tracking dependency can consume the API’s concurrency budget. The caller’s timeouts become a pile-up that spreads latency to otherwise healthy endpoints.

## Reproduction
```powershell
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-007 --mode broken --requests 20
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-007 --mode fixed --requests 20
```

## Telemetry & evidence
The in-process `HttpMessageHandler` delays each response 70 ms and observes cancellation. See [`evidence\inc-007-broken.json`](evidence/inc-007-broken.json) and [`evidence\inc-007-fixed.json`](evidence/inc-007-fixed.json).

| Run | Configured client timeout | Completed | Timed out | p50 | p95 | Peak in-flight |
|---|---|---:|---:|---:|---:|---:|
| Broken | none (global scenario budget only) | 20 | 0 | 78.43 ms | 87.24 ms | 6 |
| Fixed | 20 ms | 0 | 20 | 31.83 ms | 35.76 ms | 3 |

Harness excerpt:
```text
INC-007 Broken: 434.50 ms; evidence: ...\inc-007-broken.json
INC-007 Fixed: 381.65 ms; evidence: ...\inc-007-fixed.json
```

## Hypotheses considered and eliminated
- **A failed HTTP route:** the handler returns `200 OK` if allowed to finish.
- **Caller serialization:** only `HttpClient` latency and handler in-flight count change.
- **A hung suite:** every invocation inherits a scenario cancellation budget and the fixed client cancels at 20 ms.

## Root cause
Broken mode uses `Timeout.InfiniteTimeSpan` and waits for the full slow dependency delay. It gives no local deadline or cancellation pressure to reduce the in-flight pile-up.

## The fix
```diff
- client.Timeout = Timeout.InfiniteTimeSpan;
- await client.GetAsync(uri, ct);
+ client.Timeout = TimeSpan.FromMilliseconds(20);
+ await client.GetAsync(uri, ct); // handler observes cancellation
```

## Verification
The captured p95 changed from **87.24 ms to 35.76 ms**, and peak dependency pile-up fell from **6 to 3**. The expected fixed result is a controlled timeout (20 timeout responses), not a falsely successful response.

## Prevention
Set finite per-dependency timeouts, propagate cancellation through every async layer, record timeout/error/in-flight dimensions, use bulkheads where appropriate, and test the cancellation path—not just a fast happy path.

## Related failure modes
DNS stalls, socket/connect exhaustion, gateway deadline mismatch, retrying after caller cancellation, and a dependency returning partial slow responses can create similar pile-ups.
