# INC-009 — Poison queue message blocks a partition

## Symptoms
One message repeatedly fails, delivery count climbs, queue age grows, and later healthy messages on the same partition do not move. Operators may see a consumer “working” while useful throughput is zero.

## Business impact
Head-of-line blocking stops shipment updates behind one malformed or incompatible event. Restarting the consumer only restarts the loop unless the poison-message policy changes.

## Reproduction
```powershell
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-009 --mode broken --requests 20
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-009 --mode fixed --requests 20
```

## Telemetry & evidence
See [`evidence\inc-009-broken.json`](evidence/inc-009-broken.json) and [`evidence\inc-009-fixed.json`](evidence/inc-009-fixed.json).

| Run | Healthy processed | Poison deliveries | Redeliveries | Dead-lettered | Remaining partition messages |
|---|---:|---:|---:|---:|---:|
| Broken | 0 | 12 | 12 | 0 | 21 |
| Fixed | 20 | 3 | 2 | 1 | 0 |

Harness excerpt:
```text
INC-009 Broken: 58.59 ms; evidence: ...\inc-009-broken.json
INC-009 Fixed: 19.78 ms; evidence: ...\inc-009-fixed.json
```
Broken mode stops at a hard **12-delivery safety budget**. That is a safe representation of the production anti-pattern, which would otherwise redeliver forever.

## Hypotheses considered and eliminated
- **No healthy messages arrived:** 20 healthy messages are placed behind the poison item before consumption.
- **Consumer is simply slow:** useful throughput remains zero even though 12 deliveries complete.
- **The test hangs:** the delivery budget and cancellation token ensure termination.

## Root cause
The consumer puts the failed poison message back at the head of the same partition on every attempt, so it prevents later messages from being read.

## The fix
```diff
- queue.AddFirst(poisonMessage); // every failure, forever
+ if (message.Attempts >= 3)
+     deadLetter.Add(message);
+ else
+     queue.AddFirst(message);
```

## Verification
In the real run, fixed mode quarantined one poison message after **3** deliveries and processed all **20** healthy messages. Broken mode processed **0** healthy messages while consuming its safe 12-delivery budget.

## Prevention
Configure a bounded max-delivery count and DLQ, emit delivery-attempt/partition-age/healthy-throughput metrics, validate schema compatibility before rollout, and define a controlled replay owner for quarantined messages.

## Related failure modes
Schema/version incompatibility, malformed deserialization, permanent authorization failure, a non-idempotent handler that keeps failing, and partition hot spots can all masquerade as a poison item.
