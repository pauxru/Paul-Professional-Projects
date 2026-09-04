# INC-009 — Poison queue message partition blockage (Fixed)

- Started (UTC): 2026-09-02T22:35:20.8712642+00:00
- Requested operations: 20
- Elapsed: 19.78 ms
- Completed within budget: True

## Metrics

- **healthyMessagesProcessed:** 20
- **poisonDeliveries:** 3
- **redeliveries:** 2
- **deadLettered:** 1
- **remainingPartitionMessages:** 0
- **healthyThroughputPerSecond:** 1010.89
- **brokenSafetyDeliveryBudget:** 12

## Evidence

- The poison item is reinserted at the head of one in-process partition, so it blocks later healthy messages.
- Broken mode is deliberately stopped after 12 deliveries; the production anti-pattern would redeliver indefinitely.

## Limitations

- This is an in-process queue model. Broker lock durations, partition rebalancing, and operational dead-letter tooling vary by messaging platform.
