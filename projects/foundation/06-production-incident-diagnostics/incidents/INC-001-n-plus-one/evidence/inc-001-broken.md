# INC-001 — N+1 EF Core query pattern (Broken)

- Started (UTC): 2026-09-02T22:33:33.3899166+00:00
- Requested operations: 20
- Elapsed: 2887.38 ms
- Completed within budget: True

## Metrics

- **sqlRoundTrips:** 21
- **ordersRead:** 20
- **shipmentsRead:** 20
- **roundTripsPerOrder:** 1.05
- **allocatedBytes:** 4550104

## Evidence

- EF DbCommandInterceptor captured 21 commands after seeding.
- First SQL command: SELECT "o"."Id", "o"."Reference" FROM "Orders" AS "o" ORDER BY "o"."Id"

## Limitations

- The lab deliberately uses explicit loop loading rather than lazy-loading proxies so the query count is deterministic.
