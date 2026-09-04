# Throughput measurement

## Method

- Driver: `tests/NotificationPlatform.IntegrationTests/ThroughputDriver.cs`.
  Marked `[Fact(Skip = ...)]` so `dotnet test` does not include it by default;
  removing the `Skip` runs it under the same in-memory SQLite factory as the
  other integration tests.
- Workload: 1000 sequential Transactional Email sends against the
  `order.confirmation` template using `AlwaysSucceedProvider`s for every
  channel. Requests are `POST /api/v1/notifications` with a JWT.
- Environment: Windows, .NET SDK 10.0.400, in-memory SQLite (`:memory:`
  shared connection), single test host process. No provider I/O — the
  simulator returns synchronously.
- Timer: `System.Diagnostics.Stopwatch` around the request loop.

## Result

```
[ThroughputDriver] enqueued 1000 in 4.97s ≈ 201 req/s  (rows in DB=1000)
```

- **Ingestion rate: ~201 req/s** for a single-threaded client hitting the
  full HTTP + JWT + validation + suppression + preference + dedup + quota
  + persistence pipeline.
- The DB row count matches the request count exactly, so no notifications
  were dropped or deduplicated.

## What this number is not

- **Not** a production throughput claim. In-memory SQLite plus a
  single-thread client is the smallest possible harness.
- **Not** end-to-end throughput including provider I/O. The pipeline
  itself was measured separately in the fairness test.
- **Not** representative of real Postgres deployment. Postgres with the
  same schema and connection pooling would move the number substantially.

## Reproduce

```powershell
cd C:\Users\rukwaropaul\Downloads\DEV\Projects\11-notification-delivery-platform
# Temporarily flip the [Fact(Skip=...)] to [Fact] on ThroughputDriver
dotnet test tests\NotificationPlatform.IntegrationTests -c Release `
    --logger "console;verbosity=normal" `
    --filter "FullyQualifiedName~ThroughputDriver"
```

Numbers vary run-to-run by ~10-15%.
