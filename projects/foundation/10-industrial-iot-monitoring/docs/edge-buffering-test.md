# Edge Buffering Test

## Scope
This document records real local deterministic test results for the gateway's SQLite store-and-forward invariant. It does not claim WAN performance or production durability under power loss beyond SQLite's normal local transaction semantics.

## Offline → online replay measurement

Command actually run on the Windows/.NET 10 host:

```powershell
dotnet test tests\Iiot.UnitTests\Iiot.UnitTests.csproj -c Release --no-restore `
  --filter "FullyQualifiedName~EdgeGateway_OfflineThenOnline" --logger "console;verbosity=normal"
```

The test produced 10 sequential readings while the cloud link was disabled. It then restored the link and flushed the SQLite queue through a sink that deduplicates `(deviceId, sequence)`.

| Measurement | Actual result |
|---|---|
| Records produced while offline | 10 |
| Durable queue depth before reconnect | 10 |
| Records delivered after reconnect | 10 |
| Duplicate effective deliveries | 0 |
| Delivery ordering | preserved: sequences 0 through 9 |
| Final queue depth | 0 |

The test's measurement line is:

```text
Edge buffering measurement: produced=10, replayed=10, duplicate deliveries=0, order=preserved, final buffer depth=0.
```

## Capacity/eviction measurement
`EdgeGateway_CapEviction_DropsOldestBufferedMessages` used a capacity of 3 and produced sequences 0–4 while offline. The resulting queue contained exactly sequences **2, 3, 4**. This proves the configured oldest-first eviction policy rather than unbounded local growth.

## Failure recovery measurement
`EdgeGateway_CloudFailure_BuffersThenRecovers` made the cloud sink throw on a live send. The gateway marked the cloud link unreachable and buffered the current reading. After the sink recovered and the link flag was restored, a flush delivered the reading and reduced queue depth from 1 to 0.

## Exactly-once-effective boundary
The gateway intentionally provides at-least-once delivery. It may resend after a network failure that occurs after the API commits but before the receipt arrives. Cloud uniqueness on `(deviceId, sequence)` turns that retry into a duplicate receipt, which the gateway treats as safe acknowledgement before deleting its local queue record.
