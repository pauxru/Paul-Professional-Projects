# INC-001 — N+1 EF Core query pattern (Fixed)

- Started (UTC): 2026-09-02T22:34:34.3173033+00:00
- Requested operations: 20
- Elapsed: 2474.38 ms
- Completed within budget: True

## Metrics

- **sqlRoundTrips:** 1
- **ordersRead:** 20
- **shipmentsRead:** 20
- **roundTripsPerOrder:** 0.05
- **allocatedBytes:** 4335520

## Evidence

- EF DbCommandInterceptor captured 1 commands after seeding.
- First SQL command: SELECT "o"."Id", (     SELECT COUNT(*)     FROM "Shipments" AS "s"     WHERE "o"."Id" = "s"."OrderId") AS "ShipmentCount" FROM "Orders" AS "o" ORDER BY "o"."Id"

## Limitations

- The lab deliberately uses explicit loop loading rather than lazy-loading proxies so the query count is deterministic.
