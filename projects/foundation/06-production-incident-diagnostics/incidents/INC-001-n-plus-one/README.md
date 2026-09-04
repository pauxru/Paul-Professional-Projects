# INC-001 — N+1 EF Core query pattern

## Symptoms
An order-list endpoint has ordinary CPU and a plausible single-query duration, yet its database command rate rises almost one-for-one with returned orders. Traces show repeated shipment lookup spans beneath one request.

## Business impact
More round trips consume database capacity, enlarge tail latency, and make an apparently harmless pagination-size increase costly. At a logistics dispatch peak, that can slow order visibility and shipment support work.

## Reproduction
```powershell
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-001 --mode broken --requests 20
dotnet run --project src\Lab.Harness -c Release -- --scenario INC-001 --mode fixed --requests 20
```

## Telemetry & evidence
Real harness evidence is in [`evidence\inc-001-broken.json`](evidence/inc-001-broken.json) and [`evidence\inc-001-fixed.json`](evidence/inc-001-fixed.json).

| Run (20 orders) | EF commands | Commands/order | Allocated bytes |
|---|---:|---:|---:|
| Broken | 21 | 1.05 | 4,550,104 |
| Fixed | 1 | 0.05 | 4,335,520 |

Harness excerpt captured on this host:
```text
INC-001 Broken: 2887.38 ms; evidence: ...\inc-001-broken.json
INC-001 Fixed: 2474.38 ms; evidence: ...\inc-001-fixed.json
```
The interceptor’s first broken command selected orders; the remaining 20 shipment lookups are the counter delta. Wall-clock timing includes EF/SQLite startup, so command count is the decisive signal.

## Hypotheses considered and eliminated
- **A missing index:** not the primary cause; the counter changed with result cardinality before a plan change.
- **Slow serialization:** does not explain 21 database commands for 20 order records.
- **Network dependency latency:** no outbound dependency is involved in this workload.

## Root cause
The broken path reads orders and then executes an EF query for shipments inside the loop. It is an explicit form of lazy/loop loading, retained in the lab because it makes the command count deterministic.

## The fix
Project shipment count in the original query rather than loading each child collection separately:
```diff
- foreach (var order in orders)
-     await db.Shipments.Where(x => x.OrderId == order.Id).ToListAsync(ct);
+ await db.Orders.Select(x => new {
+     x.Id,
+     ShipmentCount = x.Shipments.Count
+ }).ToListAsync(ct);
```

## Verification
The real before/after command counts are **21 → 1** for the same 20-order workload, a 21:1 reduction. The incident test asserts broken mode makes at least ten times the commands of fixed mode and applies a hard cancellation deadline.

## Prevention
Review collection traversal inside request handlers, project only fields required by a list response, and add a command-interceptor regression test for high-cardinality endpoints. Alert on commands/request rather than only database CPU.

## Related failure modes
Unbounded `Include` cartesian expansion, repeated `SaveChanges`, per-row remote calls, and cache-miss fan-out can have similar “one request, many operations” signatures.
