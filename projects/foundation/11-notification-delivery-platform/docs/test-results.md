# Test results

Real output from `dotnet test -c Release` on the build host
(Windows, .NET SDK 10.0.400).  Nothing here is invented.

## Build

```
dotnet build -c Release
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## Test summary

```
Passed!  - Failed:     0, Passed:    41, Skipped:     0, Total:    41, Duration: 113 ms - NotificationPlatform.UnitTests.dll (net10.0)
Skipped  - NotificationPlatform.IntegrationTests.ThroughputDriver.Enqueue_1000_Transactional_Emails [1 ms]  (driver, intentionally skipped)
Passed!  - Failed:     0, Passed:    28, Skipped:     1, Total:    29, Duration: 4 s   - NotificationPlatform.IntegrationTests.dll (net10.0)
```

### Aggregate

| Suite | Passed | Failed | Skipped | Total |
|---|---:|---:|---:|---:|
| NotificationPlatform.UnitTests | 41 | 0 | 0 | 41 |
| NotificationPlatform.IntegrationTests | 28 | 0 | 1 | 29 |
| **All** | **69** | **0** | **1** | **70** |

The one skipped test is the throughput driver
(`ThroughputDriver.Enqueue_1000_Transactional_Emails`); it is marked
`[Fact(Skip = "...")]` on purpose so `dotnet test` does not run it. It is
run manually to produce the numbers in `docs/throughput-test.md`.

## Unit-test coverage (by class)

- `TemplateEngineTests` — 9 tests (token substitution, dotted paths,
  conditionals, loops, HTML-escape by default, `raw:` marker, strict-mode
  rejection of unknown tokens, missing data, XSS).
- `LocaleResolverTests` — 5 tests (fallback chain `sw-KE → sw → en`,
  per-tenant default, invariant fallback, empty locale, unknown locale).
- `QuietHoursTests` — 5 tests (transactional bypass, deferral within same
  day, deferral across midnight, timezone independence, no window
  configured).
- `FairnessSchedulerTests` — 3 tests (round-robin across tenants,
  weight-driven skew, fairness under load).
- `BackoffPolicyTests` — 3 tests (monotone growth, jitter within bounds,
  cap enforcement).
- `DomainInvariantTests` — 10 tests (notification state machine happy
  path + illegal transitions, circuit breaker Closed → Open → HalfOpen →
  Closed, `Attempts` invariant, dedup-key invariant, etc.).
- `SecurityServiceTests` — 6 tests (webhook signature valid / invalid /
  expired / replayed, unsubscribe token valid / tampered / expired).

## Integration-test coverage (by class)

- `AuthAndValidationTests` — 4 tests (401 on missing token, 403 on wrong
  scope, 400 on invalid body, health endpoints).
- `NotificationSendTests` — 10 tests (happy path, list, get by id, bulk,
  bulk partial failure, quiet hours defer, opt-out suppression,
  suppression list block, idempotent replay, dedup window).
- `PipelineFailoverTests` — 3 tests (all classes) covering primary →
  secondary failover, permanent-failure classification, and dead-letter
  after max attempts + replay.
- `PipelineExtraTests` — 11 tests across `CircuitBreakerTests`,
  `FrequencyCapTests`, `QuotaTests`, `UnsubscribeTests`,
  `ReceiptWebhookTests`, and `DeliverySuccessTests`, covering the
  circuit-breaker trip via consecutive failures, frequency cap,
  monthly-quota hard block, signed unsubscribe token issue + redeem,
  webhook signature valid / invalid / replayed, and end-to-end delivery
  under both single- and multi-tenant load.

## Reproduce

```powershell
cd C:\Users\rukwaropaul\Downloads\DEV\Projects\11-notification-delivery-platform
dotnet build -c Release
dotnet test  -c Release
```
