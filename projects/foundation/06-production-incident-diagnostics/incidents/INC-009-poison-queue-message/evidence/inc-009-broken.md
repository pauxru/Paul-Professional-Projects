# INC-009 — Poison queue message partition blockage (Broken)

- Started (UTC): 2026-09-02T22:34:23.6173916+00:00
- Requested operations: 20
- Elapsed: 58.59 ms
- Completed within budget: True

## Metrics

- **healthyMessagesProcessed:** 0
- **poisonDeliveries:** 12
- **redeliveries:** 12
- **deadLettered:** 0
- **remainingPartitionMessages:** 21
- **healthyThroughputPerSecond:** 0.00
- **brokenSafetyDeliveryBudget:** 12

## Evidence

- The poison item is reinserted at the head of one in-process partition, so it blocks later healthy messages.
- Broken mode is deliberately stopped after 12 deliveries; the production anti-pattern would redeliver indefinitely.

## Limitations

- This is an in-process queue model. Broker lock durations, partition rebalancing, and operational dead-letter tooling vary by messaging platform.
